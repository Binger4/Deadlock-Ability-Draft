using System.Net;
using System.Net.Sockets;
using abilitydraft.Services.InGame;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

static class LocalBackendStartupChecks
{
    public static async Task Run(string temporary)
    {
        var root = Path.Combine(temporary, "local-backend-startup");
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var configuration = Config($"http://127.0.0.1:{port}/");
        using (var first = await LocalBackendStartup.AcquireAsync(root, configuration, true))
        {
            Check(first.ShouldStart, "First local backend launch owns its startup lease");
            using var second = await LocalBackendStartup.AcquireAsync(root, configuration, true);
            Check(!second.ShouldStart && second.ExitCode == 0, "Concurrent local launches exit cleanly before starting hosted services");
            using var production = await LocalBackendStartup.AcquireAsync(root, configuration, false);
            Check(production.ShouldStart, "Local startup guard does not alter production hosting");
        }
        using (var next = await LocalBackendStartup.AcquireAsync(root, configuration, true))
            Check(next.ShouldStart, "Stopping the owner releases the startup lease for the next launch");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        await using var existing = builder.Build();
        existing.Urls.Add("http://127.0.0.1:0");
        existing.MapGet("/api/ingame/v1/hosting", (HttpContext context) =>
            context.Request.Headers.Authorization == "Bearer " + new string('x', 48)
                ? Results.Json(new[] { new DraftServerStatus("custom", 27067, true, true), new DraftServerStatus("public", 27068, true, true) })
                : Results.Unauthorized());
        await existing.StartAsync();
        try
        {
            configuration = Config(existing.Urls.Single());
            using var reuse = await LocalBackendStartup.AcquireAsync(root, configuration, true);
            Check(!reuse.ShouldStart && reuse.ExitCode == 0 && reuse.Message!.Contains("Reusing"),
                "An authenticated existing backend is reused without launching duplicate drafting servers");
            configuration["InGame:ServerKey"] = new string('y', 48);
            using var unrelated = await LocalBackendStartup.AcquireAsync(root, configuration, true);
            Check(!unrelated.ShouldStart && unrelated.ExitCode == 1 && unrelated.Message!.Contains("already in use"),
                "An occupied port with different authentication is reported instead of being reused or stopped");
            configuration["InGame:ServerKey"] = new string('x', 48);
            using var retry = await LocalBackendStartup.AcquireAsync(root, configuration, true);
            Check(!retry.ShouldStart && retry.Message!.Contains("Reusing"), "A rejected startup releases its lease before retrying");
        }
        finally { await existing.StopAsync(); }
    }

    private static IConfigurationRoot Config(string backend) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["urls"] = backend,
        ["InGame:Enabled"] = "true", ["InGame:ServerKey"] = new string('x', 48),
        ["InGame:DraftServers:Enabled"] = "true", ["InGame:DraftServers:BackendUrl"] = backend
    }).Build();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
