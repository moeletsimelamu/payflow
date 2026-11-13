using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Webhooks;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayFlow.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "webhooks");

            migrationBuilder.AlterColumn<string>(
                name: "last_error",
                schema: "platform",
                table: "outbox_messages",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "endpoints",
                schema: "webhooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    event_types = table.Column<string[]>(type: "text[]", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    secret_ciphertext = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    secret_hint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    previous_secret_ciphertext = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    previous_secret_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_endpoints", x => x.id);
                    table.CheckConstraint("ck_endpoints_events", "cardinality(event_types) > 0");
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "webhooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    api_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_processed",
                schema: "platform",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_processed", x => new { x.message_id, x.handler });
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "webhooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    attempt_budget_start = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => x.id);
                    table.CheckConstraint("ck_deliveries_attempts", "attempt_count >= 0 AND attempt_budget_start <= attempt_count");
                    table.CheckConstraint("ck_deliveries_status", "status IN ('Pending', 'Retrying', 'Succeeded', 'Exhausted', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_deliveries_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "webhooks",
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deliveries_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "webhooks",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                schema: "webhooks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    request_headers = table.Column<string>(type: "jsonb", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body_excerpt = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    error_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_delivery_attempts_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalSchema: "webhooks",
                        principalTable: "deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_due",
                schema: "webhooks",
                table: "deliveries",
                column: "next_attempt_at",
                filter: "status IN ('Pending', 'Retrying')");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_endpoint_id",
                schema: "webhooks",
                table: "deliveries",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_event_id_endpoint_id",
                schema: "webhooks",
                table: "deliveries",
                columns: new[] { "event_id", "endpoint_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_merchant_id_created_at",
                schema: "webhooks",
                table: "deliveries",
                columns: new[] { "merchant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_delivery_id_attempt_number",
                schema: "webhooks",
                table: "delivery_attempts",
                columns: new[] { "delivery_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_endpoints_merchant_id",
                schema: "webhooks",
                table: "endpoints",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_merchant_id_created_at",
                schema: "webhooks",
                table: "events",
                columns: new[] { "merchant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_events_source_message_id",
                schema: "webhooks",
                table: "events",
                column: "source_message_id",
                unique: true);

            // Events and attempts are history: immutable. Endpoints and deliveries change state but are never deleted.
            migrationBuilder.Sql(AppendOnlySql.Protect(WebhooksModule.Schema, "events"));
            migrationBuilder.Sql(AppendOnlySql.Protect(WebhooksModule.Schema, "delivery_attempts"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(WebhooksModule.Schema, "endpoints"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(WebhooksModule.Schema, "deliveries"));

            migrationBuilder.Sql("""
                ALTER TABLE webhooks.endpoints
                    ADD CONSTRAINT fk_endpoints_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id);
                ALTER TABLE webhooks.events
                    ADD CONSTRAINT fk_events_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id),
                    ADD CONSTRAINT fk_events_outbox_messages_source_message_id FOREIGN KEY (source_message_id) REFERENCES platform.outbox_messages (id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE webhooks.events DROP CONSTRAINT IF EXISTS fk_events_outbox_messages_source_message_id, DROP CONSTRAINT IF EXISTS fk_events_merchants_merchant_id;
                ALTER TABLE webhooks.endpoints DROP CONSTRAINT IF EXISTS fk_endpoints_merchants_merchant_id;
                """);
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(WebhooksModule.Schema, "deliveries"));
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(WebhooksModule.Schema, "endpoints"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(WebhooksModule.Schema, "delivery_attempts"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(WebhooksModule.Schema, "events"));

            migrationBuilder.DropTable(
                name: "delivery_attempts",
                schema: "webhooks");

            migrationBuilder.DropTable(
                name: "inbox_processed",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "webhooks");

            migrationBuilder.DropTable(
                name: "endpoints",
                schema: "webhooks");

            migrationBuilder.DropTable(
                name: "events",
                schema: "webhooks");

            migrationBuilder.AlterColumn<string>(
                name: "last_error",
                schema: "platform",
                table: "outbox_messages",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);
        }
    }
}
