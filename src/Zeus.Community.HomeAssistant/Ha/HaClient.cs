// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Community.HomeAssistant.Ha;

internal sealed class HaClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReconnectMin { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>A large installation's get_states reply runs to a few MB; anything far beyond that is refused.</summary>
    public int MaxMessageBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>
/// One connection to Home Assistant's WebSocket API: authenticates with a long-lived access
/// token, keeps a live copy of every entity's state (get_states, then state_changed events),
/// and calls services. Reconnects by itself with backoff, except after a rejected token:
/// retrying a bad token only feeds Home Assistant's login-attempt ban, so it waits for new settings.
/// Safe to call from any thread.
/// </summary>
internal sealed class HaClient : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly HaClientOptions _opt;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private ClientWebSocket? _ws;
    private int _nextId;

    private LinkState _link = LinkState.Unconfigured;
    private string? _url, _error, _haVersion;
    private DateTime? _connectedUtc;

    private ConcurrentDictionary<string, HaEntity> _entities = new(StringComparer.Ordinal);
    private List<(HaEntity? State, string EntityId)>? _early;   // events seen before this session's snapshot
    private int _subscriptionId = -1;

    public HaClient(ILogger log, HaClientOptions? options = null)
    {
        _log = log;
        _opt = options ?? new HaClientOptions();
    }

    public HaSnapshot GetSnapshot()
    {
        lock (_gate) return new HaSnapshot(_link, _url, _error, _haVersion, _entities, _connectedUtc);
    }

    public HaEntity? Get(string entityId) => _entities.TryGetValue(entityId, out var e) ? e : null;

