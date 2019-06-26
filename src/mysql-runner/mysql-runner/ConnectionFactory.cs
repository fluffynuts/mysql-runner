using MySql.Data.MySqlClient;

namespace mysql_runner
{
    public static class ConnectionFactory
    {
        public static MySqlConnection Open(string connectionString)
        {
            var result = new MySqlConnection(connectionString);
            result.Open();
            return result;
        }
    }
}