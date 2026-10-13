// SPDX-License-Identifier: GPL-2.0-or-later
//
// Self-contained test runner (a .NET 10 file-based app, so no test project or
// test-framework packages are needed):
//
//     dotnet run tests/HomeAssistant.Tests.cs
//
// It talks to a mock Home Assistant that implements the parts of the WebSocket API
// the feature uses (auth, get_states, subscribe_events, call_service, ping).
// Nothing here touches a real Home Assistant.
#:sdk Microsoft.NET.Sdk.Web
#:property AssemblyName=Zeus.Community.HomeAssistant.Tests
#:property PublishAot=false
#:project ../src/Zeus.Community.HomeAssistant/Zeus.Community.HomeAssistant.csproj

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Community.HomeAssistant;
using Zeus.Community.HomeAssistant.Ha;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool condition, string message) { if (!condition) throw new Exception("FAILED: " + message); }
async Task<bool> Until(Func<bool> condition, int timeoutMs = 4000)
{
    var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(20); }
    return condition();
}
HaClientOptions Fast() => new()
{
    ConnectTimeout = TimeSpan.FromSeconds(2), CommandTimeout = TimeSpan.FromSeconds(1),
    PingInterval = TimeSpan.FromMilliseconds(300),
    ReconnectMin = TimeSpan.FromMilliseconds(100), ReconnectMax = TimeSpan.FromMilliseconds(300),
};
const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJ0ZXN0In0.c2lnbmF0dXJlLWZvci10ZXN0cw";

async Task<HaService> Running(MockHa ha, IRadioStateSource? radio = null, MemorySettingsStore? store = null)
{
    var s = new HaService(NullLogger.Instance, store ?? new MemorySettingsStore(), radio ?? new FakeRadio(), Fast());
    await s.StartAsync(CancellationToken.None);
    var r = await s.ApplyConnectionAsync(ha.Url, Token, false, null, CancellationToken.None);
    Check(r.Ok, "connection settings accepted: " + r.Message);
    Check(await Until(() => s.Client.GetSnapshot().Link == LinkState.Connected), "never connected: " + s.Client.GetSnapshot().Error);
    return s;
}

TileInput T(string entity, string? kind = null, bool? lockTx = null, TileMeter? meter = null, string? label = null) =>
    new(null, entity, kind, label, null, lockTx, null, meter, null);

// Development host instead of the tests: a mock Home Assistant with a station's worth of entities,
// the real backend, and the panel's static harness, for trying the UI in a browser.
//     ZHA_DEV_HOST=harness dotnet run tests/HomeAssistant.Tests.cs   (the folder is relative to tests/)
if (Environment.GetEnvironmentVariable("ZHA_DEV_HOST") is { Length: > 0 } devRoot)
{
    await DevHost.RunAsync(devRoot, Token);
    return 0;
}

// ------------------------------------------------------------------ addresses, tokens, tiles

Test("url: normalised, and anything odd refused", () =>
{
    Check(HaUrl.Normalize("homeassistant.local:8123") == "http://homeassistant.local:8123", "scheme added");
    Check(HaUrl.Normalize(" https://ha.example.com/ ") == "https://ha.example.com", "trailing slash dropped");
    Check(HaUrl.Normalize("http://10.0.0.5:8123/ha/") == "http://10.0.0.5:8123/ha", "path prefix kept");
    Check(HaUrl.Normalize("ftp://x") is null && HaUrl.Normalize("http://u:p@x") is null, "scheme and credentials");
    Check(HaUrl.Normalize("http://x/?a=1") is null && HaUrl.Normalize("http://x/#f") is null && HaUrl.Normalize("") is null, "query, fragment, empty");
    Check(HaUrl.WebSocket("https://ha.example.com").ToString() == "wss://ha.example.com/api/websocket", "wss");
    Check(HaUrl.WebSocket("http://10.0.0.5:8123/ha").ToString() == "ws://10.0.0.5:8123/ha/api/websocket", "ws with prefix");
    Check(HaUrl.LooksLikeToken(Token) && !HaUrl.LooksLikeToken("short") && !HaUrl.LooksLikeToken(Token + " x"), "token shape");
    return Task.CompletedTask;
});

