using MudBlazor.Services;
using WebAdmin.Components;

namespace WebAdmin;

public class Program
{
    public static async Task Main(string[] args)
    {
        var reverseProxyApp = await ReverseProxy.AppBuilder.BuildReverseProxy();

        var builder = WebApplication.CreateBuilder(args);

        // Add MudBlazor services
        _ = builder.Services.AddMudServices();

        // Add services to the container.
        _ = builder.Services.AddRazorComponents()
                            .AddInteractiveServerComponents();

        var webAdminApp = builder.Build();

        // Configure the HTTP request pipeline.
        if (!webAdminApp.Environment.IsDevelopment())
        {
            _ = webAdminApp.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            _ = webAdminApp.UseHsts();
        }

        _ = webAdminApp.UseHttpsRedirection();

        _ = webAdminApp.UseAntiforgery();

        _ = webAdminApp.MapStaticAssets();
        _ = webAdminApp.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        reverseProxyApp.Urls.Clear();
        reverseProxyApp.Urls.Add("http://*:5000");

        webAdminApp.Urls.Clear();
        webAdminApp.Urls.Add("http://*:5001");

        var webAdminTask = webAdminApp.RunAsync();
        var reverseProxyTask = reverseProxyApp.RunAsync();

        var exitedTask = await Task.WhenAny(webAdminTask, reverseProxyTask);

        if(exitedTask == webAdminTask)
        {
            await webAdminApp.StopAsync();
        }
        else
        {
            await reverseProxyApp.StopAsync();
        }

    }
}
