using NUnit.Framework;
using System;
using System.Text;

namespace mysql_runner.tests;

[TestFixture]
public class TestBinaryLiteralConverter
{
    /// <summary>
    /// Helper: simulate the file-read pipeline. Takes a string of "what the
    /// dump file should contain conceptually" (which may include emoji etc.),
    /// encodes it as UTF-8 to get the on-disk bytes, then decodes those bytes
    /// as Latin1 — which is what StatementReader does. The result is a string
    /// where each char's low 8 bits represents one file byte: exactly the
    /// input contract for BinaryLiteralConverter.Convert.
    /// </summary>
    private static string AsReadFromFile(string conceptualContent)
    {
        var utf8 = Encoding.UTF8.GetBytes(conceptualContent);
        return Encoding.Latin1.GetString(utf8);
    }

    /// <summary>
    /// Helper: hex-encode bytes uppercase, no separators.
    /// </summary>
    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    [TestFixture]
    public class WhenNothingToConvert
    {
        [Test]
        public void ShouldReturnNullForNull()
        {
            Expect(BinaryLiteralConverter.Convert(null)).To.Be.Null();
        }

        [Test]
        public void ShouldReturnEmptyForEmpty()
        {
            Expect(BinaryLiteralConverter.Convert("")).To.Equal("");
        }

        [Test]
        public void ShouldPassPureAsciiThrough()
        {
            // Arrange
            var sql = AsReadFromFile("INSERT INTO `foo` (`name`) VALUES ('hello');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `foo` (`name`) VALUES ('hello');");
            Expect(BinaryLiteralConverter.LastConvertCount).To.Equal(0);
        }

        [Test]
        public void ShouldDecodeUtf8TextWithEmojiOutsideAnyBinaryLiteral()
        {
            // Arrange
            // Real dump scenario: emoji in a TEXT column.
            var sql = AsReadFromFile("INSERT INTO `t` (`name`) VALUES ('😄');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            // The emoji must come out as a real Unicode codepoint, not 4
            // separate Latin1 chars.
            Expect(result).To.Equal("INSERT INTO `t` (`name`) VALUES ('😄');");
            Expect(BinaryLiteralConverter.LastConvertCount).To.Equal(0);
        }

        [Test]
        public void ShouldNotMatchPartialWordEndingInBinary()
        {
            // Arrange
            var sql = AsReadFromFile("INSERT INTO `foo` (`col_not_binary`) VALUES ('hi');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `foo` (`col_not_binary`) VALUES ('hi');");
        }

        [Test]
        public void ShouldNotMatchBinaryFollowedByIdentifierChar()
        {
            // Arrange
            var sql = AsReadFromFile("INSERT INTO `foo` (`_binaryx`) VALUES ('hi');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `foo` (`_binaryx`) VALUES ('hi');");
        }

        [Test]
        public void ShouldNotMatchBinaryNotFollowedByQuote()
        {
            // Arrange
            // _binary as a word but no quote -> not a literal
            var sql = AsReadFromFile("-- the _binary type stores BLOBs");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("-- the _binary type stores BLOBs");
        }
    }

    [TestFixture]
    public class WhenConvertingSimpleAsciiPayload
    {
        [Test]
        public void ShouldConvertSingleBinaryWithAsciiPayload()
        {
            // Arrange
            var sql = AsReadFromFile("INSERT INTO `t` (`b`) VALUES (_binary 'abc');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `t` (`b`) VALUES (0x616263);");
            Expect(BinaryLiteralConverter.LastConvertCount).To.Equal(1);
        }

        [Test]
        public void ShouldConvertEmptyBinaryToZeroX()
        {
            // Arrange
            var sql = AsReadFromFile("INSERT INTO `t` (`b`) VALUES (_binary '');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `t` (`b`) VALUES (X'');");
        }

        [Test]
        public void ShouldHandleUppercaseMarker()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_BINARY 'A');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x41);");
        }

        [Test]
        public void ShouldHandleMixedCaseMarker()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_BiNaRy 'A');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x41);");
        }

        [Test]
        public void ShouldHandleMultipleSpacesBeforeQuote()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary   'A');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x41);");
        }

        [Test]
        public void ShouldHandleTabBetweenMarkerAndQuote()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary\t'A');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x41);");
        }
    }

    [TestFixture]
    public class WhenPayloadContainsEscapes
    {
        [TestCase("\\0", new byte[] { 0x00 }, TestName = "null byte")]
        [TestCase("\\b", new byte[] { 0x08 }, TestName = "backspace")]
        [TestCase("\\t", new byte[] { 0x09 }, TestName = "tab")]
        [TestCase("\\n", new byte[] { 0x0A }, TestName = "newline")]
        [TestCase("\\r", new byte[] { 0x0D }, TestName = "carriage return")]
        [TestCase("\\Z", new byte[] { 0x1A }, TestName = "ctrl-Z")]
        [TestCase("\\'", new byte[] { 0x27 }, TestName = "escaped single quote")]
        [TestCase("\\\"", new byte[] { 0x22 }, TestName = "escaped double quote")]
        [TestCase("\\\\", new byte[] { 0x5C }, TestName = "escaped backslash")]
        public void ShouldDecodeStandardEscape(string payload, byte[] expectedBytes)
        {
            // Arrange
            var sql = AsReadFromFile($"VALUES (_binary '{payload}');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal($"VALUES (0x{Hex(expectedBytes)});");
        }

        [Test]
        public void ShouldDecodeUnknownEscapeAsLiteralChar()
        {
            // Arrange
            // MySQL's behaviour for unknown escapes: drop the backslash, keep the char.
            var sql = AsReadFromFile("VALUES (_binary '\\q');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x71);");
        }

        [Test]
        public void ShouldHandleDoubleBackslashFollowedByEscapeChar()
        {
            // Arrange
            // Source bytes: \ \ n  -> escaped backslash (0x5C) then literal n (0x6E)
            var sql = AsReadFromFile("VALUES (_binary '\\\\n');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x5C6E);");
        }

        [Test]
        public void ShouldHandleEscapedQuoteInsidePayload()
        {
            // Arrange
            // Payload: it\'s -> 0x69 0x74 0x27 0x73; escaped quote does NOT terminate
            var sql = AsReadFromFile("VALUES (_binary 'it\\'s');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x69742773);");
        }
    }
    
    [Test]
    public void ShouldConvertEmptyBinaryToXQuoteQuote()  // renamed
    {
        var sql = AsReadFromFile("INSERT INTO `t` (`b`) VALUES (_binary '');");
        var result = BinaryLiteralConverter.Convert(sql);
        Expect(result).To.Equal("INSERT INTO `t` (`b`) VALUES (X'');");
    }

    [TestFixture]
    public class WhenPayloadContainsHighBytes
    {
        [Test]
        public void ShouldEmitHexForRawHighBytes()
        {
            // Arrange
            // Simulate a file containing raw bytes 0xEA, 0xA9, 'X' inside _binary '...'.
            // These bytes are NOT valid UTF-8 sequences on their own — but they're
            // inside a _binary literal, so the converter must process them as bytes
            // and never try to UTF-8 decode them.
            var fileBytes = new byte[]
            {
                // "VALUES (_binary '"
                0x56, 0x41, 0x4C, 0x55, 0x45, 0x53, 0x20, 0x28, 0x5F, 0x62, 0x69, 0x6E,
                0x61, 0x72, 0x79, 0x20, 0x27,
                // payload: EA A9 58
                0xEA, 0xA9, 0x58,
                // "');"
                0x27, 0x29, 0x3B,
            };
            var sql = Encoding.Latin1.GetString(fileBytes);
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0xEAA958);");
        }

        [Test]
        public void ShouldHandleRealGeometryStylePayload()
        {
            // Arrange
            // WKB-ish header bytes: 00 00 00 00 01 03 (00 written as \0 by mysqldump,
            // 01 and 03 as raw control bytes).
            // Build the file bytes directly to avoid C# string escaping confusion.
            var prefix = Encoding.UTF8.GetBytes("INSERT INTO `g` (`shape`) VALUES (_binary '");
            var payload = new byte[]
            {
                0x5C, 0x30, // \0
                0x5C, 0x30, // \0
                0x5C, 0x30, // \0
                0x5C, 0x30, // \0
                0x01,        // raw
                0x03,        // raw
            };
            var suffix = Encoding.UTF8.GetBytes("');");
            var fileBytes = new byte[prefix.Length + payload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, fileBytes, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, fileBytes, prefix.Length, payload.Length);
            Buffer.BlockCopy(suffix, 0, fileBytes, prefix.Length + payload.Length, suffix.Length);

            var sql = Encoding.Latin1.GetString(fileBytes);
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `g` (`shape`) VALUES (0x000000000103);");
        }
    }

    [TestFixture]
    public class WhenMultipleBinaryLiteralsPresent
    {
        [Test]
        public void ShouldConvertAllOccurrences()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary 'A', 1, _binary 'B');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x41, 1, 0x42);");
            Expect(BinaryLiteralConverter.LastConvertCount).To.Equal(2);
        }

        [Test]
        public void ShouldConvertThreeInOneStatement()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary 'A'), (_binary 'BC'), (_binary 'D');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x41), (0x4243), (0x44);");
            Expect(BinaryLiteralConverter.LastConvertCount).To.Equal(3);
        }

        [Test]
        public void ShouldHandleAdjacentLiteralsWithEscapes()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary 'a\\'b', _binary 'c\\'d');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x612762, 0x632764);");
        }
    }

    [TestFixture]
    public class WhenMixingUtf8TextAndBinaryLiterals
    {
        [Test]
        public void ShouldPreserveEmojiAlongsideBinaryLiteral()
        {
            // Arrange
            // The integration-test scenario, but as a unit test on the converter.
            // File on disk: UTF-8 bytes including the 4-byte emoji, plus a
            // _binary literal containing arbitrary bytes.
            var sql = AsReadFromFile(
                "INSERT INTO `t` (`a`, `b`) VALUES ('😄', _binary 'abc');"
            );
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            // Emoji is a real codepoint in the output; blob is hex.
            Expect(result).To.Equal(
                "INSERT INTO `t` (`a`, `b`) VALUES ('😄', 0x616263);"
            );
            // And when MySqlConnector re-encodes this as UTF-8 for the wire,
            // it produces the original 4-byte UTF-8 sequence for the emoji
            // (not the doubled 8-byte mojibake).
            var onWire = Encoding.UTF8.GetBytes(result);
            Expect(onWire).To.Contain.All.Of(new byte[] { 0xF0, 0x9F, 0x98, 0x84 });
        }

        [Test]
        public void ShouldHandleEmojiBeforeAndAfterBinaryLiteral()
        {
            // Arrange
            var sql = AsReadFromFile(
                "INSERT INTO `t` VALUES ('🎉', _binary 'X', '🚀');"
            );
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("INSERT INTO `t` VALUES ('🎉', 0x58, '🚀');");
        }

        [Test]
        public void ShouldHandleMultiByteUtf8CharImmediatelyBeforeBinaryRegion()
        {
            // Arrange
            // Boundary case: a 3-byte UTF-8 char (日) ends right before
            // ", _binary". The UTF-8 decode of the prefix slice must end
            // exactly at the codepoint boundary, not run past it.
            var sql = AsReadFromFile("VALUES ('日', _binary 'X');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES ('日', 0x58);");
        }

        [Test]
        public void ShouldHandleMultiByteUtf8CharImmediatelyAfterBinaryRegion()
        {
            // Arrange
            // Boundary case: the byte directly after a closing blob quote is
            // the lead byte of a multi-byte UTF-8 sequence.
            var sql = AsReadFromFile("VALUES (_binary 'X', '日');");
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0x58, '日');");
        }

        [Test]
        public void ShouldHandleHighBytesAtEndOfBlobPayload()
        {
            // Arrange
            // The blob ends with bytes that, if parsed naively as UTF-8,
            // would look like the start of a multi-byte sequence. The blob
            // parser is byte-level so it stops cleanly at the closing quote.
            var prefix = Encoding.UTF8.GetBytes("VALUES (_binary '");
            var payload = new byte[] { 0xF0, 0xF1, 0xF2 };
            var suffix = Encoding.UTF8.GetBytes("');");
            var fileBytes = new byte[prefix.Length + payload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, fileBytes, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, fileBytes, prefix.Length, payload.Length);
            Buffer.BlockCopy(suffix, 0, fileBytes, prefix.Length + payload.Length, suffix.Length);

            var sql = Encoding.Latin1.GetString(fileBytes);
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            Expect(result).To.Equal("VALUES (0xF0F1F2);");
        }

        [Test]
        public void ShouldHandleMultiByteUtf8TextWithRawBytesInBlobPayload()
        {
            // Arrange
            // The hardest case: UTF-8 text outside, raw non-UTF-8 bytes inside.
            // If the converter ever tried to UTF-8 decode the file as a whole,
            // the blob's raw bytes (e.g. 0xEA standalone) would be invalid UTF-8.
            // It must only decode the *non-blob* regions.
            var prefix = Encoding.UTF8.GetBytes("INSERT INTO `t` VALUES ('日本語', _binary '");
            var payload = new byte[] { 0xEA, 0xA9, 0xFF, 0x00, 0x01 };
            // 0x00 will appear as \0 in mysqldump output; build it with the escape:
            // Actually for this test let's bypass that and feed it as a raw 0x00.
            // mysqldump escapes 0x00 as \0, so let's go with that representation:
            payload = new byte[]
            {
                0xEA, 0xA9, 0xFF, // raw high bytes
                0x5C, 0x30,        // \0 (representing 0x00)
                0x01,              // raw 0x01
            };
            var suffix = Encoding.UTF8.GetBytes("');");
            var fileBytes = new byte[prefix.Length + payload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, fileBytes, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, fileBytes, prefix.Length, payload.Length);
            Buffer.BlockCopy(suffix, 0, fileBytes, prefix.Length + payload.Length, suffix.Length);

            var sql = Encoding.Latin1.GetString(fileBytes);
            // Act
            var result = BinaryLiteralConverter.Convert(sql);
            // Assert
            // Japanese text comes through as proper codepoints; blob bytes are hex.
            Expect(result).To.Equal("INSERT INTO `t` VALUES ('日本語', 0xEAA9FF0001);");
        }
    }

    [TestFixture]
    public class WhenInputIsMalformed
    {
        [Test]
        public void ShouldThrowOnUnterminatedLiteral()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary 'no closing quote");
            // Act / Assert
            Expect(() => BinaryLiteralConverter.Convert(sql))
                .To.Throw<BinaryLiteralParseException>()
                .With.Message.Containing("Unterminated");
        }

        [Test]
        public void ShouldThrowOnTruncatedEscape()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary 'oops\\");
            // Act / Assert
            Expect(() => BinaryLiteralConverter.Convert(sql))
                .To.Throw<BinaryLiteralParseException>()
                .With.Message.Containing("Truncated");
        }

        [Test]
        public void ShouldThrowOnInvalidUtf8OutsideBinaryLiteral()
        {
            // Arrange
            // Lone high byte outside any _binary region: not valid UTF-8.
            // The converter must fail loud rather than silently corrupting text.
            var fileBytes = new byte[]
            {
                0x53, 0x45, 0x4C, 0x45, 0x43, 0x54, 0x20, // "SELECT "
                0xC3,                                       // dangling UTF-8 lead byte
                0x3B,                                       // ";"
            };
            var sql = Encoding.Latin1.GetString(fileBytes);
            // Act / Assert
            Expect(() => BinaryLiteralConverter.Convert(sql))
                .To.Throw<BinaryLiteralParseException>()
                .With.Message.Containing("Invalid UTF-8");
        }

        [Test]
        public void ShouldThrowWhenInputContainsCodepointAboveLatin1Range()
        {
            // Arrange
            // The caller is supposed to give us a Latin1-decoded string (bytes-as-chars).
            // If they hand us a string containing a real high codepoint, that's a
            // pipeline bug and we should say so loudly.
            var sql = "SELECT '😄';"; // contains U+1F604, well above 0xFF
            // Act / Assert
            Expect(() => BinaryLiteralConverter.Convert(sql))
                .To.Throw<BinaryLiteralParseException>()
                .With.Message.Containing("Latin1-decoded");
        }

        [Test]
        public void ShouldIncludeOffsetAndSnippetInException()
        {
            // Arrange
            var sql = AsReadFromFile("VALUES (_binary 'unterminated");
            // Act / Assert
            try
            {
                BinaryLiteralConverter.Convert(sql);
                Assert.Fail("Expected exception");
            }
            catch (BinaryLiteralParseException ex)
            {
                Expect(ex.Offset).To.Be.Greater.Than(0);
                Expect(ex.Snippet).Not.To.Be.Null.Or.Empty();
                Expect(ex.Snippet).To.Contain("_binary");
            }
        }
    }

    [TestFixture]
    public class EndToEndWireBytes
    {
        /// <summary>
        /// The defining behavioural test: simulate the entire pipeline from
        /// dump file on disk to the bytes MySqlConnector would put on the wire,
        /// and verify the wire bytes equal what we expect the server to receive.
        /// </summary>
        [Test]
        public void ShouldProduceCorrectWireBytesForEmojiPlusBlob()
        {
            // Arrange — what's in the dump file on disk:
            var conceptual = "INSERT INTO `t` (`a`, `b`) VALUES ('😄', _binary 'abc');";
            var fileBytes = Encoding.UTF8.GetBytes(conceptual);

            // Simulate StatementReader: read as Latin1
            var statementAsLatin1 = Encoding.Latin1.GetString(fileBytes);

            // Act — what the converter produces, then what MySqlConnector
            // would put on the wire (UTF-8 encoding of CommandText)
            var converted = BinaryLiteralConverter.Convert(statementAsLatin1);
            var wireBytes = Encoding.UTF8.GetBytes(converted);

            // Assert — the wire bytes equal the original file bytes for the
            // text portions (emoji intact) and the blob portion is now hex.
            var expectedWire = Encoding.UTF8.GetBytes(
                "INSERT INTO `t` (`a`, `b`) VALUES ('😄', 0x616263);"
            );
            Expect(wireBytes).To.Equal(expectedWire);
        }

        /// <summary>
        /// Regression guard: the old Latin1-passthrough pipeline produced
        /// doubled high bytes for emoji. This test asserts the new pipeline
        /// does NOT have that bug.
        /// </summary>
        [Test]
        public void ShouldNotDoubleEncodeEmojiBytes()
        {
            // Arrange
            var fileBytes = Encoding.UTF8.GetBytes("VALUES ('😄');");
            var statementAsLatin1 = Encoding.Latin1.GetString(fileBytes);

            // Act
            var converted = BinaryLiteralConverter.Convert(statementAsLatin1);
            var wireBytes = Encoding.UTF8.GetBytes(converted);

            // Assert — the original 4-byte UTF-8 sequence for the emoji is
            // present on the wire, NOT the 8-byte doubled form.
            Expect(wireBytes).To.Contain.All.Of(new byte[] { 0xF0, 0x9F, 0x98, 0x84 });
            // And the buggy doubled sequence is not present:
            // 0xC3 0xB0 would be the start of the doubled form (U+00F0 as UTF-8).
            var doubled = new byte[] { 0xC3, 0xB0, 0xC2, 0x9F, 0xC2, 0x98, 0xC2, 0x84 };
            Expect(ContainsSequence(wireBytes, doubled)).To.Be.False(
                () => "Wire bytes contain the doubled-encoding regression pattern"
            );
        }

        private static bool ContainsSequence(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return false;
            }
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }
    }
}
