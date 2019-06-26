using System;
using MySql.Data.MySqlClient;

namespace mysql_runner
{
    class Program
    {
        static void Main(string[] args)
        {
            var opts = new Options(args);
            var connectionStringProvider = new ConnectionStringProvider(opts);
            opts.Files.ForEach(file =>
            {
                using (var reader = new StatementReader(file))
                {
                    string statement;
                    while ((statement = reader.Next()) != null)
                    {
                        using (var conn = ConnectionFactory.Open(connectionStringProvider.ConnectionString))
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $"{DISABLE_CONSTRAINTS}{Environment.NewLine}{statement}";
                            LogStatement(opts, statement);
                            try
                            {
                                cmd.ExecuteNonQuery();
                            }
                            catch (MySqlException ex)
                            {
                                if (opts.StopOnError)
                                {
                                    throw;
                                }

                                Console.WriteLine($"[FAIL] {ex.Message}");
                            }
                        }
                    }
                }
            });
        }

        private static void LogStatement(Options opts, string statement)
        {
            if (opts.Quiet)
            {
                return;
            }

            Console.WriteLine($"-----{Environment.NewLine}{statement}{Environment.NewLine}-----");
        }

        private const string DISABLE_CONSTRAINTS = @"
SET FOREIGN_KEY_CHECKS=0;
SET UNIQUE_CHECKS=0;
";
    }
}