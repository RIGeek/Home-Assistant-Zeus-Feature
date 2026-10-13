// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using Zeus.Community.HomeAssistant.Ha;

namespace Zeus.Community.HomeAssistant;

// ---- JSON shapes returned to the panel (serialized camelCase) ----------------

internal sealed record ApiStatus(ApiConnection Connection, ApiRadio Radio, IReadOnlyList<ApiTile> Tiles, string SampledUtc);

internal sealed record ApiConnection(string State, string? Url, string? Error, string? HaVersion, int Entities);

internal sealed record ApiRadio(bool Available, bool? Tx);

internal sealed record ApiTile(
    string Id, string EntityId, string Kind, string? Label, string? Group, bool LockTx, bool Confirm,
    TileMeter? Meter, int? Digits,
    string Domain, string? Name, string? State, double? Value, string? Unit, string? DeviceClass,
    bool Missing, bool Available, bool? On, bool Locked, string? LastChanged);

internal sealed record ApiEntity(string EntityId, string? Name, string Domain, string State, string? Unit, string[] Kinds);

/// <summary>
/// Everything the feature does, with no dependency on the Zeus SDK so that it can be
/// exercised directly by tests: the Home Assistant link, the tile list, and the lockout.
/// </summary>
internal sealed class HaService : IAsyncDisposable
{
    private static readonly HashSet<string> OnStates = ["on", "open", "home", "playing", "unlocked"];

    private readonly ISettingsStore _store;
    private readonly IRadioStateSource _radio;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private HaSettings _settings = HaSettings.Empty;

    public HaClient Client { get; }

    public HaService(ILogger log, ISettingsStore store, IRadioStateSource radio, HaClientOptions? options = null)
    {
        _store = store;
        _radio = radio;
        Client = new HaClient(log, options);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _settings = await _store.LoadAsync(ct).ConfigureAwait(false);
        await Client.StartAsync(_settings.Url, _settings.Token, _settings.AcceptAnyCertificate).ConfigureAwait(false);
    }

    public HaSettings GetSettings() => _settings;

