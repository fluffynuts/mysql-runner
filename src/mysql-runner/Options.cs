using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PeanutButter.Utils;

namespace mysql_runner
{
    public class Options
    {
        public string Host { get; set; } = "localhost";
        public string User { get; set; } = "root";
        public string Password { get; set; }
        public uint Port { get; set; } = 3306;
        public string Database { get; set; }
        public bool OverwriteExisting { get; set; }

        public List<string> Files { get; } = new();

        public bool Verbose { get; set; }
        public bool StopOnError { get; set; }
        public bool ShowedHelp { get; set; }

        public bool IsValid { get; set; }
        private bool ShouldPromptForPassword { get; set; }
        public bool NoProgress { get; set; }

        public Options(string[] args)
        {
            if (args.Length == 0)
            {
                ShowHelp(this);
                return;
            }

            Parse(args);
            if (ShowedHelp)
            {
                return;
            }

            Validate();
            if (IsValid && ShouldPromptForPassword)
            {
                PromptForPassword(this);
            }
        }

        private void Parse(string[] args)
        {
            Action<Options, string> optionHandler = null;
            foreach (var arg in args)
            {
                if (OptionHandlers.TryGetValue(arg, out var thisOptionHandler))
                {
                    optionHandler = thisOptionHandler;
                    continue;
                }

                if (_flagHandlers.TryGetValue(arg, out var thisFlagHandler))
                {
                    thisFlagHandler.Invoke(this);
                    continue;
                }

                if (optionHandler == null)
                {
                    AddFile(this, arg);
                    continue;
                }

                optionHandler?.Invoke(this, arg);
                optionHandler = null;
            }

            if (ShowedHelp)
            {
                return;
            }

            if (optionHandler != null)
            {
                throw new ArgumentException($"No option value set for {args.Last()}");
            }
        }

        private readonly Dictionary<string, Action<Options>> _flagHandlers =
            new()
            {
                ["-v"] = SetVerbose,
                ["-s"] = SetStopOnError,
                ["--help"] = ShowHelp,
                ["--prompt"] = SetShouldPromptForPassword,
                ["--no-progress"] = SetNoProgress,
                ["--overwrite-existing"] = SetOverwriteExisting
            };

        private static void SetOverwriteExisting(Options obj)
        {
            obj.OverwriteExisting = true;
        }

        private static void SetNoProgress(Options obj)
        {
            obj.NoProgress = true;
        }

        private static void SetShouldPromptForPassword(Options obj)
        {
            obj.ShouldPromptForPassword = true;
        }

        // adapted from https://stackoverflow.com/a/3404522/1697008
        private static void PromptForPassword(Options obj)
        {
            Console.Out.Write("Please enter password: ");
            var pass = "";
            do
            {
                var key = Console.ReadKey(true);
                // Backspace Should Not Work
                if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
                {
                    pass += key.KeyChar;
                    Console.Write("*");
                }
                else
                {
                    if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
                    {
                        pass = pass.Substring(0, (pass.Length - 1));
                        Console.Write("\b \b");
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        break;
                    }
                }
            } while (true);

            obj.Password = pass;
            Console.WriteLine("");
        }

        private static void SetStopOnError(Options obj)
        {
            obj.StopOnError = true;
        }

        private static void SetVerbose(Options obj)
        {
            obj.Verbose = true;
        }

        private Dictionary<string, Action<Options, string>> OptionHandlers =
            new Dictionary<string, Action<Options, string>>()
            {
                ["-u"] = SetUser,
                ["-p"] = SetPassword,
                ["-P"] = SetPort,
                ["-h"] = SetHost,
                ["-d"] = SetDatabase,
            };

        private static void ShowHelp(Options arg1)
        {
            new[]
            {
                "MySql Runner",
                "Usage: mysql-runner {options} <file.sql> {<file.sql>...}",
                "  where options are of:",
                "  -d {database}         set database (no default)",
                "  -h {host}             set database host (defaults to localhost)",
                "  --no-progress         disable progress on quiet operation",
                "  --overwrite-existing  overwrite any existing target schema, if found",
                "  -p {password}         set password to log in with (defaults empty)",
                "  --prompt              will prompt for password",
                "  -P {port}             set port (defaults to 3306}",
                "  -s                    stop on error (defaults to carry on)",
                "  -u {user}             set user to log in with (defaults to root)",
                "  -v                    verbose operations (echo statements)"
            }.ForEach(line =>
            {
                Console.WriteLine(line);
            });
            arg1.ShowedHelp = true;
        }

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
            IsValid = true;
            CheckSet("-u", User);
            if (!ShouldPromptForPassword)
            {
                CheckSet("-p", Password);
            }

            CheckSet("-h", Host);
            CheckSet("-d", Database);
            if (Files.IsEmpty())
            {
                Console.WriteLine("No files specified");
                IsValid = false;
            }
        }

        private void CheckSet(string arg, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Console.WriteLine($"Required argument missing: {arg}");
                IsValid = false;
            }
        }
    }
}