using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger;
using PayFlow.Modules.Payments;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayFlow.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentsLedgerIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_merchant", "(kind = 'MerchantPayable') = (merchant_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_accounts_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "platform",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    operation = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    resource_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.scope, x.operation, x.key });
                    table.CheckConstraint("ck_idempotency_records_status", "status IN ('InProgress', 'Completed')");
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payment_method = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    fee_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fee_rate = table.Column<decimal>(type: "numeric(7,6)", precision: 7, scale: 6, nullable: false),
                    fee_fixed = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    fee_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    refunded_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    pending_refund_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    simulated_resolve_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_payments_failure_reason", "status NOT IN ('Declined', 'Failed', 'Expired') OR failure_code IS NOT NULL");
                    table.CheckConstraint("ck_payments_fee", "fee_amount IS NULL OR (fee_amount >= 0 AND fee_amount < amount)");
                    table.CheckConstraint("ck_payments_fee_rate", "fee_rate >= 0 AND fee_rate < 1");
                    table.CheckConstraint("ck_payments_fee_when_captured", "status NOT IN ('Succeeded', 'PartiallyRefunded', 'Refunded', 'Reversed') OR fee_amount IS NOT NULL");
                    table.CheckConstraint("ck_payments_refund_balance", "refunded_amount >= 0 AND pending_refund_amount >= 0 AND refunded_amount + pending_refund_amount <= amount");
                    table.CheckConstraint("ck_payments_status", "status IN ('Created', 'Processing', 'Pending', 'Succeeded', 'Declined', 'Failed', 'Expired', 'PartiallyRefunded', 'Refunded', 'Reversed')");
                    table.ForeignKey(
                        name: "fk_payments_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "postings",
                schema: "ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_postings", x => x.id);
                    table.CheckConstraint("ck_postings_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_postings_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_postings_journal_entries_journal_entry_id",
                        column: x => x.journal_entry_id,
                        principalSchema: "ledger",
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_status_history",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_status_history_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "payments",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_code",
                schema: "ledger",
                table: "accounts",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_currency",
                schema: "ledger",
                table: "accounts",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_accounts_merchant_id",
                schema: "ledger",
                table: "accounts",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "platform",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_resource_type_resource_id",
                schema: "platform",
                table: "idempotency_records",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_occurred_at",
                schema: "ledger",
                table: "journal_entries",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_source_type_source_id_entry_type",
                schema: "ledger",
                table: "journal_entries",
                columns: new[] { "source_type", "source_id", "entry_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_status_history_payment_id_occurred_at",
                schema: "payments",
                table: "payment_status_history",
                columns: new[] { "payment_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_currency",
                schema: "payments",
                table: "payments",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_payments_merchant_id_created_at",
                schema: "payments",
                table: "payments",
                columns: new[] { "merchant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_payments_merchant_id_idempotency_key",
                schema: "payments",
                table: "payments",
                columns: new[] { "merchant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_merchant_id_merchant_reference",
                schema: "payments",
                table: "payments",
                columns: new[] { "merchant_id", "merchant_reference" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_merchant_id_status",
                schema: "payments",
                table: "payments",
                columns: new[] { "merchant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_pending_due",
                schema: "payments",
                table: "payments",
                column: "simulated_resolve_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_payments_processing",
                schema: "payments",
                table: "payments",
                column: "processing_started_at",
                filter: "status = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "ix_payments_status_failure_code",
                schema: "payments",
                table: "payments",
                columns: new[] { "status", "failure_code" });

            migrationBuilder.CreateIndex(
                name: "ix_postings_account_id",
                schema: "ledger",
                table: "postings",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_postings_journal_entry_id",
                schema: "ledger",
                table: "postings",
                column: "journal_entry_id");

            // Ledger: balanced entries enforced at commit; nothing in the ledger can change afterwards.
            migrationBuilder.Sql(LedgerModule.BalanceTriggersSql);
            migrationBuilder.Sql(AppendOnlySql.Protect(LedgerModule.Schema, "accounts"));
            migrationBuilder.Sql(AppendOnlySql.Protect(LedgerModule.Schema, "journal_entries"));
            migrationBuilder.Sql(AppendOnlySql.Protect(LedgerModule.Schema, "postings"));

            // Payment status history is append-only.
            migrationBuilder.Sql(AppendOnlySql.Protect(PaymentsModule.Schema, "payment_status_history"));

            // Cross-module foreign keys, which the modules cannot express in code.
            migrationBuilder.Sql("""
                ALTER TABLE payments.payments
                    ADD CONSTRAINT fk_payments_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id),
                    ADD CONSTRAINT fk_payments_fee_plans_fee_plan_id FOREIGN KEY (fee_plan_id) REFERENCES merchants.fee_plans (id);
                ALTER TABLE ledger.accounts
                    ADD CONSTRAINT fk_accounts_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE ledger.accounts DROP CONSTRAINT IF EXISTS fk_accounts_merchants_merchant_id;
                ALTER TABLE payments.payments DROP CONSTRAINT IF EXISTS fk_payments_fee_plans_fee_plan_id;
                ALTER TABLE payments.payments DROP CONSTRAINT IF EXISTS fk_payments_merchants_merchant_id;
                """);
            migrationBuilder.Sql(AppendOnlySql.Unprotect(PaymentsModule.Schema, "payment_status_history"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(LedgerModule.Schema, "postings"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(LedgerModule.Schema, "journal_entries"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(LedgerModule.Schema, "accounts"));
            migrationBuilder.Sql(LedgerModule.DropBalanceTriggersSql);

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "payment_status_history",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "postings",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journal_entries",
                schema: "ledger");
        }
    }
}
