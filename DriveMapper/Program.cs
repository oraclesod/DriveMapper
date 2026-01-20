using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DriveMapper
{
    class Program
    {
        private const string DrivesBaseKey = @"Software\Policies\DriveMapper\Drives";

        // WNetAddConnection2 flags
        private const int CONNECT_UPDATE_PROFILE = 0x00000001; // persistent
        private const int CONNECT_TEMPORARY      = 0x00000004; // not persistent

        static void Main(string[] args)
        {
            var mappings = LoadMappingsFromPolicy();
            if (mappings.Count == 0)
            {
                Console.WriteLine("No enabled drive mappings found in policy registry.");
                return;
            }

            foreach (var mapping in mappings)
                MapDrive(mapping);
        }

        /// <summary>
        /// Loads mappings from HKLM then HKCU; HKCU overwrites HKLM by drive letter.
        /// Only includes Enabled=true items. Disabled items are ignored.
        /// </summary>
        static List<DriveMapping> LoadMappingsFromPolicy()
        {
            var merged = new Dictionary<string, DriveMapping>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in ReadEnabledMappingsFromHive(RegistryHive.LocalMachine))
                merged[m.DriveLetter] = m;

            foreach (var m in ReadEnabledMappingsFromHive(RegistryHive.CurrentUser))
                merged[m.DriveLetter] = m;

            return merged.Values
                .OrderBy(m => m.DriveLetter, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static IEnumerable<DriveMapping> ReadEnabledMappingsFromHive(RegistryHive hive)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var drivesKey = baseKey.OpenSubKey(DrivesBaseKey, writable: false);
            if (drivesKey == null) yield break;

            foreach (var letter in drivesKey.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(letter) || letter.Length != 1)
                    continue;

                char c = char.ToUpperInvariant(letter[0]);
                if (c < 'A' || c > 'Z')
                    continue;

                using var letterKey = drivesKey.OpenSubKey(letter, writable: false);
                if (letterKey == null) continue;

                bool enabled = ReadBool(letterKey, "Enabled", defaultValue: false);
                if (!enabled) continue;

                string name = ReadString(letterKey, "Name");
                string path = ReadString(letterKey, "Path");
                bool reconnect = ReadBool(letterKey, "Reconnect", defaultValue: false);

                if (string.IsNullOrWhiteSpace(path))
                    continue;

                yield return new DriveMapping
                {
                    Name = name,
                    Path = path,
                    DriveLetter = c.ToString(),
                    Reconnect = reconnect
                };
            }
        }

        static string ReadString(RegistryKey key, string valueName)
            => key.GetValue(valueName, "") as string ?? "";

        static bool ReadBool(RegistryKey key, string valueName, bool defaultValue)
        {
            object v = key.GetValue(valueName, null);
            if (v == null) return defaultValue;

            if (v is int i) return i != 0;
            if (v is long l) return l != 0;      // <-- add this
            if (v is string s)
            {
                if (bool.TryParse(s, out var b)) return b;
                if (int.TryParse(s, out var n)) return n != 0;  // <-- nice extra
            }

            return defaultValue;
        }


        static void MapDrive(DriveMapping mapping)
        {
            // Remove any existing mapping first
            WNetCancelConnection2(mapping.DriveLetter + ":", 0, true);

            int flags = mapping.Reconnect ? CONNECT_UPDATE_PROFILE : CONNECT_TEMPORARY;

            var result = WNetAddConnection2(new NETRESOURCE
            {
                dwType = 1,
                lpLocalName = mapping.DriveLetter + ":",
                lpRemoteName = mapping.Path,
                lpProvider = null
            }, null, null, flags);

            if (result != 0)
                Console.WriteLine($"Failed to map drive {mapping.DriveLetter}: to {mapping.Path}. Error code: {result}");
        }

        [DllImport("mpr.dll")]
        static extern int WNetAddConnection2(NETRESOURCE netResource, string password, string username, int flags);

        [DllImport("mpr.dll")]
        static extern int WNetCancelConnection2(string name, int flags, bool force);

        [StructLayout(LayoutKind.Sequential)]
        public class NETRESOURCE
        {
            public int dwScope = 0;
            public int dwType = 1;
            public int dwDisplayType = 0;
            public int dwUsage = 0;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment = "";
            public string lpProvider = "";
        }

        public class DriveMapping
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string DriveLetter { get; set; }
            public bool Reconnect { get; set; }
        }
    }
}
