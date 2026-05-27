using PeanutButter.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using MySqlConnector;

namespace mysql_runner;

public static class Program
{
    public static int Main(string[] args)
    {
        var opts = new Options(args);
        if (opts.ShowedHelp)
        {
            return 0;
        }

        if (!opts.IsValid)
        {
            return 2;
        }

        var connectionStringProvider = new ConnectionStringProvider(opts);
        var originalSettings = ApplyFastRestoreSettingsAsync(connectionStringProvider);
        HandleUserExit(originalSettings, connectionStringProvider);
        try
        {
            CreateDatabaseIfRequired(opts, connectionStringProvider);
            RunAllScriptFiles(opts, connectionStringProvider);
            return 0;
        }
        finally
        {
            RestoreSettings(connectionStringProvider, originalSettings);
        }
    }

    private static void HandleUserExit(
        MysqlRestorePerformanceState originalSettings,
        ConnectionStringProvider connectionStringProvider
        )
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            RestoreSettings(connectionStringProvider, originalSettings);
        };
    }

    private static MysqlRestorePerformanceState ApplyFastRestoreSettingsAsync(
        ConnectionStringProvider connectionStringProvider
    )
    {
        using var conn = OpenConnection(
            connectionStringProvider,
            true
        );
        return MysqlRestorePerformance.ApplyFastRestoreSettingsAsync(
            conn
        );
    }

    private static void RestoreSettings(
        ConnectionStringProvider connectionStringProvider,
        MysqlRestorePerformanceState originalSettings
    )
    {
        using var conn = OpenConnection(
            connectionStringProvider,
            true
        );
        MysqlRestorePerformance.RestoreSettingsAsync(
            conn,
            originalSettings
        );
    }

    private static MySqlConnection OpenConnection(
        ConnectionStringProvider connectionStringProvider,
        bool noDb = false
    )
    {
        var connectionString = noDb
            ? CreateNoDbConnectionString(connectionStringProvider)
            : connectionStringProvider.ConnectionString;
        var result = new MySqlConnection(
            connectionString
        );
        result.Open();
        return result;
    }

    private static string CreateNoDbConnectionString(
        ConnectionStringProvider connectionStringProvider
        )
    {
        var builder = new MySqlConnectionStringBuilder(
            connectionStringProvider.ConnectionString
        );
        builder.Database = "";
        return builder.ToString();
    }


    private static void CreateDatabaseIfRequired(
        Options options,
        ConnectionStringProvider connectionStringProvider)
    {
        var dbName = options.Database.Replace("'", "''");
        using var conn = OpenConnection(connectionStringProvider, true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select * from INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = '{dbName}';";
        using var reader = cmd.ExecuteReader();
        var exists = reader.Read();
        reader.Close();
        if (exists)
        {
            if (!options.OverwriteExisting)
            {
                return;
            }
        }

        if (exists)
        {
            cmd.CommandText = $"drop database `{dbName}`";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = $"create database `{dbName}`;";
        cmd.ExecuteNonQuery();
    }

    private static void RunAllScriptFiles(Options opts, ConnectionStringProvider connectionStringProvider)
    {
        opts.Files.ForEach((file, idx) =>
        {
            var info = new FileInfo(file);
            var readBytes = 0L;
            using var reader = new StatementReader(file, opts);
            string statement;
            using var disposer = new AutoDisposer();
            var conn = disposer.Add(ConnectionFactory.Open(connectionStringProvider.ConnectionString));
            var cmd = disposer.Add(conn.CreateCommand());
            while ((statement = reader.Next()) != null)
            {
                readBytes += reader.LastReadBytes;

                cmd.CommandText = $"{DISABLE_CONSTRAINTS}{Environment.NewLine}{statement}";
                LogStatement(opts.Verbose, opts.NoProgress, statement, readBytes, info.Length, idx,
                    opts.Files.Count);
                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (MySqlException ex)
                {
                    disposer.DisposeNow(cmd);
                    disposer.DisposeNow(conn);
                    conn = disposer.Add(ConnectionFactory.Open(connectionStringProvider.ConnectionString));
                    cmd = disposer.Add(conn.CreateCommand());

                    if (opts.StopOnError)
                    {
                        throw;
                    }

                    if (!opts.Verbose)
                    {
                        ClearProgress();
                        LogStatement(true, true, statement, readBytes, info.Length, idx, opts.Files.Count);
                    }

                    Console.WriteLine($"[FAIL] {ex.Message}");
                }
            }
        });
        ClearProgress();
    }

    private static void LogStatement(
        bool verbose,
        bool noProgress,
        string statement,
        long bytesReadSoFar,
        long totalExpectedBytes,
        int file,
        int fileCount)
    {
        if (verbose)
        {
            Console.WriteLine($"-----{Environment.NewLine}{statement}{Environment.NewLine}-----");
            return;
        }

        if (!noProgress)
        {
            ShowProgress(file, fileCount, bytesReadSoFar, totalExpectedBytes);
        }
    }

    private static int _lastProgressLength = 0;
    private static DateTime _started = DateTime.MinValue;
    private static DateTime _lastProgress = DateTime.MinValue;

    private static void ShowProgress(
        in int file,
        in int fileCount,
        in long bytesReadSoFar,
        in long totalExpectedBytes)
    {
        if (_started == DateTime.MinValue)
        {
            _started = DateTime.Now;
        }

        if ((DateTime.Now - _lastProgress).TotalSeconds < 1)
        {
            // don't report more than 1x per second
            return;
        }

        _lastProgress = DateTime.Now;

        var runTime = (decimal)((DateTime.Now - _started).TotalSeconds);
        var percentComplete = (100M * bytesReadSoFar) / totalExpectedBytes;
        var estimatedTotalTime = 100M * (runTime / percentComplete);
        var overwrite = new String(' ', _lastProgressLength);
        var message = $@"File {file + 1} / {
            fileCount
        }    {percentComplete:F1}%    ({
            HumanReadableTimeFor((int)runTime)
        } / {
            HumanReadableTimeFor((int)estimatedTotalTime)
        }  rem: {HumanReadableTimeFor((int)(estimatedTotalTime - runTime))})";
        _lastProgressLength = message.Length;
        Console.Out.Write($"\r{overwrite}\r{message}");
        Console.Out.Flush();
    }

    private static void ClearProgress()
    {
        var overwrite = new String(' ', _lastProgressLength);
        Console.Out.Write($"\r{overwrite}\r");
        Console.Out.Flush();
    }

    private static string HumanReadableTimeFor(
        int secondsRemaining)
    {
        var seconds = secondsRemaining % 60;
        var minutes = (secondsRemaining / 60) % 60;
        var hours = (secondsRemaining / 3600) % 3600;
        var parts = new List<string>();
        if (hours > 0)
        {
            parts.Add(hours.ToString());
            parts.Add(minutes.ToString("D2"));
        }
        else
        {
            parts.Add(minutes.ToString());
        }

        parts.Add(seconds.ToString("D2"));

        return string.Join(":",
            parts
        );
    }


    private const string DISABLE_CONSTRAINTS = @"
SET FOREIGN_KEY_CHECKS=0;
SET UNIQUE_CHECKS=0;
";
}