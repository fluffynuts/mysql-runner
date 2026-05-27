using System;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

public record MysqlRestorePerformanceState(
    int InnodbFlushLogAtTrxCommit,
    int SyncBinlog,
    int ForeignKeyChecks,
    int UniqueChecks
);

public static class MysqlRestorePerformance
{
    public static MysqlRestorePerformanceState ApplyFastRestoreSettingsAsync(
        MySqlConnection connection
    )
    {
        var original = ReadCurrentSettings(connection);

        Console.WriteLine(
            """
            Applying global settings to speed up restore:
            - innodb_flush_log_at_trx_commit:   2
            - sync_binlog:                      0
            - foreign_key_checks:               0
            - unique_checks:                    0
            """
        );
        ExecuteNonQuery(connection, "SET GLOBAL innodb_flush_log_at_trx_commit = 2");
        ExecuteNonQuery(connection, "SET GLOBAL sync_binlog = 0");
        ExecuteNonQuery(connection, "SET GLOBAL foreign_key_checks = 0");
        ExecuteNonQuery(connection, "SET GLOBAL unique_checks = 0");

        return original;
    }

    public static void RestoreSettingsAsync(
        MySqlConnection connection,
        MysqlRestorePerformanceState original,
        CancellationToken cancellationToken = default
    )
    {
        Console.WriteLine(
            $"""
             Restoring global settings:
             - innodb_flush_log_at_trx_commit:  {original.InnodbFlushLogAtTrxCommit}
             - sync_binlog:                     {original.SyncBinlog}
             - foreign_key_checks:              {original.ForeignKeyChecks}
             - unique_checks:                   {original.UniqueChecks}
             """
        );
        ExecuteNonQuery(connection,
            $"SET GLOBAL innodb_flush_log_at_trx_commit = {original.InnodbFlushLogAtTrxCommit}"
        );
        ExecuteNonQuery(connection, $"SET GLOBAL sync_binlog = {original.SyncBinlog}");
        ExecuteNonQuery(connection, $"SET GLOBAL foreign_key_checks = {original.ForeignKeyChecks}");
        ExecuteNonQuery(connection, $"SET GLOBAL unique_checks = {original.UniqueChecks}");
    }

    private static MysqlRestorePerformanceState ReadCurrentSettings(
        MySqlConnection connection
    )
    {
        const string sql =
            "SELECT " +
            "@@global.innodb_flush_log_at_trx_commit AS InnodbFlushLogAtTrxCommit, " +
            "@@global.sync_binlog AS SyncBinlog, " +
            "@@global.foreign_key_checks AS ForeignKeyChecks, " +
            "@@global.unique_checks AS UniqueChecks";

        using var command = new MySqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidOperationException("Failed to read current MySQL performance settings");
        }

        return new MysqlRestorePerformanceState(
            InnodbFlushLogAtTrxCommit: reader.GetInt32("InnodbFlushLogAtTrxCommit"),
            SyncBinlog: reader.GetInt32("SyncBinlog"),
            ForeignKeyChecks: reader.GetInt32("ForeignKeyChecks"),
            UniqueChecks: reader.GetInt32("UniqueChecks")
        );
    }

    private static void ExecuteNonQuery(
        MySqlConnection connection,
        string sql
    )
    {
        using var command = new MySqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}