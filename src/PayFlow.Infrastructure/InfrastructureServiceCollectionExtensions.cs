using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.SharedKernel.Ids;
using StackExchange.Redis;

namespace PayFlow.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string DatabaseConnectionName = "PayFlow";
    public const string RedisConnectionName = "Redis";
    public const string MigrationsAssembly = "PayFlow.Migrations";

    /// <summary>Configures EF Core for PostgreSQL with the conventions every module relies on.</summary>
    public static DbContextOptionsBuilder UsePayFlowPostgres(this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly(MigrationsAssembly)
                .MigrationsHistoryTable("ef_migrations_history", PayFlowDbContext.PlatformSchema))
            .UseSnakeCaseNamingConvention();
    }

    public static IServiceCollection AddPayFlowInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var database = RequireConnectionString(configuration, DatabaseConnectionName);
        var redis = RequireConnectionString(configuration, RedisConnectionName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IdGenerator>();

        services.AddDbContext<PayFlowDbContext>(options => options.UsePayFlowPostgres(database));

        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddSingleton<RateLimiting.RedisRateLimiter>();

        services.Configure<Workers.WorkerOptions>(configuration.GetSection(Workers.WorkerOptions.Section));
        services.Configure<Idempotency.IdempotencyOptions>(configuration.GetSection(Idempotency.IdempotencyOptions.Section));
        services.AddScoped<Idempotency.IdempotencyStore>();
        Workers.WorkerRegistration.AddRecurringJob<Workers.IdempotencySweepJob>(services, o => o.IdempotencySweepInterval);
        Workers.WorkerRegistration.AddRecurringJob<OutboxDispatchJob>(services, o => o.OutboxDispatchInterval);

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.Configure<Security.MasterKeyOptions>(configuration.GetSection(Security.MasterKeyOptions.Section));
        services.AddSingleton<Security.SecretProtector>();

        // Key ring persisted in PostgreSQL so all API instances share it and cookies survive restarts,
        // and each key encrypted at rest with the master key (a database dump cannot forge sessions).
        services.AddDataProtection()
            .SetApplicationName("PayFlow")
            .PersistKeysToDbContext<PayFlowDbContext>();
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<Security.SecretProtector>((o, protector) => o.XmlEncryptor = new Security.MasterKeyXmlEncryptor(protector));

        return services;
    }

    private static string RequireConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Connection string '{name}' is not configured. Set ConnectionStrings__{name} via environment, user-secrets or Docker secrets.");
}
