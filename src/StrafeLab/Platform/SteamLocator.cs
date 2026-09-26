using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;

namespace StrafeLab.Platform;

public sealed class SteamLocator
{
    public IReadOnlyList<string> FindSteamLibraries()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steam = ReadSteamPath();
        if (!string.IsNullOrWhiteSpace(steam))
        {
            candidates.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    var text = File.ReadAllText(vdf);
                    foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                    {
                        candidates.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
                catch { }
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            var common = Path.Combine(root, "SteamLibrary");
            if (Directory.Exists(common))
            {
                candidates.Add(common);
            }
        }

        return candidates.Where(Directory.Exists).ToArray();
    }

    public IReadOnlyList<string> FindCs2CfgDirectories()
    {
        return FindSteamLibraries()
            .Select(x => Path.Combine(x, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg"))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> FindDemoRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[]
        {
            Path.Combine(home, "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wmpvp", "demo"),
            Path.Combine(AppContext.BaseDirectory,"incoming")
        })
        {
            roots.Add(root);
        }

        foreach (var library in FindSteamLibraries())
        {
            var install = Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive");
            foreach (var root in new[]
            {
                Path.Combine(install, "game", "csgo"),
                Path.Combine(install, "csgo"),
                Path.Combine(install, "game", "csgo", "replays")
            })
            {
                if (Directory.Exists(root)) roots.Add(root);
            }
        }

        return roots.ToArray();
    }

    private static string? ReadSteamPath()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view)
                    .OpenSubKey("Software\\Valve\\Steam");
                var value = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
        }

        return null;
    }
}