Test("tiles: kinds follow the domain; locks, covers and climate cannot be operated", () =>
{
    Check(Tiles.KindsFor("switch").SequenceEqual([TileKind.Toggle, TileKind.Sensor]), "switch");
    Check(Tiles.KindsFor("script").SequenceEqual([TileKind.Button, TileKind.Sensor]), "script");
    foreach (var d in new[] { "lock", "cover", "climate", "alarm_control_panel", "sensor" })
        Check(Tiles.KindsFor(d).SequenceEqual([TileKind.Sensor]), d + " is display-only");
    var (tiles, err) = Tiles.Validate([T("lock.front_door", "toggle")]);
    Check(tiles is null && err!.Contains("cannot be a toggle"), "lock refused as toggle");
    (tiles, _) = Tiles.Validate([T("switch.coax_relay"), T("button.restart"), T("sensor.shack_temp")]);
    Check(tiles!.Select(t => t.Kind).SequenceEqual([TileKind.Toggle, TileKind.Button, TileKind.Sensor]), "default kinds");
    Check(tiles![0].LockTx && !tiles[2].LockTx, "lock while transmitting defaults on for controls, never for readouts");
    return Task.CompletedTask;
});

Test("tiles: services come from the table, never from the request", () =>
{
    var (tiles, _) = Tiles.Validate([T("light.bench"), T("input_button.reset"), T("scene.night"), T("script.tune")]);
    Check(Tiles.ServiceFor(tiles![0], true) == ("light", "turn_on") && Tiles.ServiceFor(tiles[0], false) == ("light", "turn_off"), "toggle");
    Check(Tiles.ServiceFor(tiles[0], null) is null, "a toggle is not pressed");
    Check(Tiles.ServiceFor(tiles[1], null) == ("input_button", "press"), "input_button");
    Check(Tiles.ServiceFor(tiles[2], null) == ("scene", "turn_on") && Tiles.ServiceFor(tiles[3], null) == ("script", "turn_on"), "scene and script");
    Check(Tiles.ServiceFor(tiles[1], true) is null, "a button is not switched");
    return Task.CompletedTask;
});

Test("tiles: validation of ids, labels, meters and limits", () =>
{
    Check(Tiles.Validate([T("not an id")]).Error is not null, "bad entity id");
    Check(Tiles.Validate([T("Sensor.Upper")]).Error is not null, "upper case refused");
    Check(Tiles.Validate([T("switch.a", "explode")]).Error is not null, "unknown kind");
    Check(Tiles.Validate(Enumerable.Range(0, 65).Select(i => T($"switch.s{i}")).ToList()).Error is not null, "too many");
    Check(Tiles.Validate([T("sensor.a", meter: new(10, 5, null, null, false))]).Error is not null, "min above max");
    Check(Tiles.Validate([T("sensor.a", meter: new(0, 100, 80, 60, false))]).Error is not null, "yellow above red");
    Check(Tiles.Validate([T("sensor.batt", meter: new(0, 100, 30, 15, true))]).Error is null, "low-is-bad allows yellow above red");
    Check(Tiles.Validate([T("sensor.a", meter: new(0, 100, double.NaN, null, false))]).Error is not null, "NaN threshold");
    var (tiles, _) = Tiles.Validate([
        new("keep1", "switch.a", null, "  Coax\u0007 relay  " + new string('x', 60), " Shack ", false, true, null, 2),
        new("keep1", "switch.b", null, "", null, null, null, new(0, 1, null, null, false), null)]);
    Check(tiles![0].Id == "keep1" && tiles[1].Id != "keep1", "ids kept and de-duplicated");
    Check(tiles[0].Label!.Length == Tiles.MaxLabel && !tiles[0].Label!.Contains('\u0007') && tiles[0].Group == "Shack", "label cleaned and capped");
    Check(tiles[0].Digits is null && tiles[1].Meter is null && tiles[1].Label is null, "digits and meters only on readouts; empty label is null");
    Check(tiles[0] is { LockTx: false, Confirm: true }, "options kept");
    var round = Tiles.Deserialize(Tiles.Serialize(tiles));
    Check(round.SequenceEqual(tiles), "stored form round-trips");
    Check(Tiles.Deserialize("{garbage").Count == 0, "unreadable store is empty, not an error");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ client against the mock

Test("client: signs in, reads every state, and follows changes", async () =>
{
    await using var ha = new MockHa();
    await using var c = new HaClient(NullLogger.Instance, Fast());
    await c.StartAsync(ha.Url, Token, false);
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Connected), "connected");
    var snap = c.GetSnapshot();
    Check(snap.HaVersion == "2026.10.1" && snap.Entities.Count == ha.EntityCount, "version and all entities");
    var temp = c.Get("sensor.shack_temp")!;
    Check(temp is { State: "23.4", Unit: "°C", FriendlyName: "Shack temperature", Numeric: 23.4 }, "attributes parsed");
    ha.SetState("sensor.shack_temp", "24.1");
    Check(await Until(() => c.Get("sensor.shack_temp")?.State == "24.1"), "state_changed applied");
    ha.Remove("sensor.shack_temp");
    Check(await Until(() => c.Get("sensor.shack_temp") is null), "removed entity dropped");
    Check(ha.AuthAttempts == 1, "one sign-in");
});

