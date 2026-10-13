// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Community.HomeAssistant.Ha;
using Zeus.Plugins.Contracts;

namespace Zeus.Community.HomeAssistant;

/// <summary>Settings that survive a restart of Zeus. The token never leaves the backend.</summary>
internal sealed record HaSettings(
    string? Url, string? Token, bool AcceptAnyCertificate, IReadOnlyList<Tile> Tiles)
{
    public static HaSettings Empty { get; } = new(null, null, false, []);
}

internal interface ISettingsStore
{
    ValueTask<HaSettings> LoadAsync(CancellationToken ct);
    ValueTask SaveAsync(HaSettings settings, CancellationToken ct);
}

/// <summary>Whether the radio is transmitting, for tiles that lock during TX.</summary>
internal interface IRadioStateSource
{
    bool Available { get; }
    bool? Transmitting { get; }
}

/// <summary>
/// The ONLY place that touches Zeus-provided services beyond logging.
/// Settings use the plugin-scoped store; the transmit lockout reads MOX from
/// IRadioStateReader (ReadRadioState). The feature never calls IRadioController.
/// </summary>
internal static class HostBridge
{
    public static ISettingsStore CreateSettingsStore(IPluginContext context) =>
        new PluginSettingsStore(context.Settings);

    public static IRadioStateSource CreateRadioStateSource(IPluginContext context) =>
        context.Radio is { } radio ? new ZeusRadioStateSource(radio) : new UnavailableRadioStateSource();
}

internal sealed class PluginSettingsStore(IPluginSettings settings) : ISettingsStore
{
    private const string UrlKey = "url";
    private const string TokenKey = "token";
    private const string InsecureKey = "acceptAnyCertificate";
    private const string TilesKey = "tiles";

    public async ValueTask<HaSettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            var url = await settings.GetAsync<string>(UrlKey, ct).ConfigureAwait(false);
            var token = await settings.GetAsync<string>(TokenKey, ct).ConfigureAwait(false);
            var insecure = await settings.GetAsync<bool?>(InsecureKey, ct).ConfigureAwait(false);
            var tiles = await settings.GetAsync<string>(TilesKey, ct).ConfigureAwait(false);
            return new HaSettings(
                HaUrl.Normalize(url),
                string.IsNullOrWhiteSpace(token) ? null : token,
                insecure ?? false,
                Tiles.Deserialize(tiles));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HaSettings.Empty;   // unreadable settings must not stop the feature loading
        }
    }

    public async ValueTask SaveAsync(HaSettings s, CancellationToken ct)
    {
        await settings.SetAsync(UrlKey, s.Url ?? string.Empty, ct).ConfigureAwait(false);
        await settings.SetAsync(TokenKey, s.Token ?? string.Empty, ct).ConfigureAwait(false);
        await settings.SetAsync(InsecureKey, s.AcceptAnyCertificate, ct).ConfigureAwait(false);
        await settings.SetAsync(TilesKey, Tiles.Serialize(s.Tiles), ct).ConfigureAwait(false);
    }
}

internal sealed class ZeusRadioStateSource(IRadioStateReader reader) : IRadioStateSource
{
    public bool Available => true;

    public bool? Transmitting
    {
        get
        {
            try { return reader.Mox; }
            catch (Exception) { return null; }
        }
    }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    private HaSettings _current = HaSettings.Empty;
    public int Saves { get; private set; }

    public ValueTask<HaSettings> LoadAsync(CancellationToken ct) => new(_current);

    public ValueTask SaveAsync(HaSettings settings, CancellationToken ct)
    {
        _current = settings;
        Saves++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class UnavailableRadioStateSource : IRadioStateSource
{
    public bool Available => false;
    public bool? Transmitting => null;
}
