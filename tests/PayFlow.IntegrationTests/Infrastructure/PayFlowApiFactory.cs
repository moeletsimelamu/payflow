using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace PayFlow.IntegrationTests.Infrastructure;

/// <summary>The real API host, connected to the test containers as the restricted app role.</summary>
public sealed class PayFlowApiFactory(
    PayFlowTestHost host,
    IReadOnlyDictionary<string, string?>? overrides,
    TimeProvider? clock = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    /// <summary>Every log event the host writes, rendered with properties and exceptions.</summary>
    public CapturingLogSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:PayFlow"] = host.AppConnectionString,
            ["ConnectionStrings:Redis"] = host.RedisConnectionString,
            ["Database:ApplyMigrationsOnStartup"] = "false",
            ["Seed:PlatformAdmin:Email"] = PayFlowTestHost.AdminEmail,
            ["Seed:PlatformAdmin:Password"] = PayFlowTestHost.AdminPassword,
            ["Security:ApiKeys:Pepper"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-pepper-32-bytes!!")),
            ["Security:MasterKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("integration-test-master-key-32by")),
            ["RateLimiting:AuthenticationPermitLimit"] = "10000",
            ["RateLimiting:MerchantApiPermitLimit"] = "10000",
            ["Serilog:MinimumLevel:Default"] = "Debug",
            // Background timers are off; tests run each job explicitly so outcomes are deterministic.
            ["Workers:Enabled"] = "false",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        // Serilog's ReadFrom.Services picks up every registered sink.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(Logs);
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }

            configureServices?.Invoke(services);
        });
    }
}

public sealed class CapturingLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<string> _lines = new();

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var line = new StringBuilder(logEvent.RenderMessage());
        foreach (var (name, value) in logEvent.Properties)
        {
            line.Append(' ').Append(name).Append('=').Append(value);
        }

        if (logEvent.Exception is not null)
        {
            line.Append(' ').Append(logEvent.Exception);
        }

        _lines.Enqueue(line.ToString());
    }

    public bool Contains(string text) => _lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    public int Count => _lines.Count;
}
