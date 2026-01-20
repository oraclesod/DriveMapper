using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using System.Diagnostics;

namespace DriveMapper
{
    class Program
    {
        private const string DrivesBaseKey = @"Software\Policies\DriveMapper\Drives";

        // Track what this tool last applied so we can safely remove mappings when policy keys disappear
        // (prevents us from unmapping drives we didn't create).
        private const string StateBaseKey = @"Software\DriveMapper\State\AppliedDrives";

        // WNetAddConnection2 flags
        private const int CONNECT_UPDATE_PROFILE = 0x00000001; // persistent
        private const int CONNECT_TEMPORARY      = 0x00000004; // not persistent

        // WNetGetConnection error codes
        private const int NO_ERROR = 0;
        private const int ERROR_NOT_CONNECTED = 2250;

        // ----------------------------
        // Event Log
        // ----------------------------

        private static class EventLogger
        {
            private const string LogName = "Application";
            private const string SourceName = "DriveMapper";

            private static bool _ready;
            private static bool _disabled;

            public static void Info(string message) => Write(EventLogEntryType.Information, message);
            public static void Warn(string message) => Write(EventLogEntryType.Warning, message);
            public static void Error(string message) => Write(EventLogEntryType.Error, message);

            private static void EnsureSource()
            {
                if (_ready || _disabled) return;

                try
                {
                    // Creating an event source typically requires admin.
                    if (!EventLog.SourceExists(SourceName))
                    {
                        var csd = new EventSourceCreationData(SourceName, LogName);
                        EventLog.CreateEventSource(csd);
                    }
                    _ready = true;
                }
                catch
                {
                    // No permissions to create/check source; fall back to console.
                    _disabled = true;
                }
            }

            private static void Write(EventLogEntryType type, string message)
            {
                EnsureSource();

                if (_disabled)
                {
                    Console.WriteLine($"[{type}] {message}");
                    return;
                }

                try
                {
                    EventLog.WriteEntry(SourceName, message, type);
                }
                catch
                {
                    Console.WriteLine($"[{type}] {message}");
                }
            }
        }

