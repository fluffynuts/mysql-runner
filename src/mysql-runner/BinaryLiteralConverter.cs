using System;
using System.Text;

namespace mysql_runner
{
    /// <summary>
    /// Converts <c>_binary 'literal'</c> occurrences in a mysqldump-style SQL
    /// statement to MySQL hex literals (<c>0xABCD...</c>), while ensuring the
    /// returned string is a proper Unicode string (text outside <c>_binary</c>
    /// regions is decoded as UTF-8).
    ///
    /// Why this exists: mysqldump output is UTF-8 with arbitrary-byte payloads
    /// embedded inside <c>_binary '...'</c> literals (for BLOB / GEOMETRY /
    /// VARBINARY columns). Those payloads are not valid UTF-8. So the file can
    /// be read neither as pure UTF-8 (blob bytes get replaced with U+FFFD,
    /// destroying them) nor passed wholesale to MySqlConnector as a
    /// Latin1-decoded string (the connector re-encodes the command text as
    /// UTF-8 on the wire, which doubles up high bytes and mangles real UTF-8
    /// text like emoji).
    ///
    /// The fix: neutralise blob payloads into ASCII hex *first*, then UTF-8
    /// decode everything that's left.
    ///
    /// Input contract: <c>statement</c> is a string where each <c>char</c>'s
    /// low 8 bits represent one byte from the dump file (i.e. it was read with
    /// <see cref="Encoding.Latin1"/>). High bits of each char must be zero.
    ///
    /// Output: a proper Unicode string suitable for assignment to
    /// <c>MySqlCommand.CommandText</c>. Blob regions appear as <c>0xHEX</c>
    /// literals; all other bytes are interpreted as UTF-8 and decoded into
    /// their real codepoints.
    /// </summary>
    public static class BinaryLiteralConverter
    {
        private const string Marker = "_binary";
        private const byte MarkerFirstByte = (byte)'_';

        // Strict UTF-8 decoder: throws on invalid sequences rather than
        // silently substituting U+FFFD. The caller has chosen "fail loud"
        // for malformed input, and silent text corruption is exactly the
        // class of bug this converter exists to prevent.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Number of <c>_binary '...'</c> literals converted in the most recent
        /// <see cref="Convert"/> call. Useful for sanity-checking against
        /// expectations on real dumps.
        /// </summary>
        [ThreadStatic]
        private static int _lastConvertCount;

        public static int LastConvertCount => _lastConvertCount;

        /// <summary>
        /// Returns the statement with every <c>_binary '...'</c> literal
        /// replaced by an equivalent <c>0xHEX</c> literal, and all other bytes
        /// decoded as UTF-8.
        /// </summary>
        /// <exception cref="BinaryLiteralParseException">
        /// Thrown if a <c>_binary '...'</c> literal is malformed (unterminated
        /// quote, truncated escape) or if non-blob bytes are not valid UTF-8.
        /// </exception>
        public static string Convert(string statement)
        {
            _lastConvertCount = 0;
            if (statement is null)
            {
                return null;
            }

            if (statement.Length == 0)
            {
                return statement;
            }

            // Materialise the underlying byte sequence. Each char's low 8 bits
            // is one file byte; high bits must be zero (caller's responsibility).
            var bytes = LatinCharsToBytes(statement);

            // Fast path: no _binary marker anywhere. The whole thing is just
            // UTF-8 file bytes, so a single decode suffices.
            if (IndexOfBinaryLiteralStart(bytes, 0) < 0)
            {
                return DecodeUtf8Slice(bytes, 0, bytes.Length, sourceOffset: 0);
            }

            var output = new StringBuilder(bytes.Length);
            var cursor = 0;
            while (cursor < bytes.Length)
            {
                var markerStart = IndexOfBinaryLiteralStart(bytes, cursor);
                if (markerStart < 0)
                {
                    // Tail: decode remainder as UTF-8.
                    if (cursor < bytes.Length)
                    {
                        output.Append(DecodeUtf8Slice(bytes, cursor, bytes.Length - cursor, cursor));
                    }
                    break;
                }

                // Decode the run of UTF-8 bytes that precedes this _binary literal.
                if (markerStart > cursor)
                {
                    output.Append(DecodeUtf8Slice(bytes, cursor, markerStart - cursor, cursor));
                }

                // Skip whitespace between marker and opening quote.
                var quoteStart = markerStart + Marker.Length;
                while (quoteStart < bytes.Length && IsAsciiWhitespace(bytes[quoteStart]))
                {
                    quoteStart++;
                }

                // IndexOfBinaryLiteralStart only returns hits where the suffix
                // is "<whitespace>*'", so this should always hold. Defensive:
                if (quoteStart >= bytes.Length || bytes[quoteStart] != (byte)'\'')
                {
                    // Shouldn't reach here; treat as no-match and advance.
                    output.Append((char)bytes[markerStart]);
                    cursor = markerStart + 1;
                    continue;
                }

                // Parse the quoted payload, emitting hex.
                if (quoteStart + 1 < bytes.Length && bytes[quoteStart + 1] == (byte)'\'')
                {
                    // Empty payload — emit X'' since 0x with no digits is a bareword.
                    output.Append("X''");
                    cursor = quoteStart + 2;
                }
                else
                {
                    output.Append("0x");
                    cursor = AppendHexForBinaryPayload(bytes, quoteStart + 1, output);
                }
                _lastConvertCount++;
            }

            return output.ToString();
        }

