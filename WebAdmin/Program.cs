using Domain.Interfaces;
using MudBlazor.Services;
using Microsoft.AspNetCore.Mvc;
using WebAdmin.Components;

namespace WebAdmin;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Build the reverse proxy (initialises SQLite, registers all proxy services)
        var reverseProxyApp = await ReverseProxy.AppBuilder.BuildReverseProxy();

        // -----------------------------------------------------------------------
        // WebAdmin app
        // -----------------------------------------------------------------------
        var builder = WebApplication.CreateBuilder(args);

        // MudBlazor
        _ = builder.Services.AddMudServices();

        // Razor / Blazor
        _ = builder.Services.AddRazorComponents()
                            .AddInteractiveServerComponents();

        // Share singletons from the reverse proxy's DI container with WebAdmin
        var sharedCache  = reverseProxyApp.Services.GetRequiredService<IRatingCache>();
        var sharedBypass = reverseProxyApp.Services.GetRequiredService<IBypassService>();
        _ = builder.Services.AddSingleton(sharedCache);
        _ = builder.Services.AddSingleton(sharedBypass);

        var sharedConfiguration = reverseProxyApp.Services.GetRequiredService<IConfigurationService>();
        _ = builder.Services.AddSingleton(sharedConfiguration);

        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        _ = builder.Logging.SetMinimumLevel(LogLevel.Trace);

        var webAdminApp = builder.Build();

        // HTTP pipeline
        if (!webAdminApp.Environment.IsDevelopment())
            _ = webAdminApp.UseExceptionHandler("/Error");
        _ = webAdminApp.UseAntiforgery();
        _ = webAdminApp.MapStaticAssets();
        _ = webAdminApp.MapRazorComponents<App>()
                        .AddInteractiveServerRenderMode();

        // Bind ports
        reverseProxyApp.Urls.Clear();
        reverseProxyApp.Urls.Add("http://*:5000");

        webAdminApp.Urls.Clear();
        webAdminApp.Urls.Add("http://*:5001");

        // Run both apps concurrently; stop both if either exits
        var webAdminTask      = webAdminApp.RunAsync();
        var reverseProxyTask  = reverseProxyApp.RunAsync();

        var exitedTask = await Task.WhenAny(webAdminTask, reverseProxyTask);

        if (exitedTask == webAdminTask)
            await reverseProxyApp.StopAsync();
        else
            await webAdminApp.StopAsync();
    }
}
