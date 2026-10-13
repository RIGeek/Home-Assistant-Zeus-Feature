// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zeus.Community.HomeAssistant.Ha;

[JsonConverter(typeof(JsonStringEnumConverter<TileKind>))]
internal enum TileKind
{
    [JsonStringEnumMemberName("toggle")] Toggle,
    [JsonStringEnumMemberName("button")] Button,
    [JsonStringEnumMemberName("sensor")] Sensor,
}

/// <summary>Bar for a numeric sensor. Zones are optional; with LowIsBad the colours run the other way (battery, signal).</summary>
internal sealed record TileMeter(double Min, double Max, double? Warn, double? Bad, bool LowIsBad);

/// <summary>One button or readout in the panel, bound to a single Home Assistant entity.</summary>
internal sealed record Tile(
    string Id, string EntityId, TileKind Kind, string? Label, string? Group,
    bool LockTx, bool Confirm, TileMeter? Meter, int? Digits);

/// <summary>What the panel sends when it saves the tile list. Everything is checked by <see cref="Tiles.Validate"/>.</summary>
internal sealed record TileInput(
    string? Id, string? EntityId, string? Kind, string? Label, string? Group,
    bool? LockTx, bool? Confirm, TileMeter? Meter, int? Digits);

/// <summary>
/// Which entities may become which kind of tile, and which Home Assistant service each kind calls.
/// The panel never names a service: it presses a tile, and the service comes from this table.
/// Locks, covers, alarm panels and climate are deliberately absent (doors and heat are not
/// something to operate from a radio panel by accident).
/// </summary>
internal static partial class Tiles
{
    public const int MaxTiles = 64;
    public const int MaxLabel = 40;
    public const int MaxGroup = 24;

    private static readonly HashSet<string> ToggleDomains =
        ["switch", "light", "fan", "input_boolean", "automation", "siren"];

    private static readonly Dictionary<string, string> PressServices = new()
    {
        ["button"] = "press",
        ["input_button"] = "press",
        ["script"] = "turn_on",
        ["scene"] = "turn_on",
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[a-z0-9_]{1,64}\\.[a-z0-9_]{1,190}$")]
    private static partial Regex EntityIdPattern();

    [GeneratedRegex("^[a-z0-9]{1,16}$")]
    private static partial Regex TileIdPattern();

    public static bool IsEntityId(string? s) => s is not null && EntityIdPattern().IsMatch(s);

    public static string DomainOf(string entityId) => entityId[..entityId.IndexOf('.')];

    /// <summary>Every entity can be shown as a readout; switches and buttons can also be operated.</summary>
    public static TileKind[] KindsFor(string domain) =>
        ToggleDomains.Contains(domain) ? [TileKind.Toggle, TileKind.Sensor]
        : PressServices.ContainsKey(domain) ? [TileKind.Button, TileKind.Sensor]
        : [TileKind.Sensor];

    public static TileKind DefaultKind(string domain) => KindsFor(domain)[0];

    /// <summary>The (domain, service) pair for an action on a tile, or null when that action does not apply.</summary>
    public static (string Domain, string Service)? ServiceFor(Tile tile, bool? on)
    {
        var domain = DomainOf(tile.EntityId);
        return tile.Kind switch
        {
            TileKind.Toggle when on is bool b && ToggleDomains.Contains(domain) => (domain, b ? "turn_on" : "turn_off"),
            TileKind.Button when on is null && PressServices.TryGetValue(domain, out var s) => (domain, s),
            _ => null,
        };
    }

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>Checks and normalises a whole tile list. Returns the list or an error for the panel.</summary>
    public static (IReadOnlyList<Tile>? Tiles, string? Error) Validate(IReadOnlyList<TileInput>? input)
    {
        if (input is null) return (null, "Tiles must be a list");
        if (input.Count > MaxTiles) return (null, $"At most {MaxTiles} tiles");

        var seen = new HashSet<string>();
        var output = new List<Tile>(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            var t = input[i];
            var n = i + 1;
            if (t is null) return (null, $"Tile {n} is empty");
            if (!IsEntityId(t.EntityId)) return (null, $"Tile {n}: not a valid entity id");
            var domain = DomainOf(t.EntityId!);

            TileKind kind;
            if (string.IsNullOrEmpty(t.Kind)) kind = DefaultKind(domain);
            else if (!Enum.TryParse(t.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind))
                return (null, $"Tile {n}: unknown kind");
            if (!KindsFor(domain).Contains(kind))
                return (null, $"Tile {n}: a {domain} entity cannot be a {kind.ToString().ToLowerInvariant()}");

            var id = t.Id is { } x && TileIdPattern().IsMatch(x) ? x : NewId();
            while (!seen.Add(id)) id = NewId();

            var label = Clean(t.Label, MaxLabel);
            var group = Clean(t.Group, MaxGroup);

            TileMeter? meter = null;
            if (kind == TileKind.Sensor && t.Meter is { } m)
            {
                if (!double.IsFinite(m.Min) || !double.IsFinite(m.Max) || !(m.Min < m.Max))
                    return (null, $"Tile {n}: meter minimum must be below maximum");
                if (m.Warn is { } w && !double.IsFinite(w) || m.Bad is { } b && !double.IsFinite(b))
                    return (null, $"Tile {n}: meter thresholds must be numbers");
                if (m is { Warn: { } ww, Bad: { } bb } && (m.LowIsBad ? ww < bb : ww > bb))
                    return (null, m.LowIsBad
                        ? $"Tile {n}: with low-is-bad, yellow must not be below red"
                        : $"Tile {n}: yellow must not be above red");
                meter = m;
            }

            if (t.Digits is { } d && d is < 0 or > 3) return (null, $"Tile {n}: decimals must be 0 to 3");

            output.Add(new Tile(id, t.EntityId!, kind, label, group,
                LockTx: kind != TileKind.Sensor && (t.LockTx ?? true),
                Confirm: kind != TileKind.Sensor && (t.Confirm ?? false),
                meter, kind == TileKind.Sensor ? t.Digits : null));
        }
        return (output, null);
    }

    /// <summary>Trimmed, control characters removed, length-capped; null when empty.</summary>
    private static string? Clean(string? s, int max)
    {
        if (s is null) return null;
        var chars = s.Where(c => !char.IsControl(c)).ToArray();
        var text = new string(chars).Trim();
        if (text.Length > max) text = text[..max].TrimEnd();
        return text.Length == 0 ? null : text;
    }

    public static string Serialize(IReadOnlyList<Tile> tiles) => JsonSerializer.Serialize(tiles, Json);

    /// <summary>Reads a stored list; anything unreadable is dropped rather than stopping the feature.</summary>
    public static IReadOnlyList<Tile> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<TileInput>>(json, Json);
            var (tiles, _) = Validate(raw);
            return tiles ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
