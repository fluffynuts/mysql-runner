using MySql.Data.MySqlClient;

namespace mysql_runner
{
    internal class ConnectionStringProvider
    {
        public string ConnectionString { get; }
        public string MasterConnectionString { get; set; }

        public ConnectionStringProvider(Options opts)
        {
            ConnectionString = new MySqlConnectionStringBuilder()
            {
                Server = opts.Host,
                UserID = opts.User,
                Password = opts.Password,
                Port = opts.Port,
                Database = opts.Database,
                AllowUserVariables = true,
                Pooling = true,
                MinimumPoolSize = 10,
                MaximumPoolSize = 50
            }.ToString();
            MasterConnectionString = new MySqlConnectionStringBuilder()
            {
                Server = opts.Host,
                UserID = opts.User,
                Password = opts.Password,
                Port = opts.Port,
                AllowUserVariables = true,
                Pooling = true,
                MinimumPoolSize = 10,
                MaximumPoolSize = 50
            }.ToString();
        }
    }
}