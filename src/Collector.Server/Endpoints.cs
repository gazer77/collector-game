using System.Security.Claims;
using Collector.Contracts;
using Collector.Server.Auth;
using Collector.Server.Data;
using Collector.Server.Game;
using Collector.Server.Packs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collector.Server;

public static class Endpoints
{
    public static void MapCollectorApi(this WebApplication app)
    {
        app.MapPost("/" + ApiRoutes.GuestLogin, GuestLogin);
        app.MapGet("/" + ApiRoutes.Pack("{packId}"), GetPack);

        var game = app.MapGroup("/api/game/{packId}").RequireAuthorization();
        game.MapGet("/", Sync);
        game.MapPost("/heartbeat", Sync);
        game.MapPost("/commands", Commands);

        // Unknown API routes are 404s, not the client app's index.html.
        app.MapFallback("/api/{**rest}", () => Results.NotFound());
    }

    private static async Task<IResult> GuestLogin(GuestLoginRequest request, CollectorDb db, TokenService tokens, TimeProvider time, CancellationToken ct)
    {
        var deviceId = request.DeviceId?.Trim() ?? "";
        if (deviceId.Length is < 16 or > 128)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["deviceId"] = ["Must be 16 to 128 characters."] });

        var player = await db.Players.SingleOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (player is null)
        {
            player = new Player { Id = Guid.NewGuid(), DeviceId = deviceId, CreatedAt = time.GetUtcNow() };
            db.Players.Add(player);
            await db.SaveChangesAsync(ct);
        }

        var (token, expires) = tokens.Issue(player.Id);
        return Results.Ok(new AuthResponse(player.Id, token, expires));
    }

    private static IResult GetPack(string packId, PackRegistry packs) =>
        packs.Find(packId) is { } pack ? Results.Ok(pack.Bundle) : Results.NotFound();

    private static async Task<IResult> Sync(string packId, ClaimsPrincipal user, PackRegistry packs, GameService games, CancellationToken ct)
    {
        if (packs.Find(packId) is not { } pack)
            return Results.NotFound();
        if (TokenService.PlayerId(user) is not { } playerId)
            return Results.Unauthorized();
        return Results.Ok(await games.SyncAsync(playerId, pack, ct));
    }

    private static async Task<IResult> Commands(string packId, CommandBatch batch, ClaimsPrincipal user, PackRegistry packs,
        GameService games, IOptions<CollectorOptions> options, CancellationToken ct)
    {
        if (packs.Find(packId) is not { } pack)
            return Results.NotFound();
        if (TokenService.PlayerId(user) is not { } playerId)
            return Results.Unauthorized();
        if (batch.Commands is null || batch.Commands.Count > options.Value.MaxCommandsPerBatch || batch.Commands.Any(c => c.Command is null))
            return Results.BadRequest($"Send between 0 and {options.Value.MaxCommandsPerBatch} commands.");
        return Results.Ok(await games.ApplyAsync(playerId, pack, batch, ct));
    }
}
