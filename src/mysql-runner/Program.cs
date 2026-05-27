using PeanutButter.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        var characterSet = options.DatabaseCharacterSet.Replace(";", "").Replace("'", "''");
        var collation = options.DatabaseCollation.Replace(";", "").Replace("'", "''");
        cmd.CommandText = $"create database `{dbName}` CHARACTER SET {characterSet} collate {collation};";
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
            var connectionInfo = new ConnectionInfo(connectionStringProvider.ConnectionString);
            while ((statement = reader.Next()) != null)
            {
                readBytes += reader.LastReadBytes;
                if (statement.Contains("_binary '") || statement.Contains("_binary 0x"))
                {
                    ExecViaCli(statement, connectionInfo);
                    continue;
                }

                cmd.CommandText = statement;
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

    private static void ExecViaCli(string statementInLatin1, ConnectionInfo info)
    {
        var bytes = Encoding.Latin1.GetBytes(statementInLatin1);
        ExecuteViaCli(bytes, info);
    }

    private static void ExecuteViaCli(byte[] statementBytes, ConnectionInfo info)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mysql",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--default-character-set=binary");
        if (info.Host == "localhost")
        {
            psi.ArgumentList.Add("--host=127.0.0.1");
        }
        else
        {
            psi.ArgumentList.Add($"--host={info.Host}");
        }
        psi.ArgumentList.Add($"--port={info.Port}");
        psi.ArgumentList.Add($"--user={info.User}");

        if (!string.IsNullOrEmpty(info.Database))
        {
            psi.ArgumentList.Add(info.Database);
        }

        // Pass password via env var rather than --password=... to keep it out of process listings
        psi.EnvironmentVariables["MYSQL_PWD"] = info.Password;

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start mysql process");
        }

        // Write the statement bytes to stdin
        process.StandardInput.BaseStream.Write(statementBytes, 0, statementBytes.Length);

        // Ensure the statement is terminated with a semicolon and newline
        // (mysql CLI expects a terminator before it'll execute)
        var terminator = new byte[] { (byte)';', (byte)'\n' };
        process.StandardInput.BaseStream.Write(terminator, 0, terminator.Length);

        process.StandardInput.Close();

        // Read stderr in case of failure — must be read before WaitForExit
        // to avoid deadlock if stderr fills up its buffer
        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new MySqlCliException(
                $"mysql CLI exited with code {process.ExitCode}: {stderr.Trim()}",
                stderr,
                stdout,
                process.ExitCode
            );
        }
    }

    public class ConnectionInfo
    {
        public string Host { get; set; } = "localhost";
        public uint Port { get; set; } = 3306;
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string Database { get; set; } = "";

        public ConnectionInfo()
        {
        }

        public ConnectionInfo(
            string connectionString
        )
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            Host = builder.Server;
            Port = builder.Port;
            User = builder.UserID;
            Password = builder.Password;
            Database = builder.Database;
        }
    }

    public class MySqlCliException : Exception
    {
        public string StdErr { get; }
        public string StdOut { get; }
        public int ExitCode { get; }

        public MySqlCliException(string message, string stderr, string stdout, int exitCode)
            : base(message)
        {
            StdErr = stderr;
            StdOut = stdout;
            ExitCode = exitCode;
        }
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
}