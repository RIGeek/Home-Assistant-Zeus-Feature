// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;

namespace Zeus.Community.HomeAssistant.Ha;

internal enum LinkState { Unconfigured, Connecting, Connected, AuthFailed, Disconnected }

/// <summary>Outcome of a request. Status is the HTTP status the API layer should use.</summary>
internal sealed record CommandResult(bool Ok, string Message, int Status)
{
    public static CommandResult Success(string message) => new(true, message, 200);
    /// <summary>Refused because of the current state (not connected, transmitting, entity unavailable...).</summary>
    public static CommandResult Blocked(string message) => new(false, message, 409);
    /// <summary>Refused because the request itself was malformed.</summary>
    public static CommandResult Invalid(string message) => new(false, message, 400);
    /// <summary>Home Assistant answered with an error or did not answer.</summary>
    public static CommandResult Upstream(string message) => new(false, message, 502);
}

/// <summary>The parts of a Home Assistant state object the feature uses.</summary>
internal sealed record HaEntity(
    string EntityId, string State, string? FriendlyName, string? Unit, string? DeviceClass,
    DateTimeOffset LastChanged, DateTimeOffset LastUpdated)
{
    public string Domain => EntityId[..EntityId.IndexOf('.')];

    /// <summary>Only "unavailable" means unreachable; buttons and scenes sit at "unknown" until first used.</summary>
    public bool Unavailable => State == "unavailable";

    public double? Numeric =>
        double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}

internal sealed record HaSnapshot(
    LinkState Link, string? Url, string? Error, string? HaVersion,
    IReadOnlyDictionary<string, HaEntity> Entities, DateTime? ConnectedUtc);

internal static class HaUrl
{
    /// <summary>
    /// Accepts an http(s) base URL such as <c>http://homeassistant.local:8123</c> or
    /// <c>https://ha.example.com/</c>. No credentials, query or fragment. Returns the
    /// normalised form (no trailing slash) or null.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        if (text.Length > 300) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        if (u.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return null;
        var path = u.AbsolutePath.TrimEnd('/');
        return $"{u.Scheme}://{u.Authority}{path}";
    }

    /// <summary>The WebSocket API endpoint for a normalised base URL.</summary>
    public static Uri WebSocket(string baseUrl)
    {
        var u = new Uri(baseUrl);
        var scheme = u.Scheme == "https" ? "wss" : "ws";
        return new Uri($"{scheme}://{u.Authority}{u.AbsolutePath.TrimEnd('/')}/api/websocket");
    }

    /// <summary>Long-lived access tokens are JWTs: three base64url parts. Anything else is refused before it is sent.</summary>
    public static bool LooksLikeToken(string token) =>
        token.Length is >= 20 and <= 1024 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
