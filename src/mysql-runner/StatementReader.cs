using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace mysql_runner
{
    public class StatementReader : IDisposable
    {
        private StreamReader _reader;
        private Options _opts;
        private readonly List<string> _parts = new List<string>();
        public long LastReadBytes { get; private set; }

        public StatementReader(string filePath, Options opts)
        {
            _reader = new StreamReader(filePath);
            _opts = opts;
        }

        private static readonly Regex DelimiterMatcher = new Regex("^\\s*DELIMITER\\s*([^\\s]*)");
        private const string DEFAULT_DELIMITER = ";";
        private string _currentDelimiter = DEFAULT_DELIMITER;
        private int _currentBlockLevel = 0;
        private int _lastBlockStartIndex = 0;
        private bool _openComment = false;

        public string Next()
        {
            LastReadBytes = 0;
            lock (_parts)
            {
                _parts.Clear();
                _lastBlockStartIndex = -1;
                do
                {
                    var line = _reader.ReadLine();
                    LastReadBytes += line?.Length ?? 0;
                    line = StripComments(line);
                    switch (line)
                    {
                        case null:
                            return Finalise();
                        case "":
                            continue;
                        default:
                            _parts.Add(line);
                            break;
                    }
                } while (!IsTerminated(_parts));

                return string.Join(Environment.NewLine, _parts);
            }

            string Finalise()
            {
                return _parts.Count > 0
                    ? string.Join(Environment.NewLine, _parts)
                    : null;
            }
        }

        private string StripComments(string line)
        {
            if (line == null)
            {
                return null;
            }

            var end = line.LastIndexOf("*/");
            if (_openComment)
            {
                if (end == -1)
                {
                    return "";
                }
                else
                {
                    _openComment = false;
                    return "";
                }
            }

            if (line.StartsWith("--"))
            {
                return "";
            }

            var start = line.IndexOf("/*");

            while (start > -1 && line.Length > start && _opts.IncludeMySqlSpecificComments)
            {
                //MySql specific comments
                if (line[start + 2] == '!')
                {
                    start = line.IndexOf("/*", start + 1);
                }
                //Valid comment
                else
                {
                    break;
                }
            }

            if (start != 0)
            {
                // naive: assumes no mid-line comments; but this is how mysqldump makes it
                return line;
            }

            if (end == -1)
            {
                //Start multi line comment
                _openComment = true;
                return "";
            }

            //Multi comments in a single line where the last comment is still open requires some recursion
            return StripComments(line.Substring(end + 2).Trim());
        }

        private bool IsTerminated(List<string> parts)
        {
            if (parts.Count == 0)
            {
                return false;
            }

            var last = parts.Last().Trim();
            if (last == "")
            {
                return false;
            }

            // Prevent double counting in case nothing was added to the parts
            if (_lastBlockStartIndex == parts.Count)
            {
                return false;
            }

            var agnosticLast = last.Trim(';').Trim().ToLower();
            if (agnosticLast == "begin")
            {
                _lastBlockStartIndex = parts.Count;
                _currentBlockLevel++;
                return false;
            }

            if (agnosticLast == "end")
            {
                _lastBlockStartIndex = parts.Count;
                _currentBlockLevel--;
                if (_currentBlockLevel < 0)
                {
                    Console.Error.WriteLine(
                        $"Achieved negative block level examining the last line of:\n----\n${string.Join("\n", parts)}\n"
                    );
                    _currentBlockLevel = 0;
                }

                if (_currentBlockLevel == 0)
                {
                    return true;
                }
            }

            if (_currentBlockLevel > 0)
            {
                return false;
            }

            if (last.Length < _currentDelimiter.Length)
            {
                return false;
            }

            var delimiterMatches = DelimiterMatcher.Match(last);
            var delimiter = delimiterMatches.Groups
                .OfType<Group>()
                .Skip(1)
                .FirstOrDefault()
                ?.Value;

            if (delimiter is not null)
            {
                _currentDelimiter = delimiter;
            }

            if (_currentDelimiter != DEFAULT_DELIMITER)
            {
                // in a magically-delimited block
                return false;
            }

            var lastNChars = last.Substring(last.Length - _currentDelimiter.Length);
            return lastNChars == _currentDelimiter;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }
    }
}