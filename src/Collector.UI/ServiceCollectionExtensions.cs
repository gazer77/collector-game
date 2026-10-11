using Collector.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Collector.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the game client. The host must register an <see cref="HttpClient"/> pointing at the server.</summary>
    public static IServiceCollection AddCollectorClient(this IServiceCollection services)
    {
        services.AddScoped<GameApi>();
        services.AddScoped<GameSession>();
        services.AddScoped<IClientStorage, LocalStorageClientStorage>();
        return services;
    }
}
