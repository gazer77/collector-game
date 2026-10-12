using System.Diagnostics;
using Collector.Contracts;
using Collector.Engine.Commands;
using Collector.Engine.Definitions;
using Collector.Engine.Packs;
using Collector.Engine.Simulation;
using Collector.Engine.State;

namespace Collector.UI.Services;

public enum SessionStatus { Loading, Ready, Failed }

public sealed record Toast(string Message, bool IsError = false);

/// <summary>
/// The client side of a game. Commands run through the engine locally first, so the UI responds instantly,
/// and are then sent to the server in order. <see cref="State"/> is always rebuilt as
/// "last state the server confirmed + time since then + commands the server hasn't confirmed yet",
/// so a server correction never leaves the UI out of step.
/// </summary>
public sealed class GameSession(GameApi api, IClientStorage storage) : IAsyncDisposable
{
    private const string DeviceIdKey = "collector.deviceId";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly List<CommandEnvelope> _pending = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private PlayerState? _confirmed;
    private TimeSpan _confirmedAt;
    private TimeSpan _lastContact;
    private long _nextSeq = 1;
    private bool _inFlight;
    private bool _offline;
    private string _packId = "";

    public SessionStatus Status { get; private set; } = SessionStatus.Loading;
    public string? FailureMessage { get; private set; }
    public GameDefinitions Definitions { get; private set; } = null!;
    public GameEngine Engine { get; private set; } = null!;

    /// <summary>What the player sees: the server's state plus local predictions.</summary>
    public PlayerState State { get; private set; } = null!;

    public bool IsOffline => _offline;

    /// <summary>Raised whenever <see cref="State"/> changes, including once per second as the clock ticks.</summary>
    public event Action? Changed;
    public event Action<Toast>? Toasted;

    public async Task StartAsync(string packId)
    {
        _packId = packId;
        try
        {
            await LoginAsync();
            var bundle = await api.GetPackAsync(packId);
            Definitions = await PackLoader.LoadAsync(new MemoryPackSource(bundle.Files));
            Engine = new GameEngine(Definitions);
            Accept(await api.SyncAsync(packId));
            Status = SessionStatus.Ready;
            _ = RunTimerAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or PackValidationException or UnauthorizedAccessException)
        {
            Status = SessionStatus.Failed;
            FailureMessage = ex is HttpRequestException
                ? "Can't reach the farm server. Check your connection and reload."
                : ex.Message;
        }
        Changed?.Invoke();
    }

    /// <summary>Runs a command now and queues it for the server. Returns the local result so the UI can react.</summary>
    public CommandResult Do(GameCommand command)
    {
        var result = Engine.Apply(State, command);
        if (!result.Ok)
        {
            Toasted?.Invoke(new Toast(result.Message, IsError: true));
            return result;
        }

        _pending.Add(new CommandEnvelope(_nextSeq++, command));
        AnnounceEvents(result.Events);
        Changed?.Invoke();
        _ = FlushAsync();
        return result;
    }

    /// <summary>
    /// Plants a field's own last crop again, or the most recently planted crop if the field has none yet,
    /// on the field <see cref="NextWithRoom"/> picks. Returns the field it planted on.
    /// </summary>
    public int? Replant(int? selectedId)
    {
        var fields = State.Placements.Where(IsField).ToList();
        // The newest crop still in the ground, else any crop a field remembers.
        var latest = fields.SelectMany(f => f.Jobs).MaxBy(j => j.StartsAt)?.Recipe
            ?? fields.Select(f => f.LastRecipe).FirstOrDefault(r => r is not null);
        if (latest is null)
        {
            Toasted?.Invoke(new Toast("Plant a crop first, then press R to plant it again."));
            return null;
        }

        if (NextWithRoom(fields, selectedId) is not { } target)
        {
            Toasted?.Invoke(new Toast("Every field is already growing something.", IsError: true));
            return null;
        }
        return Do(new StartRecipe(target.Id, target.LastRecipe ?? latest)).Ok ? target.Id : null;
    }