        /// <summary>
        /// Decodes <paramref name="count"/> bytes from <paramref name="bytes"/>
        /// starting at <paramref name="offset"/> as UTF-8, wrapping decode
        /// errors in <see cref="BinaryLiteralParseException"/> with the
        /// approximate byte offset from the start of the source.
        /// </summary>
        private static string DecodeUtf8Slice(byte[] bytes, int offset, int count, int sourceOffset)
        {
            try
            {
                return StrictUtf8.GetString(bytes, offset, count);
            }
            catch (DecoderFallbackException ex)
            {
                // ex.Index is relative to the slice; translate to absolute offset.
                var absoluteOffset = sourceOffset + ex.Index;
                throw new BinaryLiteralParseException(
                    $"Invalid UTF-8 sequence outside _binary literal: {ex.Message}",
                    absoluteOffset,
                    BuildSnippetFromBytes(bytes, absoluteOffset)
                );
            }
        }

        /// <summary>
        /// Reads the mysqldump-escaped payload starting at <paramref name="payloadStart"/>
        /// (the position immediately after the opening single quote), appending
        /// the equivalent hex bytes to <paramref name="output"/>. Returns the
        /// byte position immediately after the closing single quote.
        /// </summary>
        private static int AppendHexForBinaryPayload(byte[] bytes, int payloadStart, StringBuilder output)
        {
            var i = payloadStart;
            while (i < bytes.Length)
            {
                var b = bytes[i];
                if (b == (byte)'\'')
                {
                    return i + 1;
                }

                if (b == (byte)'\\')
                {
                    if (i + 1 >= bytes.Length)
                    {
                        throw new BinaryLiteralParseException(
                            "Truncated escape sequence at end of input",
                            payloadStart - 1,
                            BuildSnippetFromBytes(bytes, payloadStart - 1)
                        );
                    }

                    var decoded = DecodeEscape(bytes[i + 1]);
                    AppendHexByte(output, decoded);
                    i += 2;
                    continue;
                }

                AppendHexByte(output, b);
                i++;
            }

            throw new BinaryLiteralParseException(
                "Unterminated _binary literal: no closing quote found",
                payloadStart - 1,
                BuildSnippetFromBytes(bytes, payloadStart - 1)
            );
        }

        /// <summary>
        /// Decodes the byte following a backslash in a mysqldump-style escape.
        /// Recognised: \0 \b \t \n \r \Z \' \" \\. Anything else: the literal
        /// byte that followed the backslash (matches MySQL's own behaviour for
        /// unknown escapes — the backslash is dropped).
        /// </summary>
        private static byte DecodeEscape(byte next)
        {
            return next switch
            {
                (byte)'0' => 0x00,
                (byte)'b' => 0x08,
                (byte)'t' => 0x09,
                (byte)'n' => 0x0A,
                (byte)'r' => 0x0D,
                (byte)'Z' => 0x1A,
                (byte)'\'' => 0x27,
                (byte)'"' => 0x22,
                (byte)'\\' => 0x5C,
                _ => next,
            };
        }

