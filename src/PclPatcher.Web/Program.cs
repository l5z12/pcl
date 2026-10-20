using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Diagnostics.CodeAnalysis;
using PclPatcher.Web;
using Microsoft.JSInterop;

internal static class Program
{
    // Preserve the methods called from JavaScript in the trimmed publish build.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(BrowserApi))]
    private static async Task Main(string[] args)
    {
        BrowserApi.Reset();
        var host = WebAssemblyHostBuilder.CreateDefault(args).Build();
        await host.Services.GetRequiredService<IJSRuntime>().InvokeVoidAsync("pclRuntimeReady");
        await host.RunAsync();
    }
}
