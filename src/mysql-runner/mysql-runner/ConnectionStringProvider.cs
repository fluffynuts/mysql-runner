using MySql.Data.MySqlClient;

namespace mysql_runner
{
    internal class ConnectionStringProvider
    {
        public string ConnectionString { get; }

        public ConnectionStringProvider(Options opts)
        {
            ConnectionString = new MySqlConnectionStringBuilder()
            {
                Server = opts.Host,
                UserID = opts.User,
                Password = opts.Password,
                Port = opts.Port,
                Database = opts.Database
            }.ToString();
        }
    }
}