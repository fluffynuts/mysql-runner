using System;
using System.Collections.Generic;
using System.IO;

namespace mysql_runner
{
    public class Options
    {
        public string Host { get; set; } = "localhost";
        public string User { get; set; } = "root";
        public string Password { get; set; }
        public uint Port { get; set; } = 3306;

        public List<string> Files { get; } = new List<string>();

        public bool Quiet { get; set; }
        public bool StopOnError { get; set; }
        public string Database { get; set; }

        public Options(string[] args)
        {
            Parse(args);
            Validate();
        }

        private void Parse(string[] args)
        {
            Action<Options, string> optionHandler = null;
            Action<Options> flagHandler = null;
            foreach (var arg in args)
            {
                if (OptionHandlers.TryGetValue(arg, out var thisOptionHandler))
                {
                    optionHandler = thisOptionHandler;
                    continue;
                }

                if (FlagHandlers.TryGetValue(arg, out var thisFlagHandler))
                {
                    flagHandler = thisFlagHandler;
                    continue;
                }

                if (optionHandler == null &&
                    flagHandler == null)
                {
                    AddFile(this, arg);
                    continue;
                }

                optionHandler?.Invoke(this, arg);
                flagHandler?.Invoke(this);
                optionHandler = null;
                flagHandler = null;
            }
        }

        private Dictionary<string, Action<Options>> FlagHandlers =
            new Dictionary<string, Action<Options>>()
            {
                ["-q"] = SetQuiet,
                ["-s"] = SetStopOnError
            };

        private static void SetStopOnError(Options obj)
        {
            obj.StopOnError = true;
        }

        private static void SetQuiet(Options obj)
        {
            obj.Quiet = true;
        }

        private Dictionary<string, Action<Options, string>> OptionHandlers =
            new Dictionary<string, Action<Options, string>>()
            {
                ["-u"] = SetUser,
                ["-p"] = SetPassword,
                ["-P"] = SetPort,
                ["-h"] = SetHost,
                ["-d"] = SetDatabase
            };

        private static void SetDatabase(Options arg1, string arg2)
        {
            arg1.Database = arg2;
        }

        private static void SetHost(Options arg1, string arg2)
        {
            arg1.Host = arg2;
        }

        private static void SetPort(Options arg1, string arg2)
        {
            if (uint.TryParse(arg2, out var port) &&
                port > 0 &&
                port < 32768)
            {
                arg1.Port = port;
                return;
            }

            throw new InvalidOperationException(
                $"'{arg2}' is not a valid port value"
            );
        }

        private static void SetPassword(Options arg1, string arg2)
        {
            arg1.Password = arg2;
        }

        private static void SetUser(Options arg1, string arg2)
        {
            arg1.User = arg2;
        }

        private void AddFile(Options arg1, string arg2)
        {
            if (!File.Exists(arg2))
            {
                throw new InvalidOperationException(
                    $"File not found: '{arg2}'"
                );
            }

            Files.Add(arg2);
        }

        private void Validate()
        {
            CheckSet("-u", User);
            CheckSet("-p", Password);
            CheckSet("-h", Host);
            CheckSet("-d", Database);
        }

        private void CheckSet(string arg, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Console.WriteLine($"Required argument missing: {arg}");
            }
        }
    }
}