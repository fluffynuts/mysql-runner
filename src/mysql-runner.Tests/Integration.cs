using System.Text;
using NUnit.Framework;
using PeanutButter.TempDb.MySql.Connector;
using PeanutButter.Utils;

namespace mysql_runner.tests;

[TestFixture]
public class Integration
{
    [Test]
    public void ShouldCompleteSimpleRestore()
    {
        // Arrange
        using var tempFile = new AutoTempFile(
            Encoding.UTF8.GetBytes(
                """
                create table foo (id int primary key, name text);
                """
            )
        );
        using var db = new TempDBMySql();
        // db.Execute(
        //     """
        //     create user 'sqltracking'@'127.0.0.1' identified with mysql_native_password by 'sqltracking';
        //     grant all privileges on *.* to 'sqltracking'@'127.0.0.1' with grant option;
        //     create user 'sqltracking'@'localhost' identified with mysql_native_password by 'sqltracking';
        //     grant all privileges on *.* to 'sqltracking'@'localhost' with grant option;
        //     flush privileges;
        //     """
        // );
        // Act
        Program.Main([
            "-h",
            "localhost",
            "-P",
            $"{db.Port}",
            "-u",
            "root",
            "-p",
            "root",
            "-d",
            "simple_db",
            tempFile.Path
        ]);

        // Assert
        db.SwitchToSchema("simple_db");
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select * from foo;";
        using var reader = cmd.ExecuteReader();
        Expect(reader.NextResult())
            .To.Be.False();
    }
}