Test("client: a rejected token is not retried (avoids Home Assistant's login ban)", async () =>
{
    await using var ha = new MockHa();
    await using var c = new HaClient(NullLogger.Instance, Fast());
    await c.StartAsync(ha.Url, "eyJ.wrong-token-value-here.x", false);
    Check(await Until(() => c.GetSnapshot().Link == LinkState.AuthFailed), "auth failed state");
    Check(c.GetSnapshot().Error!.Contains("rejected"), "plain explanation");
    await Task.Delay(800);
    Check(ha.AuthAttempts == 1, $"tried once, not {ha.AuthAttempts} times");
    Check(!c.GetSnapshot().Error!.Contains("wrong-token"), "token never appears in errors");
});

Test("client: reconnects after a drop and picks up changes made meanwhile", async () =>
{
    await using var ha = new MockHa();
    await using var c = new HaClient(NullLogger.Instance, Fast());
    await c.StartAsync(ha.Url, Token, false);
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Connected), "connected");
    ha.Silently("switch.coax_relay", "on");     // changed with no event, as if it happened while we were away
    ha.DropAll();
    Check(await Until(() => c.GetSnapshot().Link != LinkState.Connected, 2000), "noticed the drop");
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Connected), "came back");
    Check(c.Get("switch.coax_relay")?.State == "on", "fresh snapshot after reconnect");
    Check(ha.AuthAttempts >= 2, "signed in again");
});

Test("client: a server that stops answering pings is treated as gone", async () =>
{
    await using var ha = new MockHa();
    await using var c = new HaClient(NullLogger.Instance, Fast());
    await c.StartAsync(ha.Url, Token, false);
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Connected), "connected");
    ha.IgnorePings = true;
    Check(await Until(() => c.GetSnapshot().Link != LinkState.Connected, 3000), "half-open connection detected");
    ha.IgnorePings = false;
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Connected), "reconnected once it answers again");
});

Test("client: unreachable address and wrong service are explained", async () =>
{
    await using var c = new HaClient(NullLogger.Instance, Fast());
    await c.StartAsync("http://127.0.0.1:1", Token, false);
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Disconnected && c.GetSnapshot().Error is not null), "error reported");
    Check(c.GetSnapshot().Error!.Contains("127.0.0.1:1"), "names the address: " + c.GetSnapshot().Error);

    await using var web = new MockHa(notHomeAssistant: true);
    await using var d = new HaClient(NullLogger.Instance, Fast());
    await d.StartAsync(web.Url, Token, false);
    Check(await Until(() => d.GetSnapshot().Error is { } e && (e.Contains("WebSocket") || e.Contains("No Home Assistant"))), "not HA: " + d.GetSnapshot().Error);
});

Test("client: an event racing the initial snapshot is not lost", async () =>
{
    await using var ha = new MockHa { EventDuringGetStates = ("sensor.shack_temp", "30.0") };
    await using var c = new HaClient(NullLogger.Instance, Fast());
    await c.StartAsync(ha.Url, Token, false);
    Check(await Until(() => c.GetSnapshot().Link == LinkState.Connected), "connected");
    Check(c.Get("sensor.shack_temp")?.State == "30.0", "newer event kept over the older snapshot value: " + c.Get("sensor.shack_temp")?.State);
});

// ------------------------------------------------------------------ service

