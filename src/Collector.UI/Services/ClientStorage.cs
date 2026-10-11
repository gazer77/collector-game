using Microsoft.JSInterop;

namespace Collector.UI.Services;

/// <summary>Small persistent key/value store on the device (the guest's device id, for now).</summary>
public interface IClientStorage
{
    ValueTask<string?> GetAsync(string key);
    ValueTask SetAsync(string key, string value);
}

/// <summary>Browser localStorage. Also works inside a MAUI Blazor Hybrid web view.</summary>
public sealed class LocalStorageClientStorage(IJSRuntime js) : IClientStorage
{
    public async ValueTask<string?> GetAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch (JSException) { return null; } // storage blocked (private mode etc.)
    }

    public async ValueTask SetAsync(string key, string value)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", key, value); }
        catch (JSException) { }
    }
}
