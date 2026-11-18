using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Reconciliation;
using PayFlow.Modules.Settlement;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayFlow.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "settlement");

            migrationBuilder.EnsureSchema(
                name: "reconciliation");

            migrationBuilder.AddColumn<Guid>(
                name: "settlement_batch_id",
                schema: "refunds",
                table: "refunds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "settlement_batch_id",
                schema: "payments",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "batches",
                schema: "settlement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    cutoff_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_count = table.Column<int>(type: "integer", nullable: false),
                    refund_count = table.Column<int>(type: "integer", nullable: false),
                    gross_payments = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total_fees = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total_refunds = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    carried_forward_in = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    payout_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    carried_forward_out = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payout_reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    payout_attempts = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_batches", x => x.id);
                    table.CheckConstraint("ck_batches_counts", "payment_count >= 0 AND refund_count >= 0 AND payment_count + refund_count > 0");
                    table.CheckConstraint("ck_batches_net", "net_amount = gross_payments - total_fees - total_refunds + carried_forward_in");
                    table.CheckConstraint("ck_batches_payout", "payout_amount >= 0 AND carried_forward_out <= 0 AND carried_forward_in <= 0 AND payout_amount + carried_forward_out = net_amount");
                    table.CheckConstraint("ck_batches_status", "status IN ('Pending', 'Paid', 'Failed')");
                    table.ForeignKey(
                        name: "fk_batches_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "statements",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    imported_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statements", x => x.id);
                    table.CheckConstraint("ck_statements_period", "period_end >= period_start");
                    table.CheckConstraint("ck_statements_status", "status IN ('Imported', 'Reconciled')");
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "settlement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    fee_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.CheckConstraint("ck_items_type", "item_type IN ('Payment', 'Refund')");
                    table.ForeignKey(
                        name: "fk_items_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "settlement",
                        principalTable: "batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    missing_external = table.Column<int>(type: "integer", nullable: false),
                    missing_internal = table.Column<int>(type: "integer", nullable: false),
                    amount_mismatch = table.Column<int>(type: "integer", nullable: false),
                    currency_mismatch = table.Column<int>(type: "integer", nullable: false),
                    duplicate_external = table.Column<int>(type: "integer", nullable: false),
                    status_discrepancy = table.Column<int>(type: "integer", nullable: false),
                    exceptions_created = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_runs_statements_statement_id",
                        column: x => x.statement_id,
                        principalSchema: "reconciliation",
                        principalTable: "statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "statement_lines",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    transaction_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    external_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    raw = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_lines", x => x.id);
                    table.CheckConstraint("ck_statement_lines_amount", "amount > 0");
                    table.CheckConstraint("ck_statement_lines_type", "transaction_type IN ('PAYMENT', 'REFUND')");
                    table.ForeignKey(
                        name: "fk_statement_lines_statements_statement_id",
                        column: x => x.statement_id,
                        principalSchema: "reconciliation",
                        principalTable: "statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "results",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    statement_line_id = table.Column<long>(type: "bigint", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    internal_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    internal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    internal_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    external_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    internal_currency = table.Column<string>(type: "char(3)", nullable: true),
                    external_currency = table.Column<string>(type: "char(3)", nullable: true),
                    internal_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    external_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_results", x => x.id);
                    table.CheckConstraint("ck_results_outcome", "outcome IN ('Matched', 'MissingExternal', 'MissingInternal', 'AmountMismatch', 'CurrencyMismatch', 'DuplicateExternal', 'StatusDiscrepancy')");
                    table.ForeignKey(
                        name: "fk_results_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "reconciliation",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_results_statement_lines_statement_line_id",
                        column: x => x.statement_line_id,
                        principalSchema: "reconciliation",
                        principalTable: "statement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exceptions",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    latest_result_id = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    internal_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    internal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    internal_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    external_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    internal_currency = table.Column<string>(type: "char(3)", nullable: true),
                    external_currency = table.Column<string>(type: "char(3)", nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assigned_to = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resolution_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    adjustment_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    adjustment_direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    times_seen = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exceptions", x => x.id);
                    table.CheckConstraint("ck_exceptions_adjustment", "(resolution_type IS NOT DISTINCT FROM 'AdjustmentPosted') = (adjustment_amount IS NOT NULL AND adjustment_amount > 0 AND adjustment_direction IS NOT NULL)");
                    table.CheckConstraint("ck_exceptions_currency", "internal_currency IS NOT NULL OR external_currency IS NOT NULL");
                    table.CheckConstraint("ck_exceptions_outcome", "outcome IN ('Matched', 'MissingExternal', 'MissingInternal', 'AmountMismatch', 'CurrencyMismatch', 'DuplicateExternal', 'StatusDiscrepancy') AND outcome <> 'Matched'");
                    table.CheckConstraint("ck_exceptions_resolution", "(status IN ('Resolved', 'Dismissed')) = (resolution_type IS NOT NULL AND resolution_note IS NOT NULL AND resolved_by IS NOT NULL AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_exceptions_status", "status IN ('Open', 'InReview', 'Resolved', 'Dismissed')");
                    table.ForeignKey(
                        name: "fk_exceptions_results_latest_result_id",
                        column: x => x.latest_result_id,
                        principalSchema: "reconciliation",
                        principalTable: "results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exceptions_statements_statement_id",
                        column: x => x.statement_id,
                        principalSchema: "reconciliation",
                        principalTable: "statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exception_history",
                schema: "reconciliation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exception_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_exception_history_exceptions_exception_id",
                        column: x => x.exception_id,
                        principalSchema: "reconciliation",
                        principalTable: "exceptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_completed_at",
                schema: "refunds",
                table: "refunds",
                column: "completed_at");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_unsettled",
                schema: "refunds",
                table: "refunds",
                columns: new[] { "merchant_id", "currency", "completed_at" },
                filter: "settlement_batch_id IS NULL AND status = 'Succeeded'");

            migrationBuilder.CreateIndex(
                name: "ix_payments_completed_at",
                schema: "payments",
                table: "payments",
                column: "completed_at");

            migrationBuilder.CreateIndex(
                name: "ix_payments_unsettled",
                schema: "payments",
                table: "payments",
                columns: new[] { "merchant_id", "currency", "completed_at" },
                filter: "settlement_batch_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_batches_currency",
                schema: "settlement",
                table: "batches",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_batches_merchant_id_currency_created_at",
                schema: "settlement",
                table: "batches",
                columns: new[] { "merchant_id", "currency", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_batches_one_open",
                schema: "settlement",
                table: "batches",
                columns: new[] { "merchant_id", "currency" },
                unique: true,
                filter: "status <> 'Paid'");

            migrationBuilder.CreateIndex(
                name: "ix_exception_history_exception_id_occurred_at",
                schema: "reconciliation",
                table: "exception_history",
                columns: new[] { "exception_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_exceptions_fingerprint",
                schema: "reconciliation",
                table: "exceptions",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exceptions_latest_result_id",
                schema: "reconciliation",
                table: "exceptions",
                column: "latest_result_id");

            migrationBuilder.CreateIndex(
                name: "ix_exceptions_statement_id",
                schema: "reconciliation",
                table: "exceptions",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_exceptions_status_created_at",
                schema: "reconciliation",
                table: "exceptions",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_items_batch_id",
                schema: "settlement",
                table: "items",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_item_type_source_id",
                schema: "settlement",
                table: "items",
                columns: new[] { "item_type", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_payment_id",
                schema: "settlement",
                table: "items",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_results_run_id_outcome",
                schema: "reconciliation",
                table: "results",
                columns: new[] { "run_id", "outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_results_statement_line_id",
                schema: "reconciliation",
                table: "results",
                column: "statement_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_runs_statement_id_started_at",
                schema: "reconciliation",
                table: "runs",
                columns: new[] { "statement_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_lines_statement_id_line_number",
                schema: "reconciliation",
                table: "statement_lines",
                columns: new[] { "statement_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_statements_file_sha256",
                schema: "reconciliation",
                table: "statements",
                column: "file_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_statements_imported_at",
                schema: "reconciliation",
                table: "statements",
                column: "imported_at");

            // Settlement items and reconciliation evidence are history: immutable. Batches, statements,
            // runs and exceptions change state but are never deleted.
            migrationBuilder.Sql(AppendOnlySql.Protect(SettlementModule.Schema, "items"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(SettlementModule.Schema, "batches"));
            migrationBuilder.Sql(AppendOnlySql.Protect(ReconciliationModule.Schema, "statement_lines"));
            migrationBuilder.Sql(AppendOnlySql.Protect(ReconciliationModule.Schema, "results"));
            migrationBuilder.Sql(AppendOnlySql.Protect(ReconciliationModule.Schema, "exception_history"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(ReconciliationModule.Schema, "statements"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(ReconciliationModule.Schema, "runs"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(ReconciliationModule.Schema, "exceptions"));

            // Cross-module references the module models cannot express.
            migrationBuilder.Sql("""
                ALTER TABLE payments.payments
                    ADD CONSTRAINT fk_payments_batches_settlement_batch_id FOREIGN KEY (settlement_batch_id) REFERENCES settlement.batches (id);
                ALTER TABLE refunds.refunds
                    ADD CONSTRAINT fk_refunds_batches_settlement_batch_id FOREIGN KEY (settlement_batch_id) REFERENCES settlement.batches (id);
                ALTER TABLE settlement.batches
                    ADD CONSTRAINT fk_batches_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id);
                ALTER TABLE settlement.items
                    ADD CONSTRAINT fk_items_payments_payment_id FOREIGN KEY (payment_id) REFERENCES payments.payments (id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE settlement.items DROP CONSTRAINT IF EXISTS fk_items_payments_payment_id;
                ALTER TABLE settlement.batches DROP CONSTRAINT IF EXISTS fk_batches_merchants_merchant_id;
                ALTER TABLE refunds.refunds DROP CONSTRAINT IF EXISTS fk_refunds_batches_settlement_batch_id;
                ALTER TABLE payments.payments DROP CONSTRAINT IF EXISTS fk_payments_batches_settlement_batch_id;
                """);
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(ReconciliationModule.Schema, "exceptions"));
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(ReconciliationModule.Schema, "runs"));
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(ReconciliationModule.Schema, "statements"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(ReconciliationModule.Schema, "exception_history"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(ReconciliationModule.Schema, "results"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(ReconciliationModule.Schema, "statement_lines"));
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(SettlementModule.Schema, "batches"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(SettlementModule.Schema, "items"));
            migrationBuilder.DropTable(
                name: "exception_history",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "items",
                schema: "settlement");

            migrationBuilder.DropTable(
                name: "exceptions",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "batches",
                schema: "settlement");

            migrationBuilder.DropTable(
                name: "results",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "statement_lines",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "statements",
                schema: "reconciliation");

            migrationBuilder.DropIndex(
                name: "ix_refunds_completed_at",
                schema: "refunds",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "ix_refunds_unsettled",
                schema: "refunds",
                table: "refunds");

            migrationBuilder.DropIndex(
                name: "ix_payments_completed_at",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_payments_unsettled",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "settlement_batch_id",
                schema: "refunds",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "settlement_batch_id",
                schema: "payments",
                table: "payments");
        }
    }
}