Test("service: tiles operate the right service and show live state", async () =>
{
    await using var ha = new MockHa();
    await using var s = await Running(ha);
    var (r, tiles) = await s.SetTilesAsync([T("switch.coax_relay"), T("button.rotator_park"), T("sensor.shack_temp", meter: new(0, 50, 30, 40, false))], CancellationToken.None);
    Check(r.Ok && tiles!.Count == 3, "saved");

    var st = s.BuildStatus();
    Check(st.Connection.State == "connected" && st.Tiles[0].On == false && st.Tiles[2].Value == 23.4, "status reflects HA");
    Check(st.Tiles[1].Available, "a never-pressed button (state \"unknown\") is available");

    var res = await s.OperateAsync(tiles![0].Id, "on", CancellationToken.None);
    Check(res.Ok, "switch on: " + res.Message);
    Check(ha.Calls.Contains("switch.turn_on switch.coax_relay"), "turn_on called");
    Check(await Until(() => s.BuildStatus().Tiles[0].On == true), "state followed");

    res = await s.OperateAsync(tiles[1].Id, "press", CancellationToken.None);
    Check(res.Ok && ha.Calls.Contains("button.press button.rotator_park"), "button pressed");

    Check((await s.OperateAsync(tiles[1].Id, "on", CancellationToken.None)).Status == 400, "a button cannot be switched");
    Check((await s.OperateAsync(tiles[2].Id, "press", CancellationToken.None)).Status == 400, "a readout cannot be operated");
    Check((await s.OperateAsync("nope", "on", CancellationToken.None)).Status == 400, "unknown tile");
    Check((await s.OperateAsync(tiles[0].Id, "explode", CancellationToken.None)).Status == 400, "unknown action");
});

Test("service: transmit lockout", async () =>
{
    await using var ha = new MockHa();
    var radio = new FakeRadio();
    await using var s = await Running(ha, radio);
    var (_, tiles) = await s.SetTilesAsync([T("switch.coax_relay"), T("light.bench", lockTx: false)], CancellationToken.None);
    radio.Tx = true;
    var r = await s.OperateAsync(tiles![0].Id, "on", CancellationToken.None);
    Check(r is { Ok: false, Status: 409 } && r.Message.Contains("transmitting"), "locked during TX");
    Check(!ha.Calls.Any(c => c.EndsWith("switch.coax_relay")), "nothing sent to HA");
    Check(s.BuildStatus().Tiles[0].Locked && !s.BuildStatus().Tiles[1].Locked, "status shows which are locked");
    Check((await s.OperateAsync(tiles[1].Id, "on", CancellationToken.None)).Ok, "unlocked tile still works");
    radio.Tx = null;
    Check((await s.OperateAsync(tiles[0].Id, "on", CancellationToken.None)).Status == 409, "unreadable TX state locks too");
    radio.Tx = false;
    Check((await s.OperateAsync(tiles[0].Id, "on", CancellationToken.None)).Ok, "allowed after TX");
});

Test("service: without radio state the lockout cannot apply, and status says so", async () =>
{
    await using var ha = new MockHa();
    await using var s = await Running(ha, new FakeRadio { Available = false });
    var (_, tiles) = await s.SetTilesAsync([T("switch.coax_relay")], CancellationToken.None);
    Check(!s.BuildStatus().Radio.Available, "reported unavailable");
    Check((await s.OperateAsync(tiles![0].Id, "on", CancellationToken.None)).Ok, "operates");
});

Test("service: unavailable, missing and refused entities", async () =>
{
    await using var ha = new MockHa();
    await using var s = await Running(ha);
    var (_, tiles) = await s.SetTilesAsync([T("switch.dead_plug"), T("switch.gone"), T("switch.faulty")], CancellationToken.None);
    var st = s.BuildStatus();
    Check(!st.Tiles[0].Available && !st.Tiles[0].Missing && st.Tiles[0].On is null, "unavailable shown as such");
    Check(st.Tiles[1].Missing, "missing entity flagged");
    Check((await s.OperateAsync(tiles![0].Id, "on", CancellationToken.None)).Status == 409, "unavailable refused");
    Check((await s.OperateAsync(tiles[1].Id, "on", CancellationToken.None)).Status == 409, "missing refused");
    var r = await s.OperateAsync(tiles[2].Id, "on", CancellationToken.None);
    Check(r is { Ok: false, Status: 502 } && r.Message.Contains("Relay jammed"), "HA error passed on: " + r.Message);
    ha.SlowCalls = true;
    r = await s.OperateAsync(tiles[2].Id, "off", CancellationToken.None);
    Check(r is { Status: 502 } && r.Message.Contains("in time"), "timeout reported: " + r.Message);
});