    /// <summary>
    /// Changes the connection. A null field keeps its current value; an empty URL disconnects.
    /// The token is write-only: it can be replaced or cleared, never read back.
    /// </summary>
    public async Task<CommandResult> ApplyConnectionAsync(
        string? url, string? token, bool clearToken, bool? acceptAnyCertificate, CancellationToken ct)
    {
        string? nextUrl = _settings.Url;
        if (url is not null)
        {
            if (url.Trim().Length == 0) nextUrl = null;
            else if (HaUrl.Normalize(url) is { } u) nextUrl = u;
            else return CommandResult.Invalid("Enter the Home Assistant address, e.g. http://homeassistant.local:8123");
        }

        string? nextToken = _settings.Token;
        if (clearToken) nextToken = null;
        else if (!string.IsNullOrWhiteSpace(token))
        {
            var t = token.Trim();
            if (!HaUrl.LooksLikeToken(t)) return CommandResult.Invalid("That does not look like a long-lived access token");
            nextToken = t;
        }

        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = _settings with
            {
                Url = nextUrl,
                Token = nextToken,
                AcceptAnyCertificate = acceptAnyCertificate ?? _settings.AcceptAnyCertificate,
            };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _settings = next;
            // Always restart, even when nothing changed: it is how a rejected token is retried after fixing it in HA.
            await Client.StartAsync(next.Url, next.Token, next.AcceptAnyCertificate).ConfigureAwait(false);
            return CommandResult.Success(next.Url is null ? "Disconnected" : next.Token is null ? "Saved. Add an access token to connect." : "Connecting…");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<(CommandResult Result, IReadOnlyList<Tile>? Tiles)> SetTilesAsync(IReadOnlyList<TileInput>? input, CancellationToken ct)
    {
        var (tiles, error) = Tiles.Validate(input);
        if (tiles is null) return (CommandResult.Invalid(error!), null);

        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = _settings with { Tiles = tiles };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _settings = next;
            return (CommandResult.Success("Tiles saved"), tiles);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>Presses a button tile, or switches a toggle tile on or off. Only configured tiles can be operated.</summary>
    public async Task<CommandResult> OperateAsync(string? tileId, string? action, CancellationToken ct)
    {
        var tile = _settings.Tiles.FirstOrDefault(t => t.Id == tileId);
        if (tile is null) return CommandResult.Invalid("Unknown tile");

        bool? on;
        switch (action?.ToLowerInvariant())
        {
            case "on": on = true; break;
            case "off": on = false; break;
            case "press": on = null; break;
            default: return CommandResult.Invalid("Unknown action");
        }
        if (Tiles.ServiceFor(tile, on) is not { } call) return CommandResult.Invalid("That action does not apply to this tile");

        if (tile.LockTx && _radio.Available)
        {
            switch (_radio.Transmitting)
            {
                case true: return CommandResult.Blocked("Locked while transmitting");
                case null: return CommandResult.Blocked("Cannot read the transmit state, so this tile is locked");
            }
        }

        var entity = Client.Get(tile.EntityId);
        if (Client.GetSnapshot().Link != LinkState.Connected) return CommandResult.Blocked("Not connected to Home Assistant");
        if (entity is null) return CommandResult.Blocked($"{tile.EntityId} no longer exists in Home Assistant");
        if (entity.State == "unavailable") return CommandResult.Blocked($"{Display(tile, entity)} is unavailable in Home Assistant");

        var result = await Client.CallServiceAsync(call.Domain, call.Service, tile.EntityId, ct).ConfigureAwait(false);
        return result.Ok ? CommandResult.Success(Describe(tile, entity, on)) : result;
    }

    private static string Display(Tile t, HaEntity? e) => t.Label ?? e?.FriendlyName ?? t.EntityId;

    private static string Describe(Tile t, HaEntity e, bool? on) =>
        on is null ? $"{Display(t, e)}: pressed" : $"{Display(t, e)}: {(on.Value ? "on" : "off")}";

    public ApiStatus BuildStatus()
    {
        var s = Client.GetSnapshot();
        var connected = s.Link == LinkState.Connected;
        bool? tx = _radio.Available ? _radio.Transmitting : null;

        var tiles = _settings.Tiles.Select(t =>
        {
            var e = s.Entities.TryGetValue(t.EntityId, out var x) ? x : null;
            var available = connected && e is { Unavailable: false };
            bool? isOn = t.Kind == TileKind.Toggle && available ? OnStates.Contains(e!.State) : null;
            return new ApiTile(
                t.Id, t.EntityId, t.Kind.ToString().ToLowerInvariant(), t.Label, t.Group, t.LockTx, t.Confirm,
                t.Meter, t.Digits,
                Tiles.DomainOf(t.EntityId), e?.FriendlyName, e?.State, e?.Numeric, e?.Unit, e?.DeviceClass,
                Missing: connected && e is null,
                Available: available,
                On: isOn,
                Locked: t.LockTx && _radio.Available && _radio.Transmitting != false,
                LastChanged: e is null || e.LastChanged == DateTimeOffset.MinValue ? null : e.LastChanged.ToString("O"));
        }).ToArray();

        var state = s.Link switch
        {
            LinkState.AuthFailed => "auth_failed",
            _ => s.Link.ToString().ToLowerInvariant(),
        };
        if (s.Link == LinkState.Unconfigured && _settings.Url is not null && _settings.Token is null) state = "no_token";

        return new ApiStatus(
            new ApiConnection(state, _settings.Url, s.Error, s.HaVersion, s.Entities.Count),
            new ApiRadio(_radio.Available, tx),
            tiles,
            DateTime.UtcNow.ToString("O"));
    }

    /// <summary>Entities for the tile picker: matches on id or name, operable kinds first within each match rank.</summary>
    public (bool Connected, int Total, IReadOnlyList<ApiEntity> Entities) Search(string? query, int limit)
    {
        var s = Client.GetSnapshot();
        var q = (query ?? string.Empty).Trim();
        var words = q.Length == 0 ? [] : q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = s.Entities.Values
            .Where(e => words.All(w =>
                e.EntityId.Contains(w, StringComparison.OrdinalIgnoreCase)
                || (e.FriendlyName?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderBy(e => Tiles.DefaultKind(e.Domain) == TileKind.Sensor ? 1 : 0)
            .ThenBy(e => e.EntityId, StringComparer.Ordinal)
            .ToList();
        var page = matches.Take(Math.Clamp(limit, 1, 200))
            .Select(e => new ApiEntity(e.EntityId, e.FriendlyName, e.Domain, e.State, e.Unit,
                Tiles.KindsFor(e.Domain).Select(k => k.ToString().ToLowerInvariant()).ToArray()))
            .ToArray();
        return (s.Link == LinkState.Connected, matches.Count, page);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _settingsLock.Dispose();
    }
}