    /// <summary>Starts <paramref name="recipeId"/> on the producer <see cref="NextWithRoom"/> picks among those that make it.</summary>
    public int? StartNext(string recipeId, int? selectedId)
    {
        var recipe = Definitions.Recipes[recipeId];
        var producer = Definitions.Producers[recipe.Producer];
        if (NextWithRoom(State.Placements.Where(p => p.Producer == producer.Id), selectedId) is not { } target)
        {
            Toasted?.Invoke(new Toast(State.Placements.Any(p => p.Producer == producer.Id)
                ? $"Every {producer.Name} is full." : $"Build a {producer.Name} first.", IsError: true));
            return null;
        }
        return Do(new StartRecipe(target.Id, recipe.Id)).Ok ? target.Id : null;
    }

    /// <summary>
    /// The selected placement if it's one of <paramref name="candidates"/> and has room, otherwise the next one with
    /// room after it in reading order (row by row, left to right, wrapping round), or the first when none is selected.
    /// </summary>
    private Placement? NextWithRoom(IEnumerable<Placement> candidates, int? selectedId)
    {
        var ordered = candidates.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        var start = Math.Max(0, ordered.FindIndex(p => p.Id == selectedId));
        return ordered.Skip(start).Concat(ordered.Take(start)).FirstOrDefault(p => p.Jobs.Count < Engine.TierOf(p).Slots);
    }

    /// <summary>How many of <paramref name="itemId"/> the open orders ask for, in total.</summary>
    public int OrderDemand(string itemId) =>
        State.Orders.Sum(slot => slot.Order is { } order ? order.Items.GetValueOrDefault(itemId) : 0);

    /// <summary>The market's buy currency, or null if this game can't buy goods.</summary>
    public CurrencyDefinition? BuyCurrency =>
        Definitions.Market is { } market ? Definitions.Currencies[market.BuyCurrency ?? market.SellCurrency] : null;

    /// <summary>What it costs to buy whatever is short of <paramref name="needs"/>; null if any of it can't be bought yet.</summary>
    public long? CostOfMissing(IReadOnlyDictionary<string, int> needs)
    {
        long total = 0;
        foreach (var (id, qty) in needs)
        {
            var shortBy = qty - State.Count(id);
            if (shortBy <= 0)
                continue;
            var item = Definitions.Items[id];
            if (State.Level < item.UnlockLevel || Engine.BuyPrice(item) is not { } price)
                return null;
            total += price * shortBy;
        }
        return total;
    }

    /// <summary>Buys whatever is short of <paramref name="needs"/>. Returns true if nothing is short afterwards.</summary>
    public bool BuyMissing(IReadOnlyDictionary<string, int> needs)
    {
        if (CostOfMissing(needs) is not { } cost || BuyCurrency is not { } currency)
            return false;
        if (State.Balance(currency.Id) < cost)
        {
            Toasted?.Invoke(new Toast($"That costs {cost:N0} {currency.Name}; you have {State.Balance(currency.Id):N0}.", IsError: true));
            return false;
        }
        foreach (var (id, qty) in needs.ToList())
        {
            var shortBy = qty - State.Count(id);
            if (shortBy > 0 && !Do(new BuyItem(id, shortBy)).Ok)
                return false;
        }
        return true;
    }

    private bool IsField(Placement? placement) =>
        placement is not null && Definitions.Producers[placement.Producer].Category == "field";