Test("service: settings persist, the token is write-only, and new settings retry a rejected token", async () =>
{
    await using var ha = new MockHa();
    var store = new MemorySettingsStore();
    await using var s = new HaService(NullLogger.Instance, store, new FakeRadio(), Fast());
    await s.StartAsync(CancellationToken.None);
    Check(s.BuildStatus().Connection.State == "unconfigured", "starts unconfigured");
    Check((await s.ApplyConnectionAsync("not a url!!", null, false, null, CancellationToken.None)).Status == 400, "bad url");
    Check((await s.ApplyConnectionAsync(ha.Url, "x y", false, null, CancellationToken.None)).Status == 400, "bad token");
    Check((await s.ApplyConnectionAsync(ha.Url, null, false, null, CancellationToken.None)).Ok, "url only");
    Check(s.BuildStatus().Connection.State == "no_token", "asks for a token");
    await s.ApplyConnectionAsync(null, "eyJ.wrong-token-value-here.x", false, null, CancellationToken.None);
    Check(await Until(() => s.BuildStatus().Connection.State == "auth_failed"), "auth failed");
    ha.AcceptToken = "eyJ.wrong-token-value-here.x";   // as if the token were fixed on the HA side
    await s.ApplyConnectionAsync(null, null, false, null, CancellationToken.None);
    Check(await Until(() => s.BuildStatus().Connection.State == "connected"), "saving again retries");
    Check((await store.LoadAsync(CancellationToken.None)) is { Token: "eyJ.wrong-token-value-here.x" }, "token stored");
    await s.ApplyConnectionAsync(null, null, true, null, CancellationToken.None);
    Check(s.GetSettings().Token is null && s.BuildStatus().Connection.State == "no_token", "token cleared");
    await s.ApplyConnectionAsync("", null, false, null, CancellationToken.None);
    Check(s.BuildStatus().Connection.State == "unconfigured", "empty url disconnects");
});

Test("service: entity search for the picker", async () =>
{
    await using var ha = new MockHa();
    await using var s = await Running(ha);
    var (connected, total, list) = s.Search("", 3);
    Check(connected && total == ha.EntityCount && list.Count == 3, "paged");
    Check(list[0].Kinds[0] != "sensor", "operable entities listed first");
    (_, total, list) = s.Search("shack temp", 50);
    Check(total == 1 && list[0].EntityId == "sensor.shack_temp" && list[0].Unit == "°C", "matches words in id or name");
    (_, total, _) = s.Search("COAX", 50);
    Check(total == 1, "case-insensitive");
});

// ------------------------------------------------------------------ HTTP layer

