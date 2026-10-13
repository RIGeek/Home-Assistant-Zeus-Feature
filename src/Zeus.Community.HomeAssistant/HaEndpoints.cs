// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Zeus.Community.HomeAssistant.Ha;

namespace Zeus.Community.HomeAssistant;

internal sealed record CommandRequest(string? Tile, string? Action);

internal sealed record ConnectionRequest(string? Url, string? Token, bool? ClearToken, bool? AcceptAnyCertificate);

internal sealed record TilesRequest(List<TileInput>? Tiles);

/// <summary>
/// HTTP surface, mounted by Zeus under /api/plugins/&lt;id&gt;/. It can switch things in the
/// house, so state-changing routes refuse cross-site browser requests and anything that is
/// not JSON, and the access token is accepted but never returned.
/// </summary>
internal static class HaEndpoints
{
    private const int MaxBodyBytes = 4096;
    private const int MaxTilesBytes = 64 * 1024;

    public static void Map(IEndpointRouteBuilder endpoints, Func<HaService?> service)
    {
        endpoints.MapGet("status", (HttpContext ctx) =>
            With(ctx, service, s => Results.Json(s.BuildStatus(), Tiles.Json)));

        endpoints.MapGet("settings", (HttpContext ctx) =>
            With(ctx, service, s =>
            {
                var c = s.GetSettings();
                return Results.Json(new { url = c.Url, tokenSet = c.Token is not null, acceptAnyCertificate = c.AcceptAnyCertificate });
            }));

        endpoints.MapPost("settings", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<ConnectionRequest>(ctx);
            if (error is not null) return error;
            var result = await s.ApplyConnectionAsync(body!.Url, body.Token, body.ClearToken ?? false, body.AcceptAnyCertificate, ctx.RequestAborted);
            return Reply(ctx, result);
        });

        endpoints.MapGet("tiles", (HttpContext ctx) =>
            With(ctx, service, s => Results.Json(new { tiles = s.GetSettings().Tiles }, Tiles.Json)));

        endpoints.MapPost("tiles", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<TilesRequest>(ctx, MaxTilesBytes);
            if (error is not null) return error;
            var (result, tiles) = await s.SetTilesAsync(body!.Tiles, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ok = result.Ok, message = result.Message, tiles }, Tiles.Json, statusCode: result.Status);
        });

        endpoints.MapGet("entities", (HttpContext ctx, string? q, int? limit) =>
            With(ctx, service, s =>
            {
                var (connected, total, entities) = s.Search(q is { Length: > 100 } ? q[..100] : q, limit ?? 50);
                return Results.Json(new { connected, total, entities }, Tiles.Json);
            }));

        endpoints.MapPost("command", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<CommandRequest>(ctx);
            if (error is not null) return error;
            return Reply(ctx, await s.OperateAsync(body!.Tile, body.Action, ctx.RequestAborted));
        });
    }

    private static IResult With(HttpContext ctx, Func<HaService?> service, Func<HaService, IResult> action)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var s = service();
        return s is null ? Unavailable() : action(s);
    }

    private static IResult Unavailable() =>
        Results.Json(new { ok = false, message = "The Home Assistant feature is not running" }, statusCode: 503);

    private static IResult Reply(HttpContext ctx, CommandResult result)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { ok = result.Ok, message = result.Message }, statusCode: result.Status);
    }

    private static IResult Refuse(int status, string message) =>
        Results.Json(new { ok = false, message }, statusCode: status);

    private static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext ctx, int maxBytes = MaxBodyBytes)
    {
        // Same rule as the amplifier feature: the Zeus page may be served from another port of the same
        // host (same-site), which is accepted; a request started by another site is refused. JSON-only
        // bodies add a second layer, since a cross-origin JSON POST needs a CORS preflight that is never granted.
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && site != "same-site" && site != "none")
            return (default, Refuse(403, "Cross-site requests are refused"));

        if (!ctx.Request.HasJsonContentType())
            return (default, Refuse(415, "Content-Type must be application/json"));

        if (ctx.Request.ContentLength > maxBytes)
            return (default, Refuse(413, "Request body too large"));
        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = maxBytes;

        try
        {
            var value = await ctx.Request.ReadFromJsonAsync<T>(Tiles.Json, ctx.RequestAborted);
            return value is null ? (default, Refuse(400, "Empty request")) : (value, null);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            return (default, ex is BadHttpRequestException { StatusCode: 413 }
                ? Refuse(413, "Request body too large")
                : Refuse(400, "Malformed request"));
        }
    }
}
