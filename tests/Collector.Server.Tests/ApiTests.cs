using System.Net;
using System.Net.Http.Json;
using Collector.Contracts;
using Collector.Engine.Commands;
using Collector.Engine.Simulation;
using Collector.Engine.State;

namespace Collector.Server.Tests;

public class ApiTests : IClassFixture<ServerFixture>
{
    private const string Pack = "farm";
    private readonly ServerFixture _server;

    public ApiTests(ServerFixture server) => _server = server;

    private static async Task<GameSync> SyncAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<GameSync>(ApiRoutes.Game(Pack), ServerFixture.Json))!;

    // Sequence numbers only ever grow, like a real client's, so the server never mistakes a batch for a resend.
    private static long _seq;

    private static Task<CommandBatchResult> SendAsync(HttpClient client, long clientTime, params GameCommand[] commands) =>
        SendAsync(client, new CommandBatch(clientTime, [.. commands.Select(c => new CommandEnvelope(Interlocked.Increment(ref _seq), c))]));

    private static async Task<CommandBatchResult> SendAsync(HttpClient client, CommandBatch batch)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Commands(Pack), batch, ServerFixture.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommandBatchResult>(ServerFixture.Json))!;
    }

    private static int Field(PlayerState state) => state.Placements.First(p => p.Producer == "field").Id;

    [Fact]
    public async Task Pack_bundle_is_public_and_loads_into_the_engine()
    {
        var bundle = await _server.CreateClient().GetFromJsonAsync<PackBundle>(ApiRoutes.Pack(Pack), ServerFixture.Json);

        var definitions = await Engine.Packs.PackLoader.LoadAsync(new Engine.Packs.MemoryPackSource(bundle!.Files));
        Assert.Equal(bundle.Hash, definitions.Hash);
    }

    [Fact]
    public async Task A_resent_batch_is_not_applied_twice()
    {
        var client = await _server.GuestAsync(ServerFixture.NewDeviceId());
        var state = (await SyncAsync(client)).State;
        var wheat = state.Count("wheat");
        var batch = new CommandBatch(state.Time, [new CommandEnvelope(Interlocked.Increment(ref _seq), new StartRecipe(Field(state), "grow_wheat"))]);

        var first = await SendAsync(client, batch);
        var again = await SendAsync(client, batch); // as if the first response was lost

        Assert.True(first.Results[0].Ok);
        Assert.True(again.Results[0].Ok);
        Assert.Equal(wheat - 1, again.Sync.State.Count("wheat"));
        Assert.Equal(batch.Commands[0].Seq, again.Sync.State.LastCommandSeq);
    }

    [Fact]
    public async Task Game_endpoints_require_a_token()
    {
        var response = await _server.CreateClient().GetAsync(ApiRoutes.Game(Pack));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Short_device_ids_are_rejected()
    {
        var response = await _server.CreateClient().PostAsJsonAsync(ApiRoutes.GuestLogin, new GuestLoginRequest("abc"), ServerFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Same_device_gets_the_same_game_back()
    {
        var device = ServerFixture.NewDeviceId();
        var first = await _server.GuestAsync(device);
        var state = (await SyncAsync(first)).State;
        await SendAsync(first, state.Time, new StartRecipe(Field(state), "grow_wheat"));

        var again = await _server.GuestAsync(device);
        var resumed = (await SyncAsync(again)).State;

        Assert.Equal(5, resumed.Count("wheat"));
    }

    [Fact]
    public async Task Unknown_pack_and_unknown_api_route_are_404()
    {
        var client = await _server.GuestAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ApiRoutes.Game("nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/whatever")).StatusCode);
    }

    [Fact]
    public async Task Commands_report_each_outcome_and_return_the_authoritative_state()
    {
        var client = await _server.GuestAsync();
        var state = (await SyncAsync(client)).State;

        var result = await SendAsync(client, state.Time,
            new StartRecipe(Field(state), "grow_wheat"),
            new Collect(Field(state)));

        Assert.True(result.Results[0].Ok);
        Assert.False(result.Results[1].Ok);
        Assert.Equal(ErrorCode.NotReady, result.Results[1].Error);
        Assert.Equal(5, result.Sync.State.Count("wheat"));
        Assert.Equal(StateHasher.Hash(result.Sync.State), result.Sync.StateHash);
    }

    [Fact]
    public async Task Clock_runs_while_playing_and_pauses_while_away()
    {
        // This test moves the shared fake clock, so it uses its own server.
        using var server = new ServerFixture();
        var client = await server.GuestAsync();
        var start = (await SyncAsync(client)).State.Time;

        // Heartbeats every 15 s keep the session alive: all of that time counts.
        for (var i = 0; i < 8; i++)
        {
            server.Time.Advance(TimeSpan.FromSeconds(15));
            (await client.PostAsync(ApiRoutes.Heartbeat(Pack), null)).EnsureSuccessStatusCode();
        }
        var afterPlaying = (await SyncAsync(client)).State.Time;
        Assert.Equal(120_000, afterPlaying - start);

        // An hour away doesn't count at all.
        server.Time.Advance(TimeSpan.FromHours(1));
        var afterAway = (await SyncAsync(client)).State.Time;
        Assert.Equal(afterPlaying, afterAway);
    }

    [Fact]
    public async Task Small_client_clock_drift_is_forgiven_but_repaid()
    {
        using var server = new ServerFixture();
        var client = await server.GuestAsync();
        var state = (await SyncAsync(client)).State;
        await SendAsync(client, state.Time, new StartRecipe(Field(state), "grow_wheat"));

        // 119 s of play pass on the server, but the client already shows the wheat as ready.
        for (var i = 0; i < 7; i++)
        {
            server.Time.Advance(TimeSpan.FromSeconds(17));
            (await client.PostAsync(ApiRoutes.Heartbeat(Pack), null)).EnsureSuccessStatusCode();
        }
        var result = await SendAsync(client, state.Time + 120_000, new Collect(Field(state)));
        Assert.True(result.Results[0].Ok, result.Results[0].Message);
        var timeAfterCollect = result.Sync.State.Time;

        // The extra second is paid back: the next 1 s of wall time adds no game time.
        server.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(timeAfterCollect, (await SyncAsync(client)).State.Time);

        // Asking for far more than the tolerance gets only the tolerance.
        var greedy = await SendAsync(client, timeAfterCollect + 60_000);
        Assert.Equal(timeAfterCollect + 2_000, greedy.Sync.State.Time);
    }

    [Fact]
    public async Task Oversized_batches_are_rejected()
    {
        var client = await _server.GuestAsync();
        var batch = new CommandBatch(0, [.. Enumerable.Range(0, 101).Select(i => new CommandEnvelope(i, new SellItem("wheat", 1)))]);

        var response = await client.PostAsJsonAsync(ApiRoutes.Commands(Pack), batch, ServerFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