Test("http: routes, validation, token secrecy and cross-site protection", async () =>
{
    await using var ha = new MockHa();
    await using var service = new HaService(NullLogger.Instance, new MemorySettingsStore(), new FakeRadio(), Fast());
    await service.StartAsync(CancellationToken.None);

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    HaEndpoints.Map(app.MapGroup("/api/plugins/test"), () => service);
    await app.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/api/plugins/test/") };

    async Task<(int Code, JsonElement Body, string Text)> Call(HttpMethod method, string path, string? json = null,
        string mediaType = "application/json", string? fetchSite = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, mediaType);
        if (fetchSite is not null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Check(res.Headers.CacheControl?.NoStore == true || (int)res.StatusCode >= 400, $"{path} is not cacheable");
        return ((int)res.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    var (code, st, _) = await Call(HttpMethod.Get, "status");
    Check(code == 200 && st.GetProperty("connection").GetProperty("state").GetString() == "unconfigured", "starts unconfigured");

    (code, _, _) = await Call(HttpMethod.Post, "settings", JsonSerializer.Serialize(new { url = ha.Url, token = Token }));
    Check(code == 200, "connection saved");
    Check(await Until(() => service.Client.GetSnapshot().Link == LinkState.Connected), "connected");

    string text;
    (code, st, text) = await Call(HttpMethod.Get, "settings");
    Check(st.GetProperty("tokenSet").GetBoolean() && !text.Contains(Token), "settings say a token is set but never return it");
    (_, _, text) = await Call(HttpMethod.Get, "status");
    Check(!text.Contains(Token), "status never contains the token");

    (code, st, _) = await Call(HttpMethod.Get, "entities?q=relay");
    Check(code == 200 && st.GetProperty("entities")[0].GetProperty("entityId").GetString() == "switch.coax_relay", "entity search");

    (code, st, _) = await Call(HttpMethod.Post, "tiles", "{\"tiles\":[{\"entityId\":\"switch.coax_relay\"},{\"entityId\":\"sensor.shack_temp\",\"meter\":{\"min\":0,\"max\":50,\"warn\":30,\"bad\":40}}]}");
    Check(code == 200 && st.GetProperty("tiles").GetArrayLength() == 2, "tiles saved");
    var tileId = st.GetProperty("tiles")[0].GetProperty("id").GetString();
    Check(st.GetProperty("tiles")[0].GetProperty("kind").GetString() == "toggle", "kind serialised in lower case");
    (code, st, _) = await Call(HttpMethod.Post, "tiles", "{\"tiles\":[{\"entityId\":\"lock.front_door\",\"kind\":\"toggle\"}]}");
    Check(code == 400 && st.GetProperty("message").GetString()!.Contains("cannot"), "lock refused");
    (code, st, _) = await Call(HttpMethod.Get, "tiles");
    Check(st.GetProperty("tiles").GetArrayLength() == 2, "refused list left the saved one alone");

    (code, st, _) = await Call(HttpMethod.Get, "status");
    var t0 = st.GetProperty("tiles")[0];
    Check(t0.GetProperty("name").GetString() == "Coax relay" && !t0.GetProperty("on").GetBoolean(), "tile status");

    (code, _, _) = await Call(HttpMethod.Post, "command", JsonSerializer.Serialize(new { tile = tileId, action = "on" }));
    Check(code == 200 && ha.Calls.Contains("switch.turn_on switch.coax_relay"), "command reached HA");
    (code, _, _) = await Call(HttpMethod.Post, "command", "{\"domain\":\"lock\",\"service\":\"unlock\",\"entity_id\":\"lock.front_door\"}");
    Check(code == 400 && !ha.Calls.Any(c => c.Contains("lock.")), "raw service calls are not possible");
    (code, _, _) = await Call(HttpMethod.Post, "command", "{not json");
    Check(code == 400, "malformed JSON -> 400");
    (code, _, _) = await Call(HttpMethod.Post, "command", "{}", "text/plain");
    Check(code == 415, "non-JSON -> 415");
    var before = ha.Calls.Count;
    (code, _, _) = await Call(HttpMethod.Post, "command", JsonSerializer.Serialize(new { tile = tileId, action = "off" }), fetchSite: "cross-site");
    Check(code == 403 && ha.Calls.Count == before, "cross-site -> 403 and nothing sent");
    (code, _, _) = await Call(HttpMethod.Post, "command", JsonSerializer.Serialize(new { tile = tileId, action = "off" }), fetchSite: "same-site");
    Check(code == 200, "same-site (Zeus UI on another port) accepted");
    (code, _, _) = await Call(HttpMethod.Post, "settings", "{\"url\":\"" + new string('x', 5000) + "\"}");
    Check(code == 413, "oversized body -> 413");
    (code, _, _) = await Call(HttpMethod.Post, "tiles", "{\"tiles\":[]}", fetchSite: "cross-site");
    Check(code == 403, "cross-site tiles -> 403");

    await app.StopAsync();
});

Test("http: routes answer 503 before the feature is running", async () =>
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    HaEndpoints.Map(app.MapGroup("/p"), () => null);
    await app.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/p/") };
    Check((int)(await http.GetAsync("status")).StatusCode == 503, "status 503");
    await app.StopAsync();
});

// ------------------------------------------------------------------ run

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

// ------------------------------------------------------------------ test doubles

sealed class FakeRadio : IRadioStateSource
{
    public bool Available { get; set; } = true;
    public bool? Tx { get; set; } = false;
    public bool? Transmitting => Tx;
}

/// <summary>Implements the Home Assistant WebSocket API closely enough to drive the client.</summary>
sealed class MockHa : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, JsonObject> _states = new();
    private readonly ConcurrentDictionary<WebSocket, (SemaphoreSlim Lock, int? SubId)> _sockets = new();
    private readonly ConcurrentQueue<string> _calls = new();
    private int _auth;

    public string Url { get; }
    public string AcceptToken { get; set; } = "eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJ0ZXN0In0.c2lnbmF0dXJlLWZvci10ZXN0cw";
    public int AuthAttempts => Volatile.Read(ref _auth);
    public int EntityCount => _states.Count;
    public List<string> Calls => [.. _calls];
    public volatile bool IgnorePings;
    public volatile bool SlowCalls;
    public (string Entity, string State)? EventDuringGetStates { get; init; }

    public MockHa(bool notHomeAssistant = false)
    {
        Add("sensor.shack_temp", "23.4", "Shack temperature", "°C", "temperature");
        Add("sensor.rotator_heading", "270", "Rotator heading", "°", null);
        Add("binary_sensor.shack_door", "off", "Shack door", null, "door");
        Add("switch.coax_relay", "off", "Coax relay", null, null);
        Add("switch.dead_plug", "unavailable", "Dead plug", null, null);
        Add("switch.faulty", "off", "Faulty relay", null, null);
        Add("light.bench", "on", "Bench light", null, null);
        Add("button.rotator_park", "unknown", "Park rotator", null, null);
        Add("lock.front_door", "locked", "Front door", null, null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();
        if (notHomeAssistant)
            _app.MapGet("/api/websocket", () => Results.Text("<html>a web page</html>", "text/html"));
        else
            _app.Map("/api/websocket", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await Session(ws);
            });
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First();
    }

    public void Add(string id, string state, string name, string? unit, string? deviceClass)
    {
        var attrs = new JsonObject { ["friendly_name"] = name };
        if (unit is not null) attrs["unit_of_measurement"] = unit;
        if (deviceClass is not null) attrs["device_class"] = deviceClass;
        var now = DateTime.UtcNow.AddMinutes(-5).ToString("O");
        _states[id] = new JsonObject { ["entity_id"] = id, ["state"] = state, ["attributes"] = attrs, ["last_changed"] = now, ["last_updated"] = now };
    }

    private JsonObject Touch(string id, string state)
    {
        var old = _states[id];
        var next = (JsonObject)old.DeepClone();
        next["state"] = state;
        var now = DateTime.UtcNow.ToString("O");
        next["last_changed"] = now; next["last_updated"] = now;
        _states[id] = next;
        return next;
    }

    public void SetState(string id, string state) => Broadcast(id, Touch(id, state));
    public void Silently(string id, string state) => Touch(id, state);
    public void Remove(string id) { _states.TryRemove(id, out _); Broadcast(id, null); }
    public void DropAll() { foreach (var ws in _sockets.Keys) ws.Abort(); }

    private void Broadcast(string id, JsonObject? state)
    {
        foreach (var (ws, (lk, sub)) in _sockets)
        {
            if (sub is not int s) continue;
            var ev = new JsonObject
            {
                ["id"] = s, ["type"] = "event",
                ["event"] = new JsonObject
                {
                    ["event_type"] = "state_changed",
                    ["data"] = new JsonObject { ["entity_id"] = id, ["new_state"] = state?.DeepClone() },
                },
            };
            _ = Send(ws, lk, ev);
        }
    }

    private static async Task Send(WebSocket ws, SemaphoreSlim lk, JsonNode msg)
    {
        await lk.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, default); }
        catch { }
        finally { lk.Release(); }
    }

    private async Task Session(WebSocket ws)
    {
        var lk = new SemaphoreSlim(1, 1);
        await Send(ws, lk, new JsonObject { ["type"] = "auth_required", ["ha_version"] = "2026.10.1" });
        var auth = await Receive(ws);
        Interlocked.Increment(ref _auth);
        if (auth?["access_token"]?.GetValue<string>() != AcceptToken)
        {
            await Send(ws, lk, new JsonObject { ["type"] = "auth_invalid", ["message"] = "Invalid access token or password" });
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default); } catch { }
            return;
        }
        await Send(ws, lk, new JsonObject { ["type"] = "auth_ok", ["ha_version"] = "2026.10.1" });
        _sockets[ws] = (lk, null);
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var m = await Receive(ws);
                if (m is null) break;
                var id = m["id"]!.GetValue<int>();
                switch (m["type"]?.GetValue<string>())
                {
                    case "ping":
                        if (!IgnorePings) await Send(ws, lk, new JsonObject { ["id"] = id, ["type"] = "pong" });
                        break;
                    case "subscribe_events":
                        _sockets[ws] = (lk, id);
                        await Send(ws, lk, Result(id, true, null));
                        break;
                    case "get_states":
                        var all = new JsonArray(_states.Values.Select(v => (JsonNode)v.DeepClone()).ToArray());
                        if (EventDuringGetStates is var (e, st))
                        {
                            await Task.Delay(30);     // the change happens after the snapshot was taken...
                            SetState(e, st);          // ...and its event goes out before the snapshot reply
                            await Task.Delay(30);
                        }
                        await Send(ws, lk, Result(id, true, all));
                        break;
                    case "call_service":
                        var domain = m["domain"]!.GetValue<string>();
                        var service = m["service"]!.GetValue<string>();
                        var target = m["target"]!["entity_id"]!.GetValue<string>();
                        _calls.Enqueue($"{domain}.{service} {target}");
                        if (SlowCalls) break;   // never answers
                        if (target == "switch.faulty")
                        {
                            await Send(ws, lk, new JsonObject
                            {
                                ["id"] = id, ["type"] = "result", ["success"] = false,
                                ["error"] = new JsonObject { ["code"] = "home_assistant_error", ["message"] = "Relay jammed" },
                            });
                            break;
                        }
                        await Send(ws, lk, Result(id, true, new JsonObject()));
                        if (service is "turn_on" or "turn_off" && _states.ContainsKey(target))
                            SetState(target, service == "turn_on" ? "on" : "off");
                        break;
                }
            }
        }
        catch { }
        finally { _sockets.TryRemove(ws, out _); }
    }

    private static JsonObject Result(int id, bool ok, JsonNode? result) =>
        new() { ["id"] = id, ["type"] = "result", ["success"] = ok, ["result"] = result };

    private static async Task<JsonNode?> Receive(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, default); } catch { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return JsonNode.Parse(ms.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        DropAll();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Serves the panel harness against the real backend and a mock Home Assistant. Not part of the tests.</summary>
static class DevHost
{
    public static async Task RunAsync(string root, string token)
    {
        await using var ha = new MockHa();
        ha.Add("sensor.station_psu_voltage", "13.82", "Station 13.8 V supply", "V", "voltage");
        ha.Add("sensor.ups_battery", "64", "UPS battery", "%", "battery");
        ha.Add("sensor.outdoor_wind_speed", "18.6", "Wind at the tower", "mph", "wind_speed");
        ha.Add("sensor.tower_ice", "none", "Tower icing", null, null);
        ha.Add("switch.station_power", "on", "Station power", null, null);
        ha.Add("switch.amp_psu", "off", "Amp supply", null, null);
        ha.Add("switch.antenna_ground", "off", "Antenna ground", null, null);
        ha.Add("script.tuner_reset", "off", "Reset tuner", null, null);
        var radio = new FakeRadio();
        var store = new MemorySettingsStore();
        await using var service = new HaService(NullLogger.Instance, store, radio);
        await service.StartAsync(CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ZHA_DEV_URL") ?? "http://127.0.0.1:5199");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(root)) });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(root)),
            ServeUnknownFileTypes = true,
        });
        HaEndpoints.Map(app.MapGroup("/api/plugins/com.n1mjf.homeassistant"), () => service);
        app.MapGet("/dev/tx", (bool on) => { radio.Tx = on; return Results.Ok(); });
        app.MapGet("/dev/state", (string id, string state) => { ha.SetState(id, state); return Results.Ok(); });
        app.MapGet("/dev/connect", async (bool good) =>
        {
            await service.ApplyConnectionAsync(ha.Url, good ? token : null, false, null, CancellationToken.None);
            return Results.Ok();
        });
        await app.StartAsync();
        Console.WriteLine($"Dev host on {app.Urls.First()} (mock Home Assistant at {ha.Url})");
        await app.WaitForShutdownAsync();
    }
}
