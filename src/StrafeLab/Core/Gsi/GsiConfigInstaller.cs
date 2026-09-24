using StrafeLab.Platform;
using System.IO;

namespace StrafeLab.Core.Gsi;

public sealed class GsiConfigInstaller
{
    private readonly SteamLocator _steamLocator;

    public GsiConfigInstaller(SteamLocator steamLocator)
    {
        _steamLocator = steamLocator;
    }

    public const string FileName = "gamestate_integration_strafelab.cfg";

    public IReadOnlyList<string> FindTargets() => _steamLocator.FindCs2CfgDirectories();

    public string BuildConfig(int port, string token)
    {
        return $"\"StrafeLab\"\n{{\n    \"uri\" \"http://127.0.0.1:{port}/gsi\"\n    \"timeout\" \"1.0\"\n    \"buffer\" \"0.1\"\n    \"throttle\" \"0.05\"\n    \"heartbeat\" \"0.5\"\n    \"auth\"\n    {{\n        \"token\" \"{token}\"\n    }}\n    \"data\"\n    {{\n        \"provider\" \"1\"\n        \"map\" \"1\"\n        \"player_id\" \"1\"\n        \"player_state\" \"1\"\n        \"round\" \"1\"\n        \"phase_countdowns\" \"1\"\n        \"player_weapons\" \"1\"\n    }}\n}}\n";
    }

    public IReadOnlyList<string> Install(int port, string token)
    {
        var written = new List<string>();
        foreach (var directory in FindTargets())
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temp = path + ".tmp";
            var backup = path + ".bak";
            if (File.Exists(path))
            {
                try { File.Copy(path, backup, overwrite: true); } catch { }
            }

            File.WriteAllText(temp, BuildConfig(port, token));
            File.Move(temp, path, overwrite: true);
            written.Add(path);
        }

        return written;
    }
}