        static void Main(string[] args)
        {
            try
            {
                // Load effective policy config: HKLM first, HKCU overwrites per-drive if present
                var effective = LoadEffectivePolicyEntries();

                // Load state of previously applied mappings (so we can remove if policy is removed)
                var previouslyApplied = LoadAppliedState(); // letter -> last remote path

                // Process A..Z
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    string letter = c.ToString();
                    effective.TryGetValue(letter, out var entry);

                    string? currentRemote = GetCurrentMappedRemote(letter); // null means not mapped
                    string? currentLabel = GetCurrentDriveLabel(letter);    // may be null if not accessible

                    bool hadState = previouslyApplied.TryGetValue(letter, out _);

                    if (entry != null)
                    {
                        // Policy exists for this letter (either HKLM or HKCU, with HKCU override)
                        if (!entry.Enabled)
                        {
                            // Disabled => remove mapping if we previously applied it
                            if (hadState)
                            {
                                RemoveMappingIfPresent(letter, currentRemote, reason: "Policy disabled");
                                DeleteAppliedState(letter);
                            }
                            else
                            {
                                // Not our mapping; ignore.
                            }
                            continue;
                        }

                        // Enabled but Path missing => log & treat as "do nothing", or remove if we previously applied it
                        if (string.IsNullOrWhiteSpace(entry.Path))
                        {
                            EventLogger.Warn($"Policy mapping {letter}: is enabled but Path is empty; skipping.");
                            if (hadState)
                            {
                                RemoveMappingIfPresent(letter, currentRemote, reason: "Policy enabled but Path missing");
                                DeleteAppliedState(letter);
                            }
                            continue;
                        }

                        // Decide if remap is needed
                        bool pathMatches = currentRemote != null &&
                                           currentRemote.Equals(entry.Path, StringComparison.OrdinalIgnoreCase);

                        bool nameMatches = true; // default "not blocking" if we can't reliably determine label
                        if (!string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(currentLabel))
                        {
                            nameMatches = currentLabel.Equals(entry.Name, StringComparison.OrdinalIgnoreCase);
                        }

                        if (pathMatches && nameMatches)
                        {
                            // Already correct => nothing to do
                            SaveAppliedState(letter, entry.Path);
                            EventLogger.Info($"Skipped {letter}: already mapped to {entry.Path}.");
                            continue;
                        }

                        // If currently mapped but wrong, remap
                        MapDrive(letter, entry.Path, entry.Reconnect);

                        // Persist state so we can remove later if policy disappears
                        SaveAppliedState(letter, entry.Path);
                    }
                    else
                    {
                        // No policy key exists for this letter.
                        // Only remove if we previously applied it (tracked state).
                        if (hadState)
                        {
                            RemoveMappingIfPresent(letter, currentRemote, reason: "Policy no longer defines mapping");
                            DeleteAppliedState(letter);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EventLogger.Error($"Unhandled exception in DriveMapper: {ex}");
            }
        }

        // ----------------------------
        // Policy Reading (HKLM then HKCU overwrite)
        // ----------------------------

        private sealed class PolicyEntry
        {
            public bool Enabled { get; set; }
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public bool Reconnect { get; set; }
        }

        private static Dictionary<string, PolicyEntry> LoadEffectivePolicyEntries()
        {
            var merged = new Dictionary<string, PolicyEntry>(StringComparer.OrdinalIgnoreCase);

            // Machine first
            var machine = ReadPolicyEntriesFromHive(RegistryHive.LocalMachine);
            foreach (var kv in machine)
                merged[kv.Key] = kv.Value;

            // User overwrites machine if user key exists for that letter
            var user = ReadPolicyEntriesFromHive(RegistryHive.CurrentUser);
            foreach (var kv in user)
                merged[kv.Key] = kv.Value;

            return merged;
        }

        // Reads entries for letters that actually have a subkey present under DrivesBaseKey.
        // Tries Registry64 then Registry32 (defensive).
        private static Dictionary<string, PolicyEntry> ReadPolicyEntriesFromHive(RegistryHive hive)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var drivesKey = baseKey.OpenSubKey(DrivesBaseKey, writable: false);
                    if (drivesKey == null) continue;

                    var result = new Dictionary<string, PolicyEntry>(StringComparer.OrdinalIgnoreCase);

                    foreach (var sub in drivesKey.GetSubKeyNames())
                    {
                        if (string.IsNullOrWhiteSpace(sub) || sub.Length != 1) continue;
                        char c = char.ToUpperInvariant(sub[0]);
                        if (c < 'A' || c > 'Z') continue;

                        using var letterKey = drivesKey.OpenSubKey(sub, writable: false);
                        if (letterKey == null) continue;

                        var entry = new PolicyEntry
                        {
                            Enabled = ReadBool(letterKey, "Enabled", defaultValue: false),
                            Name = ReadString(letterKey, "Name"),
                            Path = ReadString(letterKey, "Path"),
                            Reconnect = ReadBool(letterKey, "Reconnect", defaultValue: false)
                        };

                        result[c.ToString()] = entry;
                    }

                    // If we found the Drives key in this view, return it (avoid duplicates)
                    return result;
                }
                catch (Exception ex)
                {
                    EventLogger.Warn($"Failed reading policy entries from {hive} ({view}): {ex.Message}");
                    // Keep going; try other view
                }
            }

            return new Dictionary<string, PolicyEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private static string ReadString(RegistryKey key, string valueName)
            => key.GetValue(valueName, "") as string ?? "";

        private static bool ReadBool(RegistryKey key, string valueName, bool defaultValue)
        {
            object? v = key.GetValue(valueName, null);
            if (v == null) return defaultValue;

            if (v is int i) return i != 0;
            if (v is long l) return l != 0;

            if (v is string s)
            {
                if (bool.TryParse(s, out var b)) return b;
                if (int.TryParse(s, out var n)) return n != 0;
            }

            return defaultValue;
        }

        // ----------------------------
        // Applied State (so we remove only what we manage)
        // ----------------------------

