using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Install
{
    class Program
    {
        // Event log source to match DriveMapper logging
        private const string EventLogName = "Application";
        private const string EventLogSourceName = "DriveMapper";

        // BUILTIN\Users SID
        private const string BuiltinUsersSid = "S-1-5-32-545";

        // Exit codes (helpful for Intune troubleshooting)
        private const int ExitOk = 0;
        private const int ExitBadArgs = 1;
        private const int ExitConfigError = 2;
        private const int ExitBlockedDowngrade = 3;
        private const int ExitFailed = 10;

        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: Install.exe <config.json> <install|uninstall|upgrade>");
                return ExitBadArgs;
            }

            string configPath = args[0];
            string mode = (args[1] ?? "").Trim().ToLowerInvariant();

            if (mode != "install" && mode != "uninstall" && mode != "upgrade")
            {
                Console.WriteLine("Usage: Install.exe <config.json> <install|uninstall|upgrade>");
                return ExitBadArgs;
            }

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Config file not found: {configPath}");
                return ExitConfigError;
            }

            Config config;
            try
            {
                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse config JSON: {ex.Message}");
                return ExitConfigError;
            }

            if (config == null)
            {
                Console.WriteLine("Config is empty/invalid.");
                return ExitConfigError;
            }

            if (string.IsNullOrWhiteSpace(config.TargetDirectory) || string.IsNullOrWhiteSpace(config.ExeName))
            {
                Console.WriteLine("Config must include TargetDirectory and ExeName.");
                return ExitConfigError;
            }

            config.ScheduledTasks ??= new List<ScheduledTaskConfig>();

            // ExeKey is used for registry key path HKLM\SOFTWARE\<ExeKey>
            string exeKey = Path.GetFileNameWithoutExtension(config.ExeName) ?? "App";
            string targetExePath = Path.Combine(config.TargetDirectory, config.ExeName);

            try
            {
                switch (mode)
                {
                    case "install":
                        // Downgrade protection BEFORE touching anything
                        if (IsDowngradeAttempt(exeKey, config.Version, out var installedVersion, out var requestedVersion))
                        {
                            Console.WriteLine($"Blocked: installed version ({installedVersion}) is newer than requested ({requestedVersion}).");
                            Console.WriteLine("Uninstall the newer version first (or use Intune supersedence to uninstall old app then install new).");
                            return ExitBlockedDowngrade;
                        }

                        DoInstall(config, exeKey);
                        Console.WriteLine("Installation complete.");
                        return ExitOk;

                    case "uninstall":
                        // Supersedence Model 1: Intune runs uninstall for the old app, then installs the new app.
                        // Uninstall should be as complete as possible (tasks, shortcut, files, registry marker).
                        DoUninstall(config, exeKey, removeEventLogSource: false);
                        Console.WriteLine("Uninstallation complete.");
                        return ExitOk;

                    case "upgrade":
                        // Also protected from downgrades (upgrade shouldn't downgrade either)
                        if (IsDowngradeAttempt(exeKey, config.Version, out installedVersion, out requestedVersion))
                        {
                            Console.WriteLine($"Blocked: installed version ({installedVersion}) is newer than requested ({requestedVersion}).");
                            Console.WriteLine("Uninstall the newer version first.");
                            return ExitBlockedDowngrade;
                        }

                        DoUpgrade(config, exeKey, targetExePath);
                        Console.WriteLine("Upgrade complete.");
                        return ExitOk;

                    default:
                        Console.WriteLine("Unknown mode.");
                        return ExitBadArgs;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Operation failed: {ex}");
                return ExitFailed;
            }
        }

        // =====================
        // Intune Supersedence “Model 1” support
        // =====================
        // Intune performs: Detect old app -> Run old uninstall -> Install new -> Detect new.
        // Code support needed:
        //  - Uninstall should remove canonical tasks even if config differs between versions.
        //  - Use registry Version for detection & downgrade protection.

        private static void DoUpgrade(Config newConfig, string exeKey, string targetExePath)
        {
            bool installed = IsInstalled(exeKey, targetExePath);
            string oldVersion = GetInstalledVersion(exeKey);

            if (installed)
            {
                Console.WriteLine($"Detected existing install ({exeKey}) version: {oldVersion ?? "Unknown"}");

                // Prefer uninstall using the originally-installed config, if available (handles renamed tasks/dirs/shortcut).
                Config oldConfig = GetInstalledConfig(exeKey) ?? newConfig;

                Console.WriteLine("Uninstalling existing version...");
                DoUninstall(oldConfig, exeKey, removeEventLogSource: false);
            }
            else
            {
                Console.WriteLine("No existing install detected. Performing fresh install...");
            }

            Console.WriteLine($"Installing version: {newConfig.Version ?? "Unknown"}");
            DoInstall(newConfig, exeKey);
        }

        private static void DoInstall(Config config, string exeKey)
        {
            Directory.CreateDirectory(config.TargetDirectory);

            string currentDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (string file in Directory.GetFiles(currentDir))
            {
                string fileName = Path.GetFileName(file);
                string destPath = Path.Combine(config.TargetDirectory, fileName);
                File.Copy(file, destPath, true);
            }

            string exePath = Path.Combine(config.TargetDirectory, config.ExeName);

            // Ensure event log source exists so DriveMapper can write to Application log without admin
            EnsureEventLogSource();

            foreach (var task in config.ScheduledTasks)
            {
                string baseName = task.TaskName ?? config.ExeName;

                if (task.CreateLogonTask)
                    CreateScheduledTask(baseName, exePath, task.Arguments, TaskTriggerType.Logon);

                if (task.CreateNetworkTask)
                    CreateScheduledTask(baseName + "_NetworkChange", exePath, task.Arguments, TaskTriggerType.NetworkProfile);

                if (task.CreateBootTask)
                    CreateScheduledTask(baseName + "_Boot", exePath, task.Arguments, TaskTriggerType.Boot);
            }

            if (!string.IsNullOrWhiteSpace(config.ShortcutName))
                CreateStartMenuShortcut(config.ShortcutName, exePath);

            // Write install markers to registry under HKLM\Software\<ExeKey>
            using (var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\{exeKey}"))
            {
                key.SetValue("Version", config.Version ?? "Unknown", RegistryValueKind.String);

                // Store the config used for install so upgrade can uninstall accurately even if config changes later.
                key.SetValue("InstalledConfig", JsonConvert.SerializeObject(config), RegistryValueKind.String);

                key.SetValue("InstallTimeUtc", DateTime.UtcNow.ToString("o"), RegistryValueKind.String);
            }
        }

        private static void DoUninstall(Config config, string exeKey, bool removeEventLogSource)
        {
            using (TaskService ts = new TaskService())
            {
                // 1) Delete tasks listed in config (best-effort)
                foreach (var task in (config.ScheduledTasks ?? new List<ScheduledTaskConfig>()))
                {
                    string baseName = task.TaskName ?? config.ExeName;

                    // Even if flags are false, previous versions might have created them.
                    // We can delete all variants safely.
                    SafeDeleteTask(ts, baseName);
                    SafeDeleteTask(ts, baseName + "_NetworkChange");
                    SafeDeleteTask(ts, baseName + "_Boot");
                }

                // 2) Supersedence safety net: always delete canonical task names for this app
                // This prevents orphan tasks when old/new configs differ.
                string canonical = Path.GetFileNameWithoutExtension(config.ExeName) ?? config.ExeName;
                SafeDeleteTask(ts, canonical);
                SafeDeleteTask(ts, canonical + "_NetworkChange");
                SafeDeleteTask(ts, canonical + "_Boot");

                // 3) Also try the literal ExeName-based names (older behavior)
                SafeDeleteTask(ts, config.ExeName);
                SafeDeleteTask(ts, config.ExeName + "_NetworkChange");
                SafeDeleteTask(ts, config.ExeName + "_Boot");
            }

            // Remove shortcut (machine-wide)
            if (!string.IsNullOrWhiteSpace(config.ShortcutName))
            {
                string startMenuPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    "Programs",
                    config.ShortcutName + ".lnk"
                );

                try
                {
                    if (File.Exists(startMenuPath))
                        File.Delete(startMenuPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: failed to remove shortcut: {ex.Message}");
                }
            }

            // Remove files
            try
            {
                if (Directory.Exists(config.TargetDirectory))
                    Directory.Delete(config.TargetDirectory, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to remove directory '{config.TargetDirectory}': {ex.Message}");
            }

            // Remove registry marker
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\{exeKey}", false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to remove registry key HKLM\\SOFTWARE\\{exeKey}: {ex.Message}");
            }

            // Optional: remove event log source on uninstall (generally better to keep it)
            if (removeEventLogSource)
            {
                RemoveEventLogSource();
            }
        }

        private static void SafeDeleteTask(TaskService ts, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try { ts.RootFolder.DeleteTask(name, false); }
            catch { /* ignore */ }
        }

        // =====================
        // Downgrade protection
        // =====================

        private static bool IsDowngradeAttempt(string exeKey, string requestedVersionRaw, out string installedRaw, out string requestedRaw)
        {
            installedRaw = GetInstalledVersion(exeKey);
            requestedRaw = requestedVersionRaw ?? "Unknown";

            // If not installed, cannot be downgrade
            if (string.IsNullOrWhiteSpace(installedRaw))
                return false;

            // If requested isn't parseable, do NOT block (avoid false positives)
            if (!TryParseVersion(requestedRaw, out var requested))
                return false;

            // If installed isn't parseable, do NOT block (avoid false positives)
            if (!TryParseVersion(installedRaw, out var installed))
                return false;

            // Downgrade attempt if installed > requested
            return installed > requested;
        }

        private static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Normalize "1.2" -> "1.2" is fine; Version.TryParse supports 2-4 components
            return Version.TryParse(s.Trim(), out v);
        }

        // =====================
        // Install detection / stored config
        // =====================

        private static bool IsInstalled(string exeKey, string targetExePath)
        {
            var v = GetInstalledVersion(exeKey);
            if (!string.IsNullOrWhiteSpace(v)) return true;
            return File.Exists(targetExePath);
        }

        private static string GetInstalledVersion(string exeKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\{exeKey}");
                return key?.GetValue("Version") as string;
            }
            catch { return null; }
        }

        private static Config GetInstalledConfig(string exeKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\{exeKey}");
                var json = key?.GetValue("InstalledConfig") as string;
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonConvert.DeserializeObject<Config>(json);
            }
            catch { return null; }
        }

        // =====================
        // Event log source
        // =====================

        private static void EnsureEventLogSource()
        {
            try
            {
                if (!EventLog.SourceExists(EventLogSourceName))
                {
                    var data = new EventSourceCreationData(EventLogSourceName, EventLogName);
                    EventLog.CreateEventSource(data);
                }

                EventLog.WriteEntry(
                    EventLogSourceName,
                    "DriveMapper event log source created/verified by installer.",
                    EventLogEntryType.Information
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to create/verify Event Log source '{EventLogSourceName}': {ex.Message}");
            }
        }

        private static void RemoveEventLogSource()
        {
            try
            {
                if (EventLog.SourceExists(EventLogSourceName))
                    EventLog.DeleteEventSource(EventLogSourceName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to remove Event Log source '{EventLogSourceName}': {ex.Message}");
            }
        }

        // =====================
        // Scheduled tasks (Intune/SYSTEM-safe)
        // =====================

        static void CreateScheduledTask(string taskName, string exePath, string arguments, TaskTriggerType triggerType)
        {
            using (TaskService ts = new TaskService())
            {
                TaskDefinition td = ts.NewTask();
                td.RegistrationInfo.Description = $"Run {Path.GetFileName(exePath)} on {triggerType}";

                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.StartWhenAvailable = true;
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

                // Run for any logged-on user (critical for Intune SYSTEM installs)
                td.Principal.GroupId = BuiltinUsersSid;       // BUILTIN\Users
                td.Principal.LogonType = TaskLogonType.Group; // logged-on user's token
                td.Principal.RunLevel = TaskRunLevel.LUA;     // not elevated

                Trigger trigger = triggerType switch
                {
                    TaskTriggerType.Logon => new LogonTrigger
                    {
                        Delay = TimeSpan.FromSeconds(10),
                    },

                    TaskTriggerType.NetworkProfile => new EventTrigger
                    {
                        Subscription =
                            @"<QueryList><Query Id='0' Path='Microsoft-Windows-NetworkProfile/Operational'>
                                <Select Path='Microsoft-Windows-NetworkProfile/Operational'>
                                  *[System[Provider[@Name='Microsoft-Windows-NetworkProfile'] and (EventID=10000 or EventID=10001)]]
                                </Select>
                              </Query></QueryList>"
                    },

                    TaskTriggerType.Boot => new BootTrigger { Delay = TimeSpan.FromSeconds(10) },

                    _ => throw new ArgumentException("Unsupported trigger type")
                };

                td.Triggers.Add(trigger);
                td.Actions.Add(new ExecAction(exePath, arguments, null));

                ts.RootFolder.RegisterTaskDefinition(
                    taskName,
                    td,
                    TaskCreation.CreateOrUpdate,
                    userId: null,
                    password: null,
                    logonType: TaskLogonType.Group
                );
            }
        }

        // =====================
        // Shortcut creation (Common Start Menu)
        // =====================

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLink
        {
            void GetPath([Out] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        static void CreateStartMenuShortcut(string shortcutName, string targetPath)
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs",
                shortcutName + ".lnk"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));

            IShellLink link = (IShellLink)new ShellLink();
            link.SetDescription(shortcutName);
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath));

            ((IPersistFile)link).Save(shortcutPath, false);
        }

        enum TaskTriggerType
        {
            Logon,
            NetworkProfile,
            Boot
        }

        class Config
        {
            public string TargetDirectory { get; set; }
            public string ExeName { get; set; }
            public string ShortcutName { get; set; }
            public string Version { get; set; }
            public List<ScheduledTaskConfig> ScheduledTasks { get; set; }
        }

        class ScheduledTaskConfig
        {
            public string TaskName { get; set; }
            public string Arguments { get; set; }
            public bool CreateLogonTask { get; set; }
            public bool CreateNetworkTask { get; set; }
            public bool CreateBootTask { get; set; }
        }
    }
}