    private async Task LoginAsync()
    {
        var deviceId = await storage.GetAsync(DeviceIdKey);
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];
            await storage.SetAsync(DeviceIdKey, deviceId);
        }
        await api.LoginAsync(deviceId);
    }

    private async Task FlushAsync()
    {
        if (_inFlight || _pending.Count == 0)
            return;
        _inFlight = true;
        var batch = _pending.ToList();
        try
        {
            var result = await CallServerAsync(() => api.SendAsync(_packId, new CommandBatch(State.Time, batch)));
            if (result is null)
                return;
            var sent = batch.Select(c => c.Seq).ToHashSet();
            _pending.RemoveAll(c => sent.Contains(c.Seq));
            foreach (var rejected in result.Results.Where(r => !r.Ok))
                Toasted?.Invoke(new Toast($"The farm didn't accept that: {rejected.Message}", IsError: true));
            Accept(result.Sync);
        }
        finally
        {
            _inFlight = false;
        }
        if (_pending.Count > 0)
            await FlushAsync();
    }

    private async Task HeartbeatAsync()
    {
        if (_inFlight || _pending.Count > 0)
            return;
        _inFlight = true;
        try
        {
            if (await CallServerAsync(() => api.HeartbeatAsync(_packId)) is { } sync && _pending.Count == 0)
                Accept(sync);
        }
        finally
        {
            _inFlight = false;
        }
    }

    /// <summary>Calls the server, signing in again if the token expired, and tracks whether we're offline.</summary>
    private async Task<T?> CallServerAsync<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            T result;
            try
            {
                result = await call();
            }
            catch (UnauthorizedAccessException)
            {
                await LoginAsync();
                result = await call();
            }
            _lastContact = _clock.Elapsed;
            if (_offline)
            {
                _offline = false;
                Toasted?.Invoke(new Toast("Back online."));
            }
            return result;
        }
        catch (HttpRequestException ex)
        {
            if (!_offline)
            {
                _offline = true;
                Toasted?.Invoke(new Toast(ex.StatusCode is { } status
                    ? $"The farm server hit an error ({(int)status}). Your actions will be retried."
                    : "Lost connection to the farm. Your actions will be sent when it's back.", IsError: true));
            }
            return null;
        }
    }

    private void Accept(GameSync sync)
    {
        _confirmed = sync.State;
        // Number new commands after everything the server has applied, so a resend is recognised as one.
        _nextSeq = Math.Max(_nextSeq, sync.State.LastCommandSeq + 1);
        _confirmedAt = _clock.Elapsed;
        _lastContact = _clock.Elapsed;
        Rebuild();
    }

    private void Rebuild()
    {
        var state = StateHasher.Clone(_confirmed!);
        var elapsed = (long)(_clock.Elapsed - _confirmedAt).TotalMilliseconds;
        if (Definitions.Manifest.Clock.Mode != ClockMode.Turn && elapsed > 0)
            Engine.AdvanceTime(state, elapsed);
        foreach (var envelope in _pending)
            Engine.Apply(state, envelope.Command);
        State = state;
        Changed?.Invoke();
    }

    private async Task RunTimerAsync()
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var previousLevel = State.Level;
                Rebuild();
                if (_pending.Count > 0)
                    await FlushAsync();
                else if (_clock.Elapsed - _lastContact >= HeartbeatInterval)
                    await HeartbeatAsync();
                if (State.Level > previousLevel)
                    Toasted?.Invoke(new Toast($"Level {State.Level}!"));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void AnnounceEvents(IReadOnlyList<GameEvent> events)
    {
        foreach (var e in events)
        {
            if (e is LevelReached level)
            {
                var rewards = level.Rewards.Select(r => $"{r.Value} {Definitions.Currencies[r.Key].Name}")
                    .Concat(level.ItemRewards.Select(r => $"{r.Value} {Definitions.Items[r.Key].Name}"));
                Toasted?.Invoke(new Toast($"Level {level.Level}! You got {string.Join(", ", rewards)}."));
            }
            else if (e is OrderCompleted)
            {
                Toasted?.Invoke(new Toast("Order delivered."));
            }
            else if (e is BonusYield bonus)
            {
                var items = bonus.Items.Select(i => $"+{i.Value} {Definitions.Items[i.Key].Name}");
                Toasted?.Invoke(new Toast($"Bonus! {string.Join(", ", items)}"));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
