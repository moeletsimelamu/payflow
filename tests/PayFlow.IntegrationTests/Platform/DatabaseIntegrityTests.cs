using System.Net;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Platform;

/// <summary>Database-level guarantees that hold even if application code is wrong.</summary>
public sealed class DatabaseIntegrityTests(PayFlowTestHost host)
{
    [Theory]
    [InlineData("UPDATE audit.audit_events SET action = 'tampered' WHERE id = @id")]
    [InlineData("DELETE FROM audit.audit_events WHERE id = @id")]
    [InlineData("TRUNCATE audit.audit_events")]
    public async Task Audit_records_cannot_be_changed_even_by_the_schema_owner(string sql)
    {
        await using var db = await host.OpenOwnerConnectionAsync();
        var id = await InsertAuditEventAsync(db);

        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("id", id);
        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        error.MessageText.ShouldContain("append_only_violation");
    }

    [Theory]
    [InlineData("UPDATE audit.audit_events SET action = 'tampered' WHERE id = @id")]
    [InlineData("DELETE FROM audit.audit_events WHERE id = @id")]
    public async Task App_role_has_no_update_or_delete_privilege_on_audit(string sql)
    {
        await using var owner = await host.OpenOwnerConnectionAsync();
        var id = await InsertAuditEventAsync(owner);
        await using var app = new NpgsqlConnection(host.AppConnectionString);
        await app.OpenAsync();

        await using var command = new NpgsqlCommand(sql, app);
        command.Parameters.AddWithValue("id", id);
        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_role_can_append_and_read_audit_records()
    {
        await using var app = new NpgsqlConnection(host.AppConnectionString);
        await app.OpenAsync();

        var id = await InsertAuditEventAsync(app);

        await using var read = new NpgsqlCommand("SELECT action FROM audit.audit_events WHERE id = @id", app);
        read.Parameters.AddWithValue("id", id);
        ((string)(await read.ExecuteScalarAsync())!).ShouldBe("test.integrity.checked");
    }

    [Fact]
    public async Task App_role_cannot_run_ddl()
    {
        await using var app = new NpgsqlConnection(host.AppConnectionString);
        await app.OpenAsync();

        await using var command = new NpgsqlCommand("CREATE TABLE audit.sneaky (id int)", app);
        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Data_protection_keys_are_persisted_in_the_database()
    {
        using var client = host.CreateDashboardClient();
        (await client.LoginAsAdminAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = await host.OpenOwnerConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM identity.data_protection_keys", db);
        ((long)(await command.ExecuteScalarAsync())!).ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task<long> InsertAuditEventAsync(NpgsqlConnection db)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_events (occurred_at, actor_type, actor_display, action, target_type, correlation_id)
            VALUES (now(), 'System', 'system:test', 'test.integrity.checked', 'test', 'integrity-test')
            RETURNING id
            """, db);
        return (long)(await insert.ExecuteScalarAsync())!;
    }
}