        private static Dictionary<string, string> LoadAppliedState()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var stateKey = baseKey.OpenSubKey(StateBaseKey, writable: false);
                if (stateKey == null) return result;

                foreach (var name in stateKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name) || name.Length != 1) continue;
                    char c = char.ToUpperInvariant(name[0]);
                    if (c < 'A' || c > 'Z') continue;

                    var val = stateKey.GetValue(name, "") as string ?? "";
                    if (!string.IsNullOrWhiteSpace(val))
                        result[c.ToString()] = val;
                }
            }
            catch (Exception ex)
            {
                EventLogger.Warn($"Failed reading applied-state registry: {ex.Message}");
            }

            return result;
        }

        private static void SaveAppliedState(string driveLetter, string remotePath)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var stateKey = baseKey.CreateSubKey(StateBaseKey, writable: true);
                stateKey?.SetValue(driveLetter.ToUpperInvariant(), remotePath ?? "", RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                EventLogger.Warn($"Failed saving applied-state for {driveLetter}: {ex.Message}");
            }
        }

        private static void DeleteAppliedState(string driveLetter)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var stateKey = baseKey.OpenSubKey(StateBaseKey, writable: true);
                stateKey?.DeleteValue(driveLetter.ToUpperInvariant(), throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                EventLogger.Warn($"Failed deleting applied-state for {driveLetter}: {ex.Message}");
            }
        }

        // ----------------------------
        // Mapping / Unmapping logic
        // ----------------------------

        private static void MapDrive(string driveLetter, string remotePath, bool reconnect)
        {
            // Remove existing mapping first (force)
            WNetCancelConnection2(driveLetter + ":", 0, true);

            int flags = reconnect ? CONNECT_UPDATE_PROFILE : CONNECT_TEMPORARY;

            var nr = new NETRESOURCE
            {
                dwType = 1,
                lpLocalName = driveLetter + ":",
                lpRemoteName = remotePath,
                lpProvider = null
            };

            int result = WNetAddConnection2(nr, null, null, flags);
            if (result != 0)
            {
                EventLogger.Error($"Failed to map drive {driveLetter}: to {remotePath}. Error code: {result}");
            }
            else
            {
                EventLogger.Info($"Mapped {driveLetter}: -> {remotePath} (Reconnect={(reconnect ? "Yes" : "No")})");
            }
        }

        private static void RemoveMappingIfPresent(string driveLetter, string? currentRemote, string reason)
        {
            if (currentRemote == null)
                return;

            int result = WNetCancelConnection2(driveLetter + ":", 0, true);
            if (result != 0)
            {
                EventLogger.Error($"Failed to remove drive {driveLetter}: (was {currentRemote}). Reason: {reason}. Error code: {result}");
            }
            else
            {
                EventLogger.Info($"Removed {driveLetter}: (was {currentRemote}). Reason: {reason}");
            }
        }

        // Returns remote path if drive is currently mapped, else null
        private static string? GetCurrentMappedRemote(string driveLetter)
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;

            int res = WNetGetConnection(driveLetter + ":", sb, ref len);
            if (res == NO_ERROR)
                return sb.ToString();

            if (res == ERROR_NOT_CONNECTED)
                return null;

            // Other errors -> treat as not mapped
            EventLogger.Warn($"WNetGetConnection({driveLetter}:) returned error code {res}.");
            return null;
        }

        // Best-effort. For many network drives this may not be readable.
        private static string? GetCurrentDriveLabel(string driveLetter)
        {
            try
            {
                var root = driveLetter + @":\";
                if (!Directory.Exists(root)) return null;

                var di = new DriveInfo(root);
                return di.VolumeLabel;
            }
            catch
            {
                return null;
            }
        }

        // ----------------------------
        // P/Invokes
        // ----------------------------

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(NETRESOURCE netResource, string? password, string? username, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public class NETRESOURCE
        {
            public int dwScope = 0;
            public int dwType = 1;
            public int dwDisplayType = 0;
            public int dwUsage = 0;
            public string lpLocalName = "";
            public string lpRemoteName = "";
            public string? lpComment = "";
            public string? lpProvider = "";
        }
    }
}