    /// <summary>(Re)starts the connection. A null URL or token leaves the client idle and unconfigured.</summary>
    public async Task StartAsync(string? url, string? token, bool acceptAnyCertificate)
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _url = url;
            _error = null;
            _haVersion = null;
            _connectedUtc = null;
            _entities = new(StringComparer.Ordinal);
            _link = url is null || token is null ? LinkState.Unconfigured : LinkState.Connecting;
            if (_link == LinkState.Unconfigured) return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _supervisor = Task.Run(() => SuperviseAsync(url!, token!, acceptAnyCertificate, cts.Token));
        }
    }

    private async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? supervisor;
        lock (_gate)
        {
            cts = _cts;
            supervisor = _supervisor;
            _cts = null;
            _supervisor = null;
        }
        if (cts is null) return;
        cts.Cancel();
        try { _ws?.Abort(); } catch { /* already gone */ }
        if (supervisor is not null)
        {
            try { await supervisor.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        cts.Dispose();
    }

    private async Task SuperviseAsync(string url, string token, bool acceptAnyCertificate, CancellationToken ct)
    {
        var delay = _opt.ReconnectMin;
        while (!ct.IsCancellationRequested)
        {
            SetLink(LinkState.Connecting, keepError: true);
            var started = DateTime.UtcNow;
            try
            {
                var rejected = await RunSessionAsync(url, token, acceptAnyCertificate, ct).ConfigureAwait(false);
                if (rejected is not null)
                {
                    _log.LogWarning("Home Assistant rejected the access token: {Reason}", rejected);
                    lock (_gate) { _link = LinkState.AuthFailed; _error = rejected; }
                    return;   // only new settings start it again
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var message = Describe(ex, url);
                _log.LogInformation("Home Assistant connection ended: {Message}", message);
                lock (_gate) { _link = LinkState.Disconnected; _error = message; }
            }
            finally
            {
                _ws = null;
                FailPending();
            }

            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) delay = _opt.ReconnectMin;   // it had been working
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _opt.ReconnectMax.Ticks));
        }
    }

    /// <summary>Runs one connection until it fails. Returns a reason when the token was rejected.</summary>
    private async Task<string?> RunSessionAsync(string url, string token, bool acceptAnyCertificate, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (acceptAnyCertificate) ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(_opt.ConnectTimeout);
            await ws.ConnectAsync(HaUrl.WebSocket(url), connect.Token).ConfigureAwait(false);

            // auth_required -> auth -> auth_ok | auth_invalid
            var hello = await ReceiveAsync(ws, connect.Token).ConfigureAwait(false);
            if (Type(hello) != "auth_required") throw new HaProtocolException("This address did not answer like Home Assistant");
            await SendRawAsync(ws, w =>
            {
                w.WriteString("type", "auth");
                w.WriteString("access_token", token);
            }, connect.Token).ConfigureAwait(false);
            var auth = await ReceiveAsync(ws, connect.Token).ConfigureAwait(false);
            switch (Type(auth))
            {
                case "auth_ok":
                    lock (_gate) _haVersion = Str(auth, "ha_version");
                    break;
                case "auth_invalid":
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); } catch { }
                    return "The access token was rejected. Create a new long-lived token in your Home Assistant profile.";
                default:
                    throw new HaProtocolException("Unexpected reply while signing in");
            }
        }

        _ws = ws;
        lock (_gate) _early = [];
        var receive = Task.Run(() => ReceiveLoopAsync(ws, session.Token));
        try
        {
            // Subscribe first, then read every state, so no change can fall between the two.
            var subscribe = await CommandAsync(w =>
            {
                w.WriteString("type", "subscribe_events");
                w.WriteString("event_type", "state_changed");
            }, session.Token, onId: id => Volatile.Write(ref _subscriptionId, id)).ConfigureAwait(false);
            if (!Success(subscribe)) throw new HaProtocolException("Home Assistant refused the state subscription: " + ErrorText(subscribe));

            var states = await CommandAsync(w => w.WriteString("type", "get_states"), session.Token).ConfigureAwait(false);
            if (!Success(states)) throw new HaProtocolException("Home Assistant refused to list states: " + ErrorText(states));
            ApplySnapshot(states.GetProperty("result"));

            lock (_gate)
            {
                _link = LinkState.Connected;
                _error = null;
                _connectedUtc = DateTime.UtcNow;
            }
            _log.LogInformation("Connected to Home Assistant {Version} with {Count} entities", _haVersion, _entities.Count);

            // Ping until the socket dies. A missing pong means a half-open connection.
            while (true)
            {
                var finished = await Task.WhenAny(receive, Task.Delay(_opt.PingInterval, session.Token)).ConfigureAwait(false);
                if (finished == receive) { await receive.ConfigureAwait(false); throw new HaProtocolException("Home Assistant closed the connection"); }
                var pong = await CommandAsync(w => w.WriteString("type", "ping"), session.Token).ConfigureAwait(false);
                if (Type(pong) != "pong") throw new HaProtocolException("Home Assistant stopped answering");
            }
        }
        finally
        {
            session.Cancel();
            ws.Abort();
            try { await receive.ConfigureAwait(false); } catch { /* reported by the caller */ }
            lock (_gate) _early = null;
            Volatile.Write(ref _subscriptionId, -1);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (true)
        {
            var message = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (message.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in message.EnumerateArray()) Dispatch(m);   // coalesced messages, if ever sent
            }
            else
            {
                Dispatch(message);
            }
        }
    }

    private void Dispatch(JsonElement m)
    {
        switch (Type(m))
        {
            case "result" or "pong":
                if (m.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(m);
                break;
            case "event":
                if (m.TryGetProperty("id", out var subEl) && subEl.TryGetInt32(out var sub) && sub == Volatile.Read(ref _subscriptionId)
                    && m.TryGetProperty("event", out var ev) && Str(ev, "event_type") == "state_changed"
                    && ev.TryGetProperty("data", out var data) && Str(data, "entity_id") is { } entityId)
                {
                    var state = data.TryGetProperty("new_state", out var ns) && ns.ValueKind == JsonValueKind.Object ? ParseEntity(ns) : null;
                    ApplyChange(entityId, state);
                }
                break;
        }
    }

    private void ApplyChange(string entityId, HaEntity? state)
    {
        lock (_gate)
        {
            if (_early is { } early && _link != LinkState.Connected)
            {
                early.Add((state, entityId));
                return;
            }
        }
        if (state is null) _entities.TryRemove(entityId, out _);
        else _entities[entityId] = state;
    }

    private void ApplySnapshot(JsonElement result)
    {
        var fresh = new ConcurrentDictionary<string, HaEntity>(StringComparer.Ordinal);
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
                if (ParseEntity(item) is { } e) fresh[e.EntityId] = e;
        }
        lock (_gate)
        {
            // Changes that arrived while the snapshot was in flight win if they are newer.
            foreach (var (state, id) in _early ?? [])
            {
                if (state is null) fresh.TryRemove(id, out _);
                else if (!fresh.TryGetValue(id, out var old) || state.LastUpdated >= old.LastUpdated) fresh[id] = state;
            }
            _early = null;
            _entities = fresh;
        }
    }

    internal static HaEntity? ParseEntity(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        var id = Str(s, "entity_id");
        if (!Tiles.IsEntityId(id)) return null;
        var state = Str(s, "state") ?? "unknown";
        string? name = null, unit = null, deviceClass = null;
        if (s.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            name = Str(a, "friendly_name");
            unit = Str(a, "unit_of_measurement");
            deviceClass = Str(a, "device_class");
        }
        return new HaEntity(id!, Cap(state, 255)!, Cap(name, 120), Cap(unit, 24), Cap(deviceClass, 40),
            Time(s, "last_changed"), Time(s, "last_updated"));
    }

    /// <summary>Calls one service on one entity. The caller decides which pairs are allowed.</summary>
    public async Task<CommandResult> CallServiceAsync(string domain, string service, string entityId, CancellationToken ct)
    {
        if (GetSnapshot().Link != LinkState.Connected) return CommandResult.Blocked("Not connected to Home Assistant");
        try
        {
            var reply = await CommandAsync(w =>
            {
                w.WriteString("type", "call_service");
                w.WriteString("domain", domain);
                w.WriteString("service", service);
                w.WriteStartObject("target");
                w.WriteString("entity_id", entityId);
                w.WriteEndObject();
            }, ct).ConfigureAwait(false);
            return Success(reply)
                ? CommandResult.Success("Done")
                : CommandResult.Upstream("Home Assistant: " + ErrorText(reply));
        }
        catch (TimeoutException)
        {
            return CommandResult.Upstream("Home Assistant did not answer in time");
        }
        catch (Exception ex) when (ex is WebSocketException or HaProtocolException or InvalidOperationException)
        {
            return CommandResult.Upstream("The connection to Home Assistant dropped");
        }
    }

    /// <summary>Sends a command with the next id and waits for its result (or pong).</summary>
    private async Task<JsonElement> CommandAsync(Action<Utf8JsonWriter> body, CancellationToken ct, Action<int>? onId = null)
    {
        var ws = _ws ?? throw new HaProtocolException("Not connected");
        var id = Interlocked.Increment(ref _nextId);
        onId?.Invoke(id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendRawAsync(ws, w => { w.WriteNumber("id", id); body(w); }, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(_opt.CommandTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendRawAsync(ClientWebSocket ws, Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task<JsonElement> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer.GetMemory(16 * 1024), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) throw new HaProtocolException("Home Assistant closed the connection");
            buffer.Advance(r.Count);
            if (buffer.WrittenCount > _opt.MaxMessageBytes) throw new HaProtocolException("Home Assistant sent an oversized message");
            if (r.EndOfMessage) break;
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new HaProtocolException("Connection closed"));
    }

    private void SetLink(LinkState link, bool keepError)
    {
        lock (_gate)
        {
            _link = link;
            if (!keepError) _error = null;
        }
    }

    /// <summary>Plain-language reason for a failed connection (shown in the panel; never contains the token).</summary>
    internal static string Describe(Exception ex, string url)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HaProtocolException p: return p.Message;
                case AuthenticationException:
                    return "The server's certificate is not trusted. If this is your own Home Assistant with a self-signed certificate, tick \"Accept a self-signed certificate\".";
                case HttpRequestException { StatusCode: { } code }:
                    return (int)code == 404 ? "No Home Assistant API at this address (check the port and any path)" : $"Home Assistant answered HTTP {(int)code}";
                case WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket }:
                    return "This address did not accept a WebSocket connection (is it Home Assistant?)";
                case OperationCanceledException or TimeoutException:
                    return $"No answer from {url}";
            }
        }
        return ex is WebSocketException or HttpRequestException ? $"Cannot reach Home Assistant at {url}" : ex.Message;
    }

    private static string? Type(JsonElement m) => Str(m, "type");

    private static bool Success(JsonElement m) =>
        m.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

    private static string ErrorText(JsonElement m) =>
        m.TryGetProperty("error", out var e) && Str(e, "message") is { } msg ? Cap(msg, 200)! : "request failed";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset Time(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;

    private static string? Cap(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}

internal sealed class HaProtocolException(string message) : Exception(message);
