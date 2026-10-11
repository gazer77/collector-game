using System.Security.Cryptography;
using Collector.Contracts;
using Collector.Engine.State;
using Collector.Server.Data;
using Collector.Server.Packs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collector.Server.Game;

/// <summary>Loads a player's save, brings its clock up to date, applies commands and stores the result.</summary>
public sealed class GameService(CollectorDb db, PlayerLocks locks, TimeProvider time, IOptions<CollectorOptions> options)
{
    private CollectorOptions Options => options.Value;

    /// <summary>Returns the player's current game, starting a new one if they have none.</summary>
    public async Task<GameSync> SyncAsync(Guid playerId, LoadedPack pack, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(playerId, ct);
        var (save, state) = await LoadAsync(playerId, pack, ct);
        CatchUp(save, state, pack);
        await StoreAsync(save, state, ct);
        return ToSync(pack, state);
    }

    public async Task<CommandBatchResult> ApplyAsync(Guid playerId, LoadedPack pack, CommandBatch batch, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(playerId, ct);
        var (save, state) = await LoadAsync(playerId, pack, ct);
        CatchUp(save, state, pack);

        var clock = pack.Definitions.Manifest.Clock;
        var grace = GameClock.Tolerance(clock, state.Time, batch.ClientTime, save.AheadMs, Options.ClockToleranceMilliseconds);
        if (grace > 0)
        {
            pack.Engine.AdvanceTime(state, grace);
            save.AheadMs += grace;
        }

        var results = new List<CommandOutcome>(batch.Commands.Count);
        foreach (var envelope in batch.Commands)
        {
            var result = pack.Engine.Apply(state, envelope.Command);
            results.Add(new CommandOutcome(envelope.Seq, result.Ok, result.Error, result.Message, result.Events));
        }

        await StoreAsync(save, state, ct);
        return new CommandBatchResult(results, ToSync(pack, state));
    }

    private async Task<(Save Save, PlayerState State)> LoadAsync(Guid playerId, LoadedPack pack, CancellationToken ct)
    {
        var packId = pack.Definitions.Manifest.Id;
        var save = await db.Saves.SingleOrDefaultAsync(s => s.PlayerId == playerId && s.PackId == packId, ct);
        if (save is not null)
            return (save, StateHasher.Deserialize(save.StateJson));

        var state = pack.Engine.NewGame(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));
        save = new Save
        {
            PlayerId = playerId,
            PackId = packId,
            StateJson = StateHasher.Serialize(state),
            LastSeenAt = time.GetUtcNow(),
        };
        db.Saves.Add(save);
        return (save, state);
    }

    private void CatchUp(Save save, PlayerState state, LoadedPack pack)
    {
        var now = time.GetUtcNow();
        var gap = (long)(now - save.LastSeenAt).TotalMilliseconds;
        var (advance, ahead) = GameClock.CatchUp(pack.Definitions.Manifest.Clock, gap, save.AheadMs, Options.SessionTimeoutSeconds * 1000L);
        if (advance > 0)
            pack.Engine.AdvanceTime(state, advance);
        save.AheadMs = ahead;
        save.LastSeenAt = now;
    }

    private async Task StoreAsync(Save save, PlayerState state, CancellationToken ct)
    {
        save.StateJson = StateHasher.Serialize(state);
        save.Version = state.Version;
        await db.SaveChangesAsync(ct);
    }

    private static GameSync ToSync(LoadedPack pack, PlayerState state) =>
        new(pack.Definitions.Manifest.Id, pack.Definitions.Hash, state, StateHasher.Hash(state));
}
