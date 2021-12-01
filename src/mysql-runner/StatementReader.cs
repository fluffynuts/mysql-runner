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
        private readonly List<string> _parts = new List<string>();
        public long LastReadBytes { get; private set; }

        public StatementReader(string filePath)
        {
            _reader = new StreamReader(filePath);
        }

        private static readonly Regex DelimiterMatcher = new Regex("^\\s*DELIMITER\\s*([^\\s]*)");
        private const string DEFAULT_DELIMITER = ";";
        private string _currentDelimiter = DEFAULT_DELIMITER;
        private int _currentBlockLevel = 0;

        public string Next()
        {
            LastReadBytes = 0;
            lock (_parts)
            {
                _parts.Clear();
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

            if (line.StartsWith("--"))
            {
                return "";
            }

            var start = line.IndexOf("/*");
            if (start != 0)
            {
                // naive: assumes no mid-line comments; but this is how mysqldump makes it
                // also assumes no multi-line comments
                return line;
            }

            var end = line.LastIndexOf("*/");
            if (end == -1)
            {
                // broken? or perhaps multi-line (not catered for)
                return "";
            }

            return line.Substring(end + 2);
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

            var agnosticLast = last.Trim(';').Trim().ToLower();
            if (agnosticLast == "begin")
            {
                _currentBlockLevel++;
                return false;
            }

            if (agnosticLast == "end")
            {
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