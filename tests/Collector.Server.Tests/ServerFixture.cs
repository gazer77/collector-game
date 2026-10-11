using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Contracts;
using Collector.Engine.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Collector.Server.Tests;

/// <summary>A real server with its own SQLite database and a clock the test controls.</summary>
public sealed class ServerFixture : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = CreateJson();

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "collector-tests", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Collector:DataDirectory", _dataDirectory);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    /// <summary>An HTTP client signed in as a fresh guest.</summary>
    public async Task<HttpClient> GuestAsync(string? deviceId = null)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.GuestLogin, new GuestLoginRequest(deviceId ?? NewDeviceId()), Json);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    public static string NewDeviceId() => Guid.NewGuid().ToString("N");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { } // SQLite may still hold the file briefly
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        EngineJson.ApplyTo(options);
        return options;
    }
}
