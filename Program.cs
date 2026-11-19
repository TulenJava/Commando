using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace Commando
{
    internal static class Program
    {
        // ----------------------------------------------------------------
        //  Состояние оболочки
        // ----------------------------------------------------------------
        private static string _currentDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static readonly List<string> _history = new();
        private static readonly Stack<string> _directoryStack = new();
        private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient _http = new();
        private static volatile bool _stopWatch = false;
        private static volatile bool _ctrlCPressed = false;
        private static Process? _activeChild;
        private static AppSettings _settings = new();
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Commando");
        private static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
        private static string HistoryFile => Path.Combine(SettingsDirectory, "history.txt");

        private sealed class AppSettings
        {
            public string Prompt { get; set; } = "> ";
            public bool ShowBanner { get; set; } = true;
            public bool ClearOnStart { get; set; } = true;
            public string DefaultShell { get; set; } = "cmd"; // cmd | powershell
            public string PowerShellHost { get; set; } = "auto"; // auto | pwsh | windows
            public string CtrlCAction { get; set; } = "cancel"; // cancel | exit
            public bool SaveHistory { get; set; } = false;
            public string Theme { get; set; } = "gray"; // gray | green | cyan | white
        }

        // ----------------------------------------------------------------
        //  Win32 P/Invoke
        // ----------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        // ================================================================
        //  ТОЧКА ВХОДА
        // ================================================================
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            Console.Title = "Windows Commando";
            LoadSettings();
            ApplyTheme();
            Console.CancelKeyPress += HandleCancelKeyPress;
            if (_settings.SaveHistory) LoadHistory();
            try { Console.BackgroundColor = ConsoleColor.Black; if (_settings.ClearOnStart) Console.Clear(); } catch { }

            // Installation is explicit: starting the shell does not silently modify PATH or the registry.

            // One-shot mode: commando /c "command"
            if (args.Length > 0 && (args[0] == "/c" || args[0] == "-c") && args.Length > 1)
            {
                Execute(string.Join(" ", args.Skip(1)));
                return 0;
            }

            if (_settings.ShowBanner) RenderStartupScreen();

            // Ctrl+C cancels the current input/child command; it does not terminate the shell by default.
            while (true)
            {
                ApplyTheme();
                Console.Write(_settings.Prompt);

                string? line;
                try { line = Console.ReadLine(); }
                catch (IOException) { if (_ctrlCPressed) { _ctrlCPressed = false; continue; } throw; }
                if (line == null)
                {
                    if (_ctrlCPressed) { _ctrlCPressed = false; continue; }
                    break;
                }
                line = line.Trim();
                if (line.Length == 0) { _ctrlCPressed = false; continue; }

                if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line, "quit", StringComparison.OrdinalIgnoreCase))
                    break;

                _history.Add(line);
                try { Execute(line); }
                catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
                finally { _ctrlCPressed = false; }
            }
            if (_settings.SaveHistory) SaveHistory();
            Console.CancelKeyPress -= HandleCancelKeyPress;
            return 0;
        }

        // ================================================================
        //  STARTUP HEADER
        // ================================================================
        private static void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            if (string.Equals(_settings.CtrlCAction, "exit", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;
            _ctrlCPressed = true;
            var child = _activeChild;
            if (child != null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            }
        }

        private static void RunChildProcess(ProcessStartInfo psi)
        {
            using var process = Process.Start(psi);
            if (process == null) { Console.WriteLine("Could not start process."); return; }
            _activeChild = process;
            try { process.WaitForExit(); }
            finally { _activeChild = null; }
        }

        private static void ApplyTheme()
        {
            Console.ForegroundColor = (_settings.Theme ?? "gray").ToLowerInvariant() switch
            {
                "green" => ConsoleColor.Green,
                "cyan" => ConsoleColor.Cyan,
                "white" => ConsoleColor.White,
                _ => ConsoleColor.Gray
            };
        }

        private static void LoadSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                if (File.Exists(SettingsFile))
                    _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
            }
            catch (Exception ex) { Console.WriteLine($"Settings could not be loaded; defaults used: {ex.Message}"); _settings = new AppSettings(); }
            NormalizeSettings();
        }

        private static void SaveSettings()
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Settings saved: {SettingsFile}");
        }

        private static void NormalizeSettings()
        {
            _settings.Prompt ??= "> ";
            _settings.DefaultShell ??= "cmd";
            _settings.PowerShellHost ??= "auto";
            _settings.CtrlCAction ??= "cancel";
            _settings.Theme ??= "gray";
            if (string.IsNullOrWhiteSpace(_settings.Prompt)) _settings.Prompt = "> ";
            if (!_settings.DefaultShell.Equals("powershell", StringComparison.OrdinalIgnoreCase)) _settings.DefaultShell = "cmd";
            if (!_settings.PowerShellHost.Equals("pwsh", StringComparison.OrdinalIgnoreCase) && !_settings.PowerShellHost.Equals("windows", StringComparison.OrdinalIgnoreCase)) _settings.PowerShellHost = "auto";
            if (!_settings.CtrlCAction.Equals("exit", StringComparison.OrdinalIgnoreCase)) _settings.CtrlCAction = "cancel";
            if (!_settings.Theme.Equals("green", StringComparison.OrdinalIgnoreCase) && !_settings.Theme.Equals("cyan", StringComparison.OrdinalIgnoreCase) && !_settings.Theme.Equals("white", StringComparison.OrdinalIgnoreCase)) _settings.Theme = "gray";
        }

        private static void LoadHistory()
        {
            try { if (File.Exists(HistoryFile)) _history.AddRange(File.ReadAllLines(HistoryFile).TakeLast(5000)); } catch { }
        }

        private static void SaveHistory()
        {
            try { Directory.CreateDirectory(SettingsDirectory); File.WriteAllLines(HistoryFile, _history.TakeLast(5000)); } catch (Exception ex) { Console.WriteLine($"History save failed: {ex.Message}"); }
        }

        private static void CmdConfig(string argument)
        {
            string[] a = SplitArgs(argument);
            if (a.Length >= 3 && a[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                string key = a[1].ToLowerInvariant();
                string value = argument[(argument.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) + a[1].Length)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                SetConfigValue(key, value);
                return;
            }
            if (a.Length >= 1 && a[0].Equals("show", StringComparison.OrdinalIgnoreCase)) { ShowConfig(); return; }
            if (a.Length >= 1 && a[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            { _settings = new AppSettings(); SaveSettings(); ApplyTheme(); Console.WriteLine("Settings reset to defaults."); return; }

            while (true)
            {
                Console.WriteLine("\nCOMMANDO SETTINGS");
                ShowConfig();
                Console.WriteLine("\n1 Prompt  2 Startup banner  3 Clear screen on start  4 Default shell");
                Console.WriteLine("5 PowerShell host  6 Ctrl+C behavior  7 Save history  8 Theme");
                Console.WriteLine("S Save    R Reset    Q Back");
                Console.Write("Setting> ");
                string? choice = Console.ReadLine();
                if (choice == null || choice.Equals("q", StringComparison.OrdinalIgnoreCase)) break;
                if (choice.Equals("s", StringComparison.OrdinalIgnoreCase)) { SaveSettings(); continue; }
                if (choice.Equals("r", StringComparison.OrdinalIgnoreCase)) { _settings = new AppSettings(); ApplyTheme(); continue; }
                Console.Write("Value> ");
                string? value = Console.ReadLine(); if (value == null) break;
                string key = choice.Trim() switch { "1" => "prompt", "2" => "banner", "3" => "clear", "4" => "shell", "5" => "powershell", "6" => "ctrlc", "7" => "history", "8" => "theme", _ => "" };
                if (key.Length == 0) { Console.WriteLine("Unknown setting."); continue; }
                SetConfigValue(key, value);
            }
        }

        private static void ShowConfig()
        {
            Console.WriteLine($"Prompt          = {_settings.Prompt}");
            Console.WriteLine($"Banner          = {_settings.ShowBanner}");
            Console.WriteLine($"ClearOnStart    = {_settings.ClearOnStart}");
            Console.WriteLine($"DefaultShell    = {_settings.DefaultShell}");
            Console.WriteLine($"PowerShellHost  = {_settings.PowerShellHost}");
            Console.WriteLine($"CtrlCAction     = {_settings.CtrlCAction}");
            Console.WriteLine($"SaveHistory     = {_settings.SaveHistory}");
            Console.WriteLine($"Theme           = {_settings.Theme}");
            Console.WriteLine($"Config file     = {SettingsFile}");
        }

        private static void SetConfigValue(string key, string value)
        {
            value = value.Trim();
            bool BoolValue() => value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "prompt": _settings.Prompt = value; break;
                case "banner": _settings.ShowBanner = BoolValue(); break;
                case "clear": _settings.ClearOnStart = BoolValue(); break;
                case "shell": if (value.Equals("cmd", StringComparison.OrdinalIgnoreCase) || value.Equals("powershell", StringComparison.OrdinalIgnoreCase)) _settings.DefaultShell = value.ToLowerInvariant(); else { Console.WriteLine("Use cmd or powershell."); return; } break;
                case "powershell": if (new[] { "auto", "pwsh", "windows" }.Contains(value.ToLowerInvariant())) _settings.PowerShellHost = value.ToLowerInvariant(); else { Console.WriteLine("Use auto, pwsh or windows."); return; } break;
                case "ctrlc": if (new[] { "cancel", "exit" }.Contains(value.ToLowerInvariant())) _settings.CtrlCAction = value.ToLowerInvariant(); else { Console.WriteLine("Use cancel or exit."); return; } break;
                case "history": _settings.SaveHistory = BoolValue(); break;
                case "theme": if (new[] { "gray", "green", "cyan", "white" }.Contains(value.ToLowerInvariant())) _settings.Theme = value.ToLowerInvariant(); else { Console.WriteLine("Use gray, green, cyan or white."); return; } break;
                default: Console.WriteLine("Unknown setting. Use config show."); return;
            }
            ApplyTheme();
            Console.WriteLine($"{key} updated. Use 'config' then S, or 'config save', to persist.");
        }

        private static void RenderStartupScreen()
        {
            if (_settings.ClearOnStart) { try { Console.Clear(); } catch { } }
            ApplyTheme();
            Console.WriteLine("Windows Commando 2.3.1.1");
            Console.WriteLine("Type help for a list of commands.");
            Console.WriteLine();
        }

        // ================================================================
        //  ДИСПЕТЧЕР КОМАНД
        // ================================================================
        private static void Execute(string line)
        {
            foreach (var kv in _aliases)
            {
                if (line.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) &&
                    (line.Length == kv.Key.Length || line[kv.Key.Length] == ' '))
                {
                    line = kv.Value + line[kv.Key.Length..];
                    break;
                }
            }

            var parts = SplitArgs(line);
            string cmd = parts[0].ToLowerInvariant();
            string[] rest = parts.Skip(1).ToArray();
            string restLine = line.Length > parts[0].Length ? line[parts[0].Length..].Trim() : "";

            switch (cmd)
            {
                case "help":        CmdHelp();                              return;
                case "config":     if (restLine.Equals("save", StringComparison.OrdinalIgnoreCase)) SaveSettings(); else CmdConfig(restLine); return;
                case "cd":          CmdCd(restLine);                        return;
                case "pushd":       CmdPushd(restLine);                      return;
                case "popd":        CmdPopd();                               return;
                case "set":         CmdSet(restLine);                        return;
                case "path":        CmdPath(restLine);                       return;
                case "title":       Console.Title = restLine;                return;
                case "pwd":         Console.WriteLine(_currentDir);         return;
                case "clear":      Console.Clear();                        return;
                case "ls":          CmdDir(rest);                           return;


                case "du":          CmdDu(rest);                            return;
                case "cat":         CmdCat(rest);                           return;


                case "touch":       CmdTouch(rest);                         return;
                case "rm":          CmdDel(rest);                           return;
                case "cp":          CmdCopy(rest);                          return;
                case "mv":          CmdMove(rest);                          return;
                case "diff":        CmdDiff(rest);                          return;
                case "ps":          CmdPs(rest);                            return;
                case "procinfo":    CmdProcInfo(rest);                      return;
                case "kill":        CmdKill(rest);                          return;
                case "priority":    CmdPriority(rest);                      return;
                case "affinity":    CmdAffinity(rest);                      return;
                case "procmodules": CmdProcModules(rest);                   return;
                case "procthreads": CmdProcThreads(rest);                   return;
                case "closeproc":   CmdCloseProc(rest);                     return;
                case "waitproc":    CmdWaitProc(rest);                      return;
                case "killtree":   CmdKillTree(rest);                      return;

                case "sysinfo":     CmdSysInfo();                           return;
                case "netinfo":     CmdNetInfo();                           return;
                case "netconns":    CmdNetConns(rest);                      return;
                case "ping":        CmdPing(rest);                          return;
                case "portscan":    CmdPortScan(rest);                      return;
                case "http":        CmdHttp(rest).GetAwaiter().GetResult(); return;
                case "hash":        CmdHash(rest);                          return;
                case "grep":        CmdGrep(rest);                          return;
                case "zip":         CmdZip(rest);                           return;
                case "unzip":       CmdUnzip(rest);                         return;
                case "clip":        CmdClip(rest, restLine);                return;
                case "env":         CmdEnv(rest);                           return;
                case "which":       CmdWhich(rest);                         return;
                case "history":     CmdHistory();                           return;
                case "alias":       CmdAlias(restLine);                     return;
                case "base64":      CmdBase64(rest, restLine);              return;
                case "json":        CmdJson(rest);                          return;

                case "svc":         CmdSvc(rest);                           return;
                case "eventlog":    CmdEventLog(rest);                      return;
                case "whoami":      CmdWhoAmI();                            return;
                case "watch":       CmdWatch(rest);                         return;
                case "lock":        LockWorkStation();                      return;
                case "shutdown-now":RunInCmd("shutdown /s /t 0");          return;
                case "restart-now": RunInCmd("shutdown /r /t 0");          return;
                case "logoff-now":  RunInCmd("shutdown /l");               return;
                case "elevate":     CmdElevate(restLine);                   return;
                case "psh":
                case "powershell":  CmdPowerShell(restLine);                 return;
                case "ps1":         CmdPowerShellFile(rest);                 return;
                default:
                    if (_settings.DefaultShell.Equals("powershell", StringComparison.OrdinalIgnoreCase)) CmdPowerShell(line);
                    else RunInCmd(line);
                    return;
            }
        }

        // ================================================================
        //  УТИЛИТЫ
        // ================================================================
        private static string[] SplitArgs(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ' ' && !inQuotes) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } }
                else sb.Append(c);
            }
            if (sb.Length > 0) result.Add(sb.ToString());
            if (result.Count == 0) result.Add("");
            return result.ToArray();
        }

        private static void RunInCmd(string line)
        {
            // Unknown commands are executed by the real Windows CMD interpreter.
            // This preserves CMD operators, pipes, redirection, batch files and built-ins.
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /c " + line,
                WorkingDirectory = _currentDir,
                UseShellExecute = false,
            };
            RunChildProcess(psi);
        }

        // ================================================================
        //  POWERSHELL
        // ================================================================
        private static string FindPowerShell()
        {
            // Prefer PowerShell 7 (pwsh); fall back to Windows PowerShell 5.1.
            string[] candidates = _settings.PowerShellHost switch
            {
                "pwsh" => new[] { "pwsh.exe" },
                "windows" => new[] { "powershell.exe" },
                _ => new[] { "pwsh.exe", "powershell.exe" }
            };
            foreach (string candidate in candidates)
            {
                try
                {
                    var probe = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "-NoLogo -NoProfile -Command \"exit 0\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(probe);
                    if (process != null)
                    {
                        process.WaitForExit(3000);
                        if (!process.HasExited) { try { process.Kill(true); } catch { } }
                        else return candidate;
                    }
                }
                catch { }
            }
            throw new FileNotFoundException("PowerShell was not found. Install PowerShell 7 or enable Windows PowerShell.");
        }

        private static void CmdPowerShell(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                Console.WriteLine("Usage: psh <PowerShell command>");
                Console.WriteLine("Example: psh Get-Process");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FindPowerShell(),
                    WorkingDirectory = _currentDir,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                // EncodedCommand avoids Windows command-line quoting/escaping corruption.
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

                using var process = Process.Start(psi);
                if (process == null) { Console.WriteLine("Could not start PowerShell."); return; }
                _activeChild = process;
                try { process.WaitForExit(); if (process.ExitCode != 0) Console.WriteLine($"PowerShell exited with code {process.ExitCode}."); }
                finally { _activeChild = null; }
            }
            catch (Exception ex) { Console.WriteLine($"PowerShell error: {ex.Message}"); }
        }

        private static void CmdPowerShellFile(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: ps1 <script.ps1> [script arguments...]");
                Console.WriteLine("Example: ps1 \"scripts\\backup.ps1\" -Destination \"D:\\Backups\"");
                return;
            }

            string scriptPath = ResolvePath(args[0]);
            if (!File.Exists(scriptPath))
            {
                Console.WriteLine($"PowerShell script not found: {scriptPath}");
                return;
            }
            if (!string.Equals(Path.GetExtension(scriptPath), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Warning: the specified file does not have a .ps1 extension.");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FindPowerShell(),
                    WorkingDirectory = _currentDir,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);
                foreach (string arg in args.Skip(1))
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null) { Console.WriteLine("Could not start PowerShell."); return; }
                _activeChild = process;
                try { process.WaitForExit(); if (process.ExitCode != 0) Console.WriteLine($"PowerShell script exited with code {process.ExitCode}."); }
                finally { _activeChild = null; }
            }
            catch (Exception ex) { Console.WriteLine($"PowerShell error: {ex.Message}"); }
        }

        private static void CmdPushd(string path)
        {
            path = path.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("Usage: pushd <path>");
                return;
            }
            string target = ResolvePath(path);
            if (!Directory.Exists(target))
            {
                Console.WriteLine($"The system cannot find the path specified: {target}");
                return;
            }
            _directoryStack.Push(_currentDir);
            _currentDir = target;
        }

        private static void CmdPopd()
        {
            if (_directoryStack.Count == 0)
            {
                Console.WriteLine("Directory stack is empty.");
                return;
            }
            _currentDir = _directoryStack.Pop();
        }

        private static void CmdSet(string expression)
        {
            expression = expression.Trim();
            if (expression.StartsWith('"') && expression.EndsWith('"') && expression.Length >= 2)
                expression = expression[1..^1];

            if (expression.Length == 0)
            {
                foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables()
                             .Cast<System.Collections.DictionaryEntry>()
                             .OrderBy(x => x.Key?.ToString(), StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{item.Key}={item.Value}");
                return;
            }

            int equals = expression.IndexOf('=');
            if (equals < 0)
            {
                string prefix = expression;
                foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
                {
                    string key = item.Key?.ToString() ?? "";
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"{key}={item.Value}");
                }
                return;
            }

            string name = expression[..equals].Trim();
            string value = expression[(equals + 1)..];
            if (name.Length == 0)
            {
                Console.WriteLine("Environment variable name cannot be empty.");
                return;
            }
            Environment.SetEnvironmentVariable(name, value);
        }

        private static void CmdPath(string expression)
        {
            expression = expression.Trim();
            if (expression.Length == 0)
            {
                Console.WriteLine($"PATH={Environment.GetEnvironmentVariable("PATH")}");
                return;
            }
            if (expression.StartsWith('='))
            {
                Console.WriteLine("The syntax of the command is incorrect.");
                return;
            }
            Environment.SetEnvironmentVariable("PATH", expression);
        }

        private static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return $"{size:0.##} {units[i]}";
        }

        private static string ResolvePath(string p) =>
            Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_currentDir, p));

        // ================================================================
        //  КОМАНДЫ — Справка
        // ================================================================
        private static void CmdHelp()
        {
            Console.WriteLine(@"
FILES & DIRECTORIES
  cd <path>                       Change directory
  pwd                             Print current directory
  dir / ls [-a] [path]            List directory contents
  tree [depth]                    Show directory tree
  find <pattern> [path]            Find files
  du [path]                        Show directory sizes
  cat / type <file>               Display file contents
  mkdir <path>                    Create directory
  touch <file>                    Create or update file timestamp
  rm / del <path> [-r]            Remove file or directory
  cp / copy <src> <dst>            Copy
  mv / move <src> <dst>            Move or rename
  diff <file1> <file2>             Compare files
  grep <text> <path> [-r]          Search file contents
  hash <file> [md5|sha256]         Calculate file hash
  zip <source> <archive.zip>       Create archive
  unzip <archive.zip> <directory>  Extract archive

PROCESSES
  ps [filter]                      List processes
  procinfo <PID>                   Show process details
  kill <PID|name>                  Terminate process
  priority <PID> <level>           Set process priority
  affinity <PID> <hex-mask>        Set allowed CPU cores (e.g. 0x3)
  procmodules <PID>                List loaded modules (access permitting)
  procthreads <PID>                List process threads and states
  closeproc <PID>                  Request graceful window close
  waitproc <PID> [seconds]         Wait for a process to exit
  killtree <PID>                   Terminate process and its child tree (confirm)
  start <path> [arguments]         Launch application

SYSTEM
  sysinfo                          Show OS, CPU, memory and disks
  whoami                           Show user and privileges
  watch <seconds> <command>        Repeat command (Ctrl+C to stop)
  lock                             Lock workstation
  shutdown-now                     Shut down Windows
  restart-now                      Restart Windows
  logoff-now                       Sign out
  elevate <command>                Run command through UAC

NETWORK
  netinfo                          Show network interfaces and IPs
  netconns [port]                  Show active TCP connections
  ping <host>                      Test connectivity
  portscan <host> <from> <to>       Scan up to 2000 ports
  http get <url>                   Send HTTP GET request
  http download <url> <file>       Download a file

WINDOWS REGISTRY
  reg list <path>                  List keys and values
  reg get <path> <name>            Read value
  reg set <path> <name> <value>    Write value
  reg delete <path> <name>         Delete value

SERVICES & EVENTS
  svc list [filter]                List services
  svc status|start|stop|restart <name>
  eventlog <System|Application> [count]

POWERSHELL
  psh <command>                   Run a PowerShell command
  powershell <command>            Alias for psh
  ps1 <script.ps1> [arguments]    Run a PowerShell script file
  (Uses configured host; command text is passed using EncodedCommand.
   Execution policy is not bypassed automatically.)

CONFIGURATION
  config                           Open interactive Commando settings
  config show                      Show current settings
  config set <key> <value>         Change a setting
  config save                      Save settings to settings.json
  config reset                     Restore defaults

OTHER
  clip get / clip set <text>       Clipboard operations
  env list|get|set                 Environment variables
  which <command>                  Locate executable
  history                          Show command history
  alias name=command               Define command alias
  base64 encode|decode <text>      Base64 conversion
  json <file>                      Format JSON file
  exit                             Exit shell

Unknown commands use the configured default shell (cmd or PowerShell). Use psh for explicit PowerShell.
");
        }

        // ================================================================
        //  КОМАНДЫ — Файловая система
        // ================================================================
        private static void CmdCd(string arg)
        {
            arg = arg.Trim();
            bool changeDrive = false;
            if (arg.StartsWith("/d ", StringComparison.OrdinalIgnoreCase))
            {
                changeDrive = true;
                arg = arg[3..].Trim();
            }
            if (string.IsNullOrEmpty(arg))
            {
                Console.WriteLine(_currentDir);
                return;
            }
            if (arg == "~")
                arg = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string target = Path.IsPathRooted(arg)
                ? arg
                : Path.GetFullPath(Path.Combine(_currentDir, arg));
            if (Directory.Exists(target))
            {
                _currentDir = target;
                Environment.CurrentDirectory = target;
            }
            else
                Console.WriteLine($"The system cannot find the path specified: {target}");
        }

        private static void CmdDir(string[] args)
        {
            bool showHidden = args.Contains("-a");
            string path = args.FirstOrDefault(a => !a.StartsWith("-")) is string p && p.Length > 0
                ? (Path.IsPathRooted(p) ? p : Path.Combine(_currentDir, p))
                : _currentDir;
            if (!Directory.Exists(path)) { Console.WriteLine($"Directory not found: {path}"); return; }
            var di = new DirectoryInfo(path);
            Console.WriteLine($"\n {di.FullName}\n");
            foreach (var d in di.GetDirectories())
            {
                if (!showHidden && d.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                Console.WriteLine($"  [DIR]  {d.Name}");
            }
            foreach (var f in di.GetFiles())
            {
                if (!showHidden && f.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                Console.WriteLine($"  {HumanSize(f.Length),10}  {f.Name}");
            }
            Console.WriteLine();
        }

        private static void CmdTree(string[] args)
        {
            int maxDepth = args.Length > 0 && int.TryParse(args[0], out int d) ? d : int.MaxValue;
            Console.WriteLine(_currentDir);
            PrintTree(_currentDir, "", maxDepth, 0);
        }

        private static void PrintTree(string path, string indent, int maxDepth, int depth)
        {
            if (depth > maxDepth) return;
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(path).GetFileSystemInfos().OrderBy(e => e is DirectoryInfo ? 0 : 1).ThenBy(e => e.Name).ToList(); }
            catch { Console.WriteLine($"{indent}[access denied]"); return; }
            for (int i = 0; i < entries.Count; i++)
            {
                bool last = i == entries.Count - 1;
                Console.WriteLine($"{indent}{(last ? "└── " : "├── ")}{entries[i].Name}{(entries[i] is DirectoryInfo ? "/" : "")}");
                if (entries[i] is DirectoryInfo sub)
                    PrintTree(sub.FullName, indent + (last ? "    " : "│   "), maxDepth, depth + 1);
            }
        }

        private static void CmdFind(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: find <pattern> [path]"); return; }
            string pattern = args[0];
            string root = args.Length > 1 ? ResolvePath(args[1]) : _currentDir;
            if (!Directory.Exists(root)) { Console.WriteLine($"Directory not found: {root}"); return; }
            int count = 0;
            var stack = new Stack<string>(new[] { root });
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try { foreach (var f in Directory.GetFiles(dir, pattern)) { Console.WriteLine(f); count++; } } catch { }
                try { foreach (var d in Directory.GetDirectories(dir)) stack.Push(d); } catch { }
            }
            Console.WriteLine($"Found: {count}");
        }

        private static void CmdDu(string[] args)
        {
            string path = args.Length > 0 ? ResolvePath(args[0]) : _currentDir;
            if (!Directory.Exists(path)) { Console.WriteLine($"Directory not found: {path}"); return; }
            long total = 0;
            foreach (var sub in Directory.GetDirectories(path))
            { long sz = DirSize(sub); total += sz; Console.WriteLine($"  {HumanSize(sz),10}  {Path.GetFileName(sub)}/"); }
            foreach (var file in Directory.GetFiles(path))
                try { total += new FileInfo(file).Length; } catch { }
            Console.WriteLine($"  Total: {HumanSize(total)}");
        }

        private static long DirSize(string path)
        {
            long size = 0;
            try { foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) try { size += new FileInfo(f).Length; } catch { } } catch { }
            return size;
        }

        private static void CmdCat(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: cat <file>"); return; }
            string path = ResolvePath(args[0]);
            if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return; }
            Console.WriteLine(File.ReadAllText(path));
        }

        private static void CmdMkdir(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: mkdir <path>"); return; }
            Directory.CreateDirectory(ResolvePath(args[0]));
        }

        private static void CmdTouch(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: touch <file>"); return; }
            string path = ResolvePath(args[0]);
            if (!File.Exists(path)) File.Create(path).Dispose();
            else File.SetLastWriteTime(path, DateTime.Now);
        }

        private static void CmdDel(string[] args)
        {
            bool recursive = args.Contains("-r");
            string? target = args.FirstOrDefault(a => a != "-r");
            if (target == null) { Console.WriteLine("Usage: rm <path> [-r]"); return; }
            string path = ResolvePath(target);
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive);
            else Console.WriteLine($"Not found: {path}");
        }

        private static void CmdCopy(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: cp <source> <destination>"); return; }
            string src = ResolvePath(args[0]), dst = ResolvePath(args[1]);
            if (File.Exists(src)) { File.Copy(src, dst, true); return; }
            if (Directory.Exists(src)) { CopyDir(src, dst); return; }
            Console.WriteLine($"Source not found: {src}");
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static void CmdMove(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: mv <source> <destination>"); return; }
            string src = ResolvePath(args[0]), dst = ResolvePath(args[1]);
            if (File.Exists(src)) File.Move(src, dst, true);
            else if (Directory.Exists(src)) Directory.Move(src, dst);
            else Console.WriteLine($"Source not found: {src}");
        }

        private static void CmdDiff(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: diff <file1> <file2>"); return; }
            string f1 = ResolvePath(args[0]), f2 = ResolvePath(args[1]);
            if (!File.Exists(f1) || !File.Exists(f2)) { Console.WriteLine("One of the files was not found."); return; }
            var l1 = File.ReadAllLines(f1); var l2 = File.ReadAllLines(f2);
            int max = Math.Max(l1.Length, l2.Length), diffs = 0;
            for (int i = 0; i < max; i++)
            {
                string a = i < l1.Length ? l1[i] : "<no line>";
                string b = i < l2.Length ? l2[i] : "<no line>";
                if (a != b) { diffs++; Console.WriteLine($"Line {i + 1}:"); Console.WriteLine($"  - {a}"); Console.WriteLine($"  + {b}"); }
            }
            Console.WriteLine(diffs == 0 ? "Files are identical." : $"Differences: {diffs}");
        }

        private static void CmdHash(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: hash <file> [md5|sha256]"); return; }
            string path = ResolvePath(args[0]);
            if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return; }
            string algo = args.Length > 1 ? args[1].ToLowerInvariant() : "sha256";
            using var stream = File.OpenRead(path);
            byte[] h = algo == "md5" ? MD5.HashData(stream) : SHA256.HashData(stream);
            Console.WriteLine($"{algo.ToUpperInvariant()}: {Convert.ToHexString(h).ToLowerInvariant()}");
        }

        private static void CmdGrep(string[] args)
        {
            var pos = args.Where(a => a != "-r").ToArray();
            bool recursive = args.Contains("-r");
            if (pos.Length < 2) { Console.WriteLine("Usage: grep <text> <path> [-r]"); return; }
            string pattern = pos[0], path = ResolvePath(pos[1]);
            if (File.Exists(path)) { GrepFile(path, pattern); return; }
            if (!Directory.Exists(path)) { Console.WriteLine($"Not found: {path}"); return; }
            foreach (var f in Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                GrepFile(f, pattern);
        }

        private static void GrepFile(string file, string pattern)
        {
            string[] lines; try { lines = File.ReadAllLines(file); } catch { return; }
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"{file}:{i + 1}: {lines[i].Trim()}");
        }

        private static void CmdZip(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: zip <source> <archive.zip>"); return; }
            string src = ResolvePath(args[0]), dst = ResolvePath(args[1]);
            if (File.Exists(dst)) File.Delete(dst);
            if (Directory.Exists(src)) ZipFile.CreateFromDirectory(src, dst);
            else if (File.Exists(src)) { using var z = ZipFile.Open(dst, ZipArchiveMode.Create); z.CreateEntryFromFile(src, Path.GetFileName(src)); }
            else { Console.WriteLine($"Source not found: {src}"); return; }
            Console.WriteLine($"Created: {dst}");
        }

        private static void CmdUnzip(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: unzip <archive.zip> <directory>"); return; }
            string src = ResolvePath(args[0]), dst = ResolvePath(args[1]);
            if (!File.Exists(src)) { Console.WriteLine($"Archive not found: {src}"); return; }
            ZipFile.ExtractToDirectory(src, dst, true);
            Console.WriteLine($"Extracted to: {dst}");
        }

        // ================================================================
        //  КОМАНДЫ — Processы
        // ================================================================
        private static void CmdPs(string[] args)
        {
            string? filter = args.FirstOrDefault();
            Console.WriteLine($"{"PID",-8}{"Name",-30}{"Memory",12}");
            foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName))
            {
                if (filter != null && !p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                try { Console.WriteLine($"{p.Id,-8}{p.ProcessName,-30}{HumanSize(p.WorkingSet64),12}"); } catch { }
            }
        }

        private static void CmdProcInfo(string[] args)
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int pid)) { Console.WriteLine("Usage: procinfo <PID>"); return; }
            try
            {
                var p = Process.GetProcessById(pid);
                Console.WriteLine($"PID:            {p.Id}");
                Console.WriteLine($"Name:            {p.ProcessName}");
                Console.WriteLine($"Memory (WS):    {HumanSize(p.WorkingSet64)}");
                Console.WriteLine($"Threads:        {p.Threads.Count}");
                try { Console.WriteLine($"Handles:        {p.HandleCount}"); } catch { }
                try { Console.WriteLine($"CPU time:       {p.TotalProcessorTime}"); } catch { }
                try { Console.WriteLine($"Responding:     {p.Responding}"); } catch { }
                try { Console.WriteLine($"Window title:   {p.MainWindowTitle}"); } catch { }
                Console.WriteLine($"Priority:       {p.PriorityClass}");
                try { Console.WriteLine($"Started:        {p.StartTime}"); } catch { }
                try { Console.WriteLine($"Path:           {p.MainModule?.FileName}"); } catch { Console.WriteLine("Path:           [access denied]"); }
            }
            catch (Exception ex) { Console.WriteLine($"Not found: {ex.Message}"); }
        }

        private static void CmdKill(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: kill <PID|name>"); return; }
            Process[] matches;
            if (int.TryParse(args[0], out int pid))
            {
                try { matches = new[] { Process.GetProcessById(pid) }; }
                catch (Exception ex) { Console.WriteLine(ex.Message); return; }
            }
            else matches = Process.GetProcessesByName(args[0]);

            if (matches.Length == 0) { Console.WriteLine("Process not found."); return; }
            foreach (var p in matches)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.Id <= 4)
                    { Console.WriteLine($"Refusing to terminate protected process {p.Id} ({p.ProcessName})."); continue; }
                    Console.Write($"Terminate {p.ProcessName} (PID {p.Id})? [y/N]: ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                    { Console.WriteLine("Cancelled."); continue; }
                    p.Kill();
                    Console.WriteLine($"Termination requested for PID {p.Id}.");
                }
                catch (Exception ex) { Console.WriteLine($"PID {p.Id}: {ex.Message}"); }
            }
        }

        private static bool TryGetProcess(string[] args, out Process? process)
        {
            process = null;
            if (args.Length == 0 || !int.TryParse(args[0], out int pid))
            { Console.WriteLine("Usage: command <PID>"); return false; }
            try { process = Process.GetProcessById(pid); return true; }
            catch (Exception ex) { Console.WriteLine($"Process unavailable: {ex.Message}"); return false; }
        }

        private static void CmdAffinity(string[] args)
        {
            if (!TryGetProcess(args, out var p) || p == null) { Console.WriteLine("Usage: affinity <PID> <hex-mask>"); return; }
            if (args.Length < 2 || !long.TryParse(args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    System.Globalization.NumberStyles.HexNumber, null, out long mask) || mask <= 0)
            { Console.WriteLine("Usage: affinity <PID> <hex-mask> (example: affinity 1234 0x3)"); return; }
            try { p.ProcessorAffinity = new IntPtr(mask); Console.WriteLine($"CPU affinity updated for PID {p.Id}: 0x{mask:X}"); }
            catch (Exception ex) { Console.WriteLine($"Unable to change affinity: {ex.Message}"); }
        }

        private static void CmdProcModules(string[] args)
        {
            if (!TryGetProcess(args, out var p) || p == null) return;
            try
            {
                Console.WriteLine($"Modules for {p.ProcessName} (PID {p.Id}):");
                foreach (ProcessModule m in p.Modules)
                    Console.WriteLine($"{m.ModuleName,-32} {HumanSize(m.ModuleMemorySize),12}  {m.FileName}");
            }
            catch (Exception ex) { Console.WriteLine($"Module access failed: {ex.Message}"); }
        }

        private static void CmdProcThreads(string[] args)
        {
            if (!TryGetProcess(args, out var p) || p == null) return;
            try
            {
                Console.WriteLine($"Threads for {p.ProcessName} (PID {p.Id}):");
                Console.WriteLine($"{"TID",-10}{"State",-18}{"Priority",-12}{"Start time"}");
                foreach (ProcessThread t in p.Threads)
                {
                    string started; try { started = t.StartTime.ToString("u"); } catch { started = "n/a"; }
                    string priority; try { priority = t.PriorityLevel.ToString(); } catch { priority = "n/a"; }
                    Console.WriteLine($"{t.Id,-10}{t.ThreadState,-18}{priority,-12}{started}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"Thread access failed: {ex.Message}"); }
        }

        private static void CmdCloseProc(string[] args)
        {
            if (!TryGetProcess(args, out var p) || p == null) return;
            try
            {
                if (!p.CloseMainWindow()) Console.WriteLine("No closable main window; no action was taken.");
                else Console.WriteLine($"Close request sent to PID {p.Id}.");
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
        }

        private static void CmdWaitProc(string[] args)
        {
            if (!TryGetProcess(args, out var p) || p == null) return;
            int seconds = 0;
            if (args.Length > 1 && (!int.TryParse(args[1], out seconds) || seconds < 0))
            { Console.WriteLine("Usage: waitproc <PID> [seconds]"); return; }
            try
            {
                bool exited = seconds == 0 ? WaitIndefinitely(p) : p.WaitForExit(checked(seconds * 1000));
                Console.WriteLine(exited ? $"PID {p.Id} has exited (code {SafeExitCode(p)})." : "Timed out.");
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
        }

        private static bool WaitIndefinitely(Process p) { p.WaitForExit(); return true; }
        private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch { return "n/a"; } }

        private static void CmdKillTree(string[] args)
        {
            if (!TryGetProcess(args, out var p) || p == null) { Console.WriteLine("Usage: killtree <PID>"); return; }
            if (p.Id == Environment.ProcessId || p.Id <= 4)
            { Console.WriteLine("Refusing to terminate this protected process."); return; }
            Console.Write($"Terminate PID {p.Id} ({p.ProcessName}) and its child processes? [y/N]: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("Cancelled."); return; }
            try { p.Kill(entireProcessTree: true); Console.WriteLine("Process tree termination requested."); }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
        }

        private static void CmdPriority(string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[0], out int pid))
            { Console.WriteLine("Usage: priority <PID> <low|belownormal|normal|abovenormal|high>"); return; }
            if (pid == Environment.ProcessId || pid <= 4)
            { Console.WriteLine("Refusing to change priority of this protected process."); return; }
            if (!new[] { "low", "belownormal", "normal", "abovenormal", "high" }.Contains(args[1].ToLowerInvariant()))
            { Console.WriteLine("Invalid priority level."); return; }
            try
            {
                var p = Process.GetProcessById(pid);
                p.PriorityClass = args[1].ToLowerInvariant() switch
                {
                    "low" => ProcessPriorityClass.Idle,
                    "belownormal" => ProcessPriorityClass.BelowNormal,
                    "abovenormal" => ProcessPriorityClass.AboveNormal,
                    "high" => ProcessPriorityClass.High,
                    _ => ProcessPriorityClass.Normal
                };
                Console.WriteLine($"Priority {pid} -> {p.PriorityClass}.");
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
        }

        private static void CmdStart(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: start <path> [arguments]"); return; }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "",
                    WorkingDirectory = _currentDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Console.WriteLine($"Failed to launch: {ex.Message}"); }
        }

        // ================================================================
        //  КОМАНДЫ — Система
        // ================================================================
        private static void CmdSysInfo()
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref mem);
            Console.WriteLine($"OS:              {Environment.OSVersion}");
            Console.WriteLine($"Computer:       {Environment.MachineName}");
            Console.WriteLine($"User:    {Environment.UserName}");
            Console.WriteLine($"CPU cores:         {Environment.ProcessorCount}");
            Console.WriteLine($"Architecture:     {RuntimeInformation.OSArchitecture}");
            Console.WriteLine($"Total RAM:       {HumanSize((long)mem.ullTotalPhys)}");
            Console.WriteLine($"Free RAM:    {HumanSize((long)mem.ullAvailPhys)}");
            Console.WriteLine($"Uptime:          {TimeSpan.FromMilliseconds(Environment.TickCount64):dd\\.hh\\:mm\\:ss}");
            Console.WriteLine();
            Console.WriteLine("Drives:");
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady)
                    Console.WriteLine($"  {d.Name,-6} {HumanSize(d.TotalFreeSpace)} free of {HumanSize(d.TotalSize)}");
        }

        private static void CmdWhoAmI()
        {
            Console.WriteLine($"User: {Environment.UserDomainName}\\{Environment.UserName}");
            Console.WriteLine($"Computer:    {Environment.MachineName}");
            bool admin = false;
            try
            {
                var psi = new ProcessStartInfo("net.exe", "session")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!; p.WaitForExit(); admin = p.ExitCode == 0;
            }
            catch { }
            Console.WriteLine($"Administrator: {(admin ? "yes" : "no")}");
        }

        private static void CmdWatch(string[] args)
        {
            if (args.Length < 2 || !double.TryParse(args[0], out double seconds))
            { Console.WriteLine("Usage: watch <seconds> <command> (Ctrl+C stop)"); return; }
            string subCommand = string.Join(" ", args.Skip(1));
            _stopWatch = false;
            ConsoleCancelEventHandler h = (s, e) => { e.Cancel = true; _stopWatch = true; };
            Console.CancelKeyPress += h;
            try
            {
                while (!_stopWatch)
                {
                    Console.Clear();
                    Console.WriteLine($"watch -n {seconds}: {subCommand}  (Ctrl+C stop)\n");
                    try { Execute(subCommand); } catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                }
            }
            finally { Console.CancelKeyPress -= h; }
        }

        private static void CmdElevate(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) { Console.WriteLine("Usage: elevate <command>"); return; }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe", Arguments = $"/c {command}",
                    WorkingDirectory = _currentDir, UseShellExecute = true, Verb = "runas"
                });
            }
            catch (Exception ex) { Console.WriteLine($"Cancelled: {ex.Message}"); }
        }

        // ================================================================
        //  КОМАНДЫ — Сеть
        // ================================================================
        private static void CmdNetInfo()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                Console.WriteLine($"{ni.Name} [{ni.OperationalStatus}] - {ni.NetworkInterfaceType}");
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    Console.WriteLine($"    {addr.Address}");
            }
        }

        private static void CmdNetConns(string[] args)
        {
            int? filterPort = args.Length > 0 && int.TryParse(args[0], out int fp) ? fp : null;
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            Console.WriteLine($"{"Local address",-28}{"Remote address",-28}{"Status"}");
            foreach (var c in props.GetActiveTcpConnections())
            {
                if (filterPort != null && c.LocalEndPoint.Port != filterPort && c.RemoteEndPoint.Port != filterPort) continue;
                Console.WriteLine($"{c.LocalEndPoint,-28}{c.RemoteEndPoint,-28}{c.State}");
            }
        }

        private static void CmdPing(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: ping <host>"); return; }
            using var ping = new Ping();
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    var r = ping.Send(args[0], 2000);
                    Console.WriteLine(r?.Status == IPStatus.Success
                        ? $"Reply from {r.Address}: {r.RoundtripTime}ms TTL={r.Options?.Ttl}"
                        : $"No reply: {r?.Status}");
                }
                catch (Exception ex) { Console.WriteLine(ex.Message); break; }
            }
        }

        private static void CmdPortScan(string[] args)
        {
            if (args.Length < 3 || !int.TryParse(args[1], out int from) || !int.TryParse(args[2], out int to))
            { Console.WriteLine("Usage: portscan <host> <from> <to>  (max 2000)"); return; }
            if (to < from) (from, to) = (to, from);
            if (to - from > 2000) { Console.WriteLine("Maximum 2000 ports per scan."); return; }
            Console.WriteLine($"Scanning {args[0]}:{from}-{to} ...");
            for (int port = from; port <= to; port++)
            {
                using var client = new TcpClient();
                try { if (client.ConnectAsync(args[0], port).Wait(200) && client.Connected) Console.WriteLine($"  Port {port}: OPEN"); }
                catch { }
            }
            Console.WriteLine("Done.");
        }

        private static async System.Threading.Tasks.Task CmdHttp(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: http get <url>  |  http download <url> <file>"); return; }
            try
            {
                if (args[0].Equals("get", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(await _http.GetStringAsync(args[1]));
                else if (args[0].Equals("download", StringComparison.OrdinalIgnoreCase) && args.Length > 2)
                {
                    byte[] data = await _http.GetByteArrayAsync(args[1]);
                    await File.WriteAllBytesAsync(ResolvePath(args[2]), data);
                    Console.WriteLine($"Downloaded {HumanSize(data.Length)} -> {args[2]}");
                }
                else Console.WriteLine("Usage: http get <url>  |  http download <url> <file>");
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
        }

        // ================================================================
        //  КОМАНДЫ — Реестр
        // ================================================================
        private static RegistryKey? ResolveHive(string root, out string subPath)
        {
            int slash = root.IndexOfAny(new[] { '\\', '/' });
            string hive = slash >= 0 ? root[..slash] : root;
            subPath = slash >= 0 ? root[(slash + 1)..] : "";
            return hive.ToUpperInvariant() switch
            {
                "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
                "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
                "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
                "HKU" or "HKEY_USERS" => Registry.Users,
                "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
                _ => null
            };
        }

        private static void CmdReg(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: reg list|get|set|delete <path> [name] [value]"); return; }
            var hive = ResolveHive(args[1], out string subPath);
            if (hive == null) { Console.WriteLine("Unknown hive. Use HKCU, HKLM, HKCR, HKU or HKCC."); return; }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "list":
                        using (var key = hive.OpenSubKey(subPath))
                        {
                            if (key == null) { Console.WriteLine("Key not found."); return; }
                            foreach (var s in key.GetSubKeyNames()) Console.WriteLine($"  [KEY]  {s}\\");
                            foreach (var v in key.GetValueNames()) Console.WriteLine($"  {v} = {key.GetValue(v)}");
                        }
                        break;
                    case "get":
                        if (args.Length < 3) { Console.WriteLine("Usage: reg get <path> <name>"); return; }
                        using (var key = hive.OpenSubKey(subPath)) Console.WriteLine(key?.GetValue(args[2])?.ToString() ?? "(not found)");
                        break;
                    case "set":
                        if (args.Length < 4) { Console.WriteLine("Usage: reg set <path> <name> <value>"); return; }
                        using (var key = hive.CreateSubKey(subPath, true)) key.SetValue(args[2], args[3]);
                        Console.WriteLine("Written.");
                        break;
                    case "delete":
                        if (args.Length < 3) { Console.WriteLine("Usage: reg delete <path> <name>"); return; }
                        using (var key = hive.OpenSubKey(subPath, true)) key?.DeleteValue(args[2], false);
                        Console.WriteLine("Deleted (if it existed).");
                        break;
                    default: Console.WriteLine("Unknown reg subcommand reg."); break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"Registry error: {ex.Message}"); }
        }

        // ================================================================
        //  КОМАНДЫ — Службы
        // ================================================================
        private static void CmdSvc(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: svc list [filter] | svc status|start|stop|restart <name>"); return; }
            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                string? filter = args.Length > 1 ? args[1] : null;
                foreach (var s in ServiceController.GetServices())
                {
                    if (filter != null && !s.ServiceName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine($"{s.ServiceName,-30}{s.Status,-15}{s.DisplayName}");
                }
                return;
            }
            if (args.Length < 2) { Console.WriteLine("Specify a service name."); return; }
            try
            {
                using var svc = new ServiceController(args[1]);
                switch (args[0].ToLowerInvariant())
                {
                    case "status": Console.WriteLine($"{svc.ServiceName}: {svc.Status}"); break;
                    case "start": svc.Start(); svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); Console.WriteLine("Started."); break;
                    case "stop": svc.Stop(); svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); Console.WriteLine("Stopped."); break;
                    case "restart":
                        svc.Stop(); svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        svc.Start(); svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                        Console.WriteLine("Restarted."); break;
                    default: Console.WriteLine("Unknown reg subcommand svc."); break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"Service error: {ex.Message}"); }
        }

        // ================================================================
        //  КОМАНДЫ — Журнал событий
        // ================================================================
        private static void CmdEventLog(string[] args)
        {
            string logName = args.Length > 0 ? args[0] : "Application";
            int count = args.Length > 1 && int.TryParse(args[1], out int c) ? c : 10;
            try
            {
                using var log = new System.Diagnostics.EventLog(logName);
                int start = Math.Max(0, log.Entries.Count - count);
                for (int i = log.Entries.Count - 1; i >= start; i--)
                {
                    var e = log.Entries[i];
                    Console.WriteLine($"[{e.TimeGenerated:yyyy-MM-dd HH:mm:ss}] {e.EntryType} - {e.Source}: {e.Message.Split('\n')[0]}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"Event log error: {ex.Message}"); }
        }

        // ================================================================
        //  КОМАНДЫ — Прочее
        // ================================================================
        private static void CmdClip(string[] args, string restLine)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: clip get | clip set <text>"); return; }
            if (args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                string text = restLine.Length > 3 ? restLine[3..].Trim() : "";
                var psi = new ProcessStartInfo("cmd.exe", "/c clip") { RedirectStandardInput = true, UseShellExecute = false };
                using var proc = Process.Start(psi)!; proc.StandardInput.Write(text); proc.StandardInput.Close(); proc.WaitForExit();
                Console.WriteLine("Copied to clipboard.");
            }
            else if (args[0].Equals("get", StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command Get-Clipboard")
                { RedirectStandardOutput = true, UseShellExecute = false };
                using var proc = Process.Start(psi)!; Console.WriteLine(proc.StandardOutput.ReadToEnd()); proc.WaitForExit();
            }
        }

        private static void CmdEnv(string[] args)
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            { foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) Console.WriteLine($"{e.Key}={e.Value}"); return; }
            if (args[0].Equals("get", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
            { Console.WriteLine(Environment.GetEnvironmentVariable(args[1])); return; }
            if (args[0].Equals("set", StringComparison.OrdinalIgnoreCase) && args.Length > 2)
            { Environment.SetEnvironmentVariable(args[1], args[2]); Console.WriteLine($"{args[1]} has been set."); return; }
            Console.WriteLine("Usage: env [list | get name | set name value]");
        }

        private static void CmdWhich(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: which <command>"); return; }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                foreach (var ext in new[] { ".exe", ".bat", ".cmd", "" })
                {
                    string c = Path.Combine(dir, args[0] + ext);
                    if (File.Exists(c)) { Console.WriteLine(c); return; }
                }
            Console.WriteLine("Not found in PATH.");
        }

        private static void CmdHistory()
        {
            for (int i = 0; i < _history.Count; i++) Console.WriteLine($"{i + 1,4}  {_history[i]}");
        }

        private static void CmdAlias(string restLine)
        {
            if (string.IsNullOrWhiteSpace(restLine)) { foreach (var kv in _aliases) Console.WriteLine($"{kv.Key} = {kv.Value}"); return; }
            int eq = restLine.IndexOf('=');
            if (eq < 0) { Console.WriteLine("Usage: alias name=command"); return; }
            _aliases[restLine[..eq].Trim()] = restLine[(eq + 1)..].Trim();
            Console.WriteLine($"Alias: {restLine[..eq].Trim()} -> {restLine[(eq + 1)..].Trim()}");
        }

        private static void CmdBase64(string[] args, string restLine)
        {
            if (args.Length < 1) { Console.WriteLine("Usage: base64 encode|decode <text>"); return; }
            string payload = restLine.Length > args[0].Length ? restLine[args[0].Length..].Trim() : "";
            try
            {
                if (args[0].Equals("encode", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
                else if (args[0].Equals("decode", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                else Console.WriteLine("Usage: base64 encode|decode <text>");
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
        }

        private static void CmdJson(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("Usage: json <file>"); return; }
            string path = ResolvePath(args[0]);
            if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return; }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Console.WriteLine($"Invalid JSON: {ex.Message}"); }
        }
    }
}
