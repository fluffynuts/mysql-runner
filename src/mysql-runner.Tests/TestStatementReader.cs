using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using NUnit.Framework;
using static PeanutButter.RandomGenerators.RandomValueGen;
using NExpect;
using PeanutButter.Utils;
using static NExpect.Expectations;

namespace mysql_runner.tests
{
    [TestFixture]
    public class TestStatementReader
    {
        [Test]
        public void ShouldRetrieveSingleUnterminatedStatement()
        {
            // Arrange
            using var file = new AutoTempFile("select * from foo");
            using var sut = Create(file.Path);
            // Act
            var result = sut.ReadAllStatements();
            // Assert
            Expect(result)
                .To.Equal(new[] { "select * from foo" });
        }

        [Test]
        public void ShouldRetrieveASingleTerminatedStatement()
        {
            // Arrange
            using var file = new AutoTempFile("select * from foo;");
            using var sut = Create(file.Path);

            // Act
            var result = sut.ReadAllStatements();
            // Assert
            Expect(result)
                .To.Equal(new[] { "select * from foo;" });
        }

        [Test]
        public void ShouldRetrieveTwoTerminatedStatements()
        {
            // Arrange
            using var file = new AutoTempFile(@"
select * from foo;
select * from bar;
");
            using var sut = Create(file.Path);
            // Act
            var result = sut.ReadAllStatements();
            // Assert
            Expect(result)
                .To.Equal(new[]
                {
                    "select * from foo;",
                    "select * from bar;"
                });
        }

        [Test]
        public void ShouldRetrieveTwoTerminatedStatementsWithTerminatorInStrangePlace()
        {
            // Arrange
            using var file = new AutoTempFile(@"
select * from foo
;
select * from bar;
");
            using var sut = Create(file.Path);
            // Act
            var result = sut.ReadAllStatements();
            // Assert
            Expect(result)
                .To.Equal(new[]
                {
                    $"select * from foo{Environment.NewLine};",
                    "select * from bar;"
                });
        }

        [Test]
        public void ShouldNotSplitOutCodeInBeginEndBlock()
        {
            // Arrange
            var someTrigger = @"
AFTER UPDATE ON some_table
FOR EACH ROW
BEGIN
    IF NEW.`flag` <> OLD.`flag`
    THEN
        insert into `logs` (`message`) values ('flag changed');
    END IF
END
";
            using var file = new AutoTempFile(someTrigger);
            using var sut = Create(file.Path);

            // Act
            var result = sut.ReadAllStatements();
            // Assert
            Expect(result)
                .To.Contain.Only(1).Item(
                    () => string.Join("\n", result)
                );
            var sanitizedResult = string.Join(" ", result)
                .RegexReplace("\\s+", " ")
                .Trim();
            var sanitizedTrigger = someTrigger.RegexReplace(
                "\\s+", " "
            ).Trim();
            Expect(sanitizedResult)
                .To.Equal(sanitizedTrigger);
        }

        [Test]
        [Explicit("this may not be possible - see the integration test below")]
        public void ShouldIncorporateDelimiterStatementsInPairs()
        {
            // Arrange
            var someTrigger = @"
DELIMITER ;;
AFTER UPDATE ON some_table
FOR EACH ROW
BEGIN
    IF NEW.`flag` <> OLD.`flag`
    THEN
        insert into `logs` (`message`) values ('flag changed');
    END IF
END;;
DELIMITER ;
";
            using var file = new AutoTempFile(someTrigger);
            using var sut = Create(file.Path);

            // Act
            var result = sut.ReadAllStatements();
            // Assert
            Expect(result)
                .To.Contain.Only(1).Item(() => string.Join("\n---\n", result));
            var sanitizedResult = string.Join(" ", result)
                .RegexReplace("\\s+", " ")
                .Trim();
            var sanitizedTrigger = someTrigger.RegexReplace(
                "\\s+", " "
            ).Trim();
            Expect(sanitizedResult)
                .To.Equal(sanitizedTrigger);
        }

        [Test]
        [Explicit("looks like one can't have DELIMITER statements in commands - there is an open bug about this that was 'resolved', but perhaps not")]
        public void DelimitersInMySqlCommands()
        {
            // Arrange
            var sql = @"
DELIMITER ;;
select * from customers limit 10;;
DELIMITER ;
";
            // Act
            using var conn = new MySqlConnection(
                "SERVER=localhost; DATABASE=yumbi; UID=yumbidev; PASSWORD=yumbidev; POOLING=true;Allow User Variables=true; Connection Lifetime=600; Max Pool Size=50;"
            );
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Console.WriteLine(reader["id"]);
            }
            // Assert
        }

        private StatementReader Create(string filePath)
        {
            return new StatementReader(filePath);
        }
    }

    public static class StatementReaderExtensions
    {
        public static string[] ReadAllStatements(
            this StatementReader reader)
        {
            var result = new List<string>();
            var current = reader.Next();
            while (!(current is null))
            {
                result.Add(current);
                current = reader.Next();
            }

            return result.ToArray();
        }
    }
}