        /// <summary>
        /// Finds the next byte position where a <c>_binary</c> marker appears
        /// that is (a) not part of a larger identifier and (b) followed by
        /// (optional ASCII whitespace then) a single quote. Case-insensitive
        /// on the marker. Returns -1 if not found.
        /// </summary>
        private static int IndexOfBinaryLiteralStart(byte[] bytes, int from)
        {
            var i = from;
            var lastPossible = bytes.Length - Marker.Length;
            while (i <= lastPossible)
            {
                // Quick first-byte test before doing the case-insensitive compare.
                if (bytes[i] != MarkerFirstByte)
                {
                    i++;
                    continue;
                }

                if (!MatchesMarkerIgnoreCase(bytes, i))
                {
                    i++;
                    continue;
                }

                // Reject in-word matches like `not_binary` or `_binaryx`.
                if (i > 0 && IsIdentifierByte(bytes[i - 1]))
                {
                    i++;
                    continue;
                }
                var afterMarker = i + Marker.Length;
                if (afterMarker < bytes.Length && IsIdentifierByte(bytes[afterMarker]))
                {
                    i++;
                    continue;
                }

                // Look ahead past whitespace for a quote.
                var probe = afterMarker;
                while (probe < bytes.Length && IsAsciiWhitespace(bytes[probe]))
                {
                    probe++;
                }

                if (probe < bytes.Length && bytes[probe] == (byte)'\'')
                {
                    return i;
                }

                i++;
            }

            return -1;
        }

        /// <summary>
        /// Tests whether the 7 bytes starting at <paramref name="start"/> equal
        /// <c>_binary</c> case-insensitively. Caller guarantees length suffices.
        /// </summary>
        private static bool MatchesMarkerIgnoreCase(byte[] bytes, int start)
        {
            // _binary
            // Marker[0] is '_' which we already checked at the caller.
            // ASCII case-fold: bit 0x20 toggles case for letters.
            return ToAsciiLower(bytes[start + 1]) == (byte)'b'
                && ToAsciiLower(bytes[start + 2]) == (byte)'i'
                && ToAsciiLower(bytes[start + 3]) == (byte)'n'
                && ToAsciiLower(bytes[start + 4]) == (byte)'a'
                && ToAsciiLower(bytes[start + 5]) == (byte)'r'
                && ToAsciiLower(bytes[start + 6]) == (byte)'y';
        }

        private static byte ToAsciiLower(byte b)
        {
            return (b >= (byte)'A' && b <= (byte)'Z') ? (byte)(b | 0x20) : b;
        }

        private static byte[] LatinCharsToBytes(string s)
        {
            var bytes = new byte[s.Length];
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c > 0xFF)
                {
                    // Caller is supposed to hand us a Latin1-decoded string.
                    // A codepoint outside that range means an upstream bug.
                    throw new BinaryLiteralParseException(
                        $"Input contains codepoint U+{(int)c:X4} at offset {i}; " +
                        "expected a Latin1-decoded string (all chars in 0x00-0xFF). " +
                        "Was the file opened with the wrong encoding?",
                        i,
                        BuildSnippetFromString(s, i)
                    );
                }
                bytes[i] = (byte)c;
            }
            return bytes;
        }

        private static void AppendHexByte(StringBuilder output, byte b)
        {
            output.Append(HexChars[b >> 4]);
            output.Append(HexChars[b & 0x0F]);
        }

        private static readonly char[] HexChars =
            { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };

        private static bool IsAsciiWhitespace(byte b)
        {
            return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
        }

        private static bool IsIdentifierByte(byte b)
        {
            return (b >= (byte)'a' && b <= (byte)'z')
                || (b >= (byte)'A' && b <= (byte)'Z')
                || (b >= (byte)'0' && b <= (byte)'9')
                || b == (byte)'_'
                || b == (byte)'$';
        }

        private static string BuildSnippetFromBytes(byte[] bytes, int offset)
        {
            const int window = 40;
            var start = Math.Max(0, offset - window);
            var end = Math.Min(bytes.Length, offset + window);
            // Render as Latin1 (lossless) so the snippet shows raw bytes
            // even when the surrounding text is invalid UTF-8.
            var sb = new StringBuilder(end - start);
            for (var i = start; i < end; i++)
            {
                sb.Append((char)bytes[i]);
            }
            return sb.ToString();
        }

        private static string BuildSnippetFromString(string s, int offset)
        {
            const int window = 40;
            var start = Math.Max(0, offset - window);
            var end = Math.Min(s.Length, offset + window);
            return s.Substring(start, end - start);
        }
    }

    public class BinaryLiteralParseException : Exception
    {
        public int Offset { get; }
        public string Snippet { get; }

        public BinaryLiteralParseException(string message, int offset, string snippet)
            : base($"{message} (at offset {offset}): {snippet}")
        {
            Offset = offset;
            Snippet = snippet;
        }
    }
}
