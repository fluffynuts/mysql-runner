using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MySqlConnector;
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
        Expect(reader.Read())
            .To.Be.False();
    }

    [Test]
    public void ShouldSideloadBinaryDataStatements()
    {
        var geomBytes = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // SRID 0
            0x01, // byte order: little-endian
            0x01, 0x00, 0x00, 0x00, // type: Point
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, // X = 1.0
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, // Y = 2.0
        };

        // Build the file bytes piece-by-piece so the blob bytes go in literally,
        // not via any string-encoding round-trip that would mangle high bytes.
        using var fileStream = new MemoryStream();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        void WriteText(string s) => fileStream.Write(utf8.GetBytes(s));
        void WriteBytes(byte[] b) => fileStream.Write(b);

        WriteText("create table test_geom (id int, name text character set utf8mb4, shape GEOMETRY not null);\n");
        WriteText("INSERT INTO test_geom (id, name, shape) VALUES (1, '😄', _binary '");
        WriteBytes(EscapeBinaryForSqlLiteralBytes(geomBytes)); // see below
        WriteText("');\n");

        using var db = new TempDBMySql();
        using var tempFile = new AutoTempFile(fileStream.ToArray());

        Program.Main([
            "-h", "localhost",
            "-P", $"{db.Port}",
            "-u", "root",
            "-p", "root",
            "-d", "simple_db",
            "--overwrite-existing",
            tempFile.Path
        ]);

        // ... assertions unchanged ...
    }
    
    static byte[] EscapeBinaryForSqlLiteralBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes.Length * 2);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case 0x00:
                    ms.WriteByte(0x5C);
                    ms.WriteByte((byte)'0');
                    break; // \0
                case 0x27:
                    ms.WriteByte(0x5C);
                    ms.WriteByte(0x27);
                    break; // \'
                case 0x5C:
                    ms.WriteByte(0x5C);
                    ms.WriteByte(0x5C);
                    break; // \\
                case 0x0A:
                    ms.WriteByte(0x5C);
                    ms.WriteByte((byte)'n');
                    break; // \n
                case 0x0D:
                    ms.WriteByte(0x5C);
                    ms.WriteByte((byte)'r');
                    break; // \r
                case 0x1A:
                    ms.WriteByte(0x5C);
                    ms.WriteByte((byte)'Z');
                    break; // \Z
                default: ms.WriteByte(b); break;
            }
        }

        return ms.ToArray();
    }

    [Test]
    public void ShouldSideloadBinaryDataStatements_()
    {
        // Construct a minimal valid geometry: POINT(1.0, 2.0) with SRID 0
        var geomBytes = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // SRID 0
            0x01, // byte order: little-endian
            0x01, 0x00, 0x00, 0x00, // type: Point
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, // X = 1.0
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, // Y = 2.0
        };

        // Build the INSERT statement with the binary literal
        // SQL-escape the bytes: \, ', and 0x00 need backslash escaping
        var escapedLatin = EscapeBinaryForSqlLiteral(geomBytes);
        var sql = $"""
                   create table test_geom (id int, name text character set utf8mb4, shape GEOMETRY not null);
                   INSERT INTO test_geom (id, name, shape) VALUES (1, '😄', _binary '{escapedLatin}');
                   """;
        using var db = new TempDBMySql();
        using var tempFile = new AutoTempFile(
            Encoding.UTF8.GetBytes(sql)
        );
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
            "--overwrite-existing",
            tempFile.Path
        ]);

        db.SwitchToSchema("simple_db");
        using var conn = db.OpenConnection();
        // Verify row exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select count(*) from test_geom;";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Expect(count).To.Equal(1, "should have a row in there");
        }

        // Verify geometry
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select ST_AsText(shape) from test_geom where id = 1;";
            var asText = cmd.ExecuteScalar() as string;
            Expect(asText).To.Equal("POINT(1 2)");
        }

        // Verify emoji
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select name from test_geom where id = 1;";
            var name = cmd.ExecuteScalar() as string;
            Expect(name).To.Equal("😄");
        }
    }

    static string EscapeBinaryForSqlLiteral(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case 0x00: sb.Append("\\0"); break;
                case 0x27: sb.Append("\\'"); break; // '
                case 0x5C: sb.Append("\\\\"); break; // \
                case 0x0A: sb.Append("\\n"); break;
                case 0x0D: sb.Append("\\r"); break;
                case 0x1A: sb.Append("\\Z"); break;
                default: sb.Append((char)b); break; // Latin-1 character for byte
            }
        }

        return sb.ToString();
    }
}