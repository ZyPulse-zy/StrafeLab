using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StrafeLab.Core.Gsi;

public static class GsiSnapshotParser
{
    public static GsiSnapshot Parse(string json, long receivedAtUs)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var map = GetObject(root, "map");
        var round = GetObject(root, "round");
        var player = GetObject(root, "player");
        var state = GetObject(player, "state");
        var weaponName = FindActiveWeaponName(player);
        var phaseCountdowns = GetObject(root, "phase_countdowns");

        return new GsiSnapshot
        {
            ReceivedAtUs = receivedAtUs,
            Map = GetString(map, "name"),
            MapPhase = GetString(map, "phase"),
            Round = GetInt(map, "round"),
            RoundPhase = GetString(round, "phase"),
            PlayerSteamId = GetString(player, "steamid"),
            ProviderSteamId = GetString(GetObject(root,"provider"),"steamid"),
            AmmoClip = GetInt(FindActiveWeapon(player), "ammo_clip"),
            PlayerName = GetString(player, "name"),
            PlayerActivity = GetString(player, "activity"),
            WeaponName = weaponName,
            WeaponState = weaponName is null ? null : "active",
            Health = GetInt(state, "health"),
            Armor = GetInt(state, "armor"),
            IsAlive = GetBool(state, "health") is null ? null : GetInt(state, "health") > 0,
            PhaseCountdownSeconds = GetDouble(phaseCountdowns, "phase_ends_in"),
            GameTimeMs = GetLong(GetObject(root, "provider"), "timestamp") * 1000,
            RawHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()
        };
    }

    private static string? FindActiveWeaponName(JsonElement player) => GetString(FindActiveWeapon(player), "name");
    private static JsonElement FindActiveWeapon(JsonElement player)
    {
        var weapons = GetObject(player, "weapons");
        if (weapons.ValueKind == JsonValueKind.Object)
            foreach (var w in weapons.EnumerateObject())
                if (GetString(w.Value, "state") == "active") return w.Value;
        return default;
    }

    private static JsonElement GetObject(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child)
            ? child
            : default;

    private static string? GetString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
           child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;

    private static int? GetInt(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
           child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var number)
            ? number
            : null;

    private static long? GetLong(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
           child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var number)
            ? number
            : null;

    private static double? GetDouble(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
           child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child)
            ? child.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when child.TryGetInt32(out var n) => n != 0,
                _ => null
            }
            : null;
}
