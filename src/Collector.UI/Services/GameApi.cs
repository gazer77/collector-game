using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Contracts;
using Collector.Engine.Serialization;

namespace Collector.UI.Services;

/// <summary>Typed calls to the Collector server.</summary>
public sealed class GameApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    public bool HasToken => http.DefaultRequestHeaders.Authorization is not null;

    public async Task<AuthResponse> LoginAsync(string deviceId, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(ApiRoutes.GuestLogin, new GuestLoginRequest(deviceId), Json, ct);
        var auth = await ReadAsync<AuthResponse>(response, ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return auth;
    }

    public async Task<PackBundle> GetPackAsync(string packId, CancellationToken ct = default) =>
        await ReadAsync<PackBundle>(await http.GetAsync(ApiRoutes.Pack(packId), ct), ct);

    public async Task<GameSync> SyncAsync(string packId, CancellationToken ct = default) =>
        await ReadAsync<GameSync>(await http.GetAsync(ApiRoutes.Game(packId), ct), ct);

    public async Task<GameSync> HeartbeatAsync(string packId, CancellationToken ct = default) =>
        await ReadAsync<GameSync>(await http.PostAsync(ApiRoutes.Heartbeat(packId), null, ct), ct);

    public async Task<CommandBatchResult> SendAsync(string packId, CommandBatch batch, CancellationToken ct = default) =>
        await ReadAsync<CommandBatchResult>(await http.PostAsJsonAsync(ApiRoutes.Commands(packId), batch, Json, ct), ct);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("The session expired.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new JsonException($"Empty {typeof(T).Name} response.");
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        EngineJson.ApplyTo(options);
        return options;
    }
}
