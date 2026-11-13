using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Persistence;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Migrations;
using PayFlow.SharedKernel.Ids;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(PayFlowTestHost))]

namespace PayFlow.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL 17 and one Redis 7.4 container for the whole test run, prepared exactly like
/// production: <c>deploy/postgres/sql/roles.sql</c> creates the restricted app role, migrations run
/// as the schema owner, and the API then connects as <c>payflow_app</c>.
/// </summary>
public sealed class PayFlowTestHost : IAsyncLifetime
{
    public const string AdminEmail = "admin@payflow.test";
    public const string AdminPassword = "integration-test-passphrase";
    private const string AppRolePassword = "integration-app-role-pw";
    private const string Database = "payflow";

    // Several API hosts (default, fake-clock and rate-limit variants) run in parallel, each with its
    // own connection pool of up to 100, so the server default of 100 connections is not enough.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase(Database)
        .WithCommand("-c", "max_connections=400")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    private PayFlowApiFactory? _factory;

    /// <summary>Schema-owner (superuser) connection: for test setup and direct SQL assertions.</summary>
    public string OwnerConnectionString => _postgres.GetConnectionString();

    /// <summary>Restricted application-role connection, as used by the API.</summary>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(OwnerConnectionString)
    {
        Username = "payflow_app",
        Password = AppRolePassword,
    }.ConnectionString;

    public string RedisConnectionString => _redis.GetConnectionString();

    public PayFlowApiFactory Factory => _factory ?? throw new InvalidOperationException("Host not initialised.");

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        await ApplyRolesAsync();
        await MigrateAsOwnerAsync();
        _factory = CreateFactory();

        // Start the host once, before tests run in parallel and race to start it.
        _ = _factory.Server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public PayFlowApiFactory CreateFactory(
        IReadOnlyDictionary<string, string?>? overrides = null,
        TimeProvider? clock = null,
        Action<IServiceCollection>? configureServices = null) => new(this, overrides, clock, configureServices);

    public DashboardClient CreateDashboardClient(PayFlowApiFactory? factory = null) =>
        new((factory ?? Factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        }));

    public async Task<Guid> CreateUserAsync(string email, string password)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PayFlowUser>>();
        var ids = scope.ServiceProvider.GetRequiredService<IdGenerator>();
        var user = new PayFlowUser
        {
            Id = ids.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Test User",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var result = await users.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    public async Task<NpgsqlConnection> OpenOwnerConnectionAsync()
    {
        var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task ApplyRolesAsync()
    {
        var sql = (await File.ReadAllTextAsync(RepositoryFile("deploy", "postgres", "sql", "roles.sql")))
            .Replace(":'app_password'", $"'{AppRolePassword}'", StringComparison.Ordinal)
            .Replace(":\"db_name\"", $"\"{Database}\"", StringComparison.Ordinal);
        await using var connection = await OpenOwnerConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task MigrateAsOwnerAsync()
    {
        var options = new DbContextOptionsBuilder<PayFlowDbContext>();
        options.UsePayFlowPostgres(OwnerConnectionString);
        await using var db = new PayFlowDbContext(options.Options, DesignTimeDbContextFactory.Modules);
        await db.Database.MigrateAsync();
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PayFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Repository root (PayFlow.slnx) not found.")
            : Path.Combine([directory.FullName, .. parts]);
    }
}
