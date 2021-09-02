using System;
using System.Collections.Generic;
using NUnit.Framework;
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
        [Explicit("WIP: this is a bug that should be fixed!")]
        public void ShouldNotSplitOutCodeInBeginEndBlock()
        {
            // Arrange
            var someTrigger = @"
AFTER UPDATE ON some_table
FO EACH ROW
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
                .To.Equal(new[] { someTrigger });
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