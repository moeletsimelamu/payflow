using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Payments;
using PayFlow.Modules.Refunds;

#nullable disable

namespace PayFlow.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundsAndReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "refunds");

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    payment_method = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    requested_by_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount", "amount > 0");
                    table.CheckConstraint("ck_refunds_completed", "(status = 'Pending') = (completed_at IS NULL)");
                    table.CheckConstraint("ck_refunds_failure_reason", "status <> 'Failed' OR failure_reason IS NOT NULL");
                    table.CheckConstraint("ck_refunds_status", "status IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "fk_refunds_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reversals",
                schema: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    fee_returned = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    requested_by_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reversals", x => x.id);
                    table.CheckConstraint("ck_reversals_amount", "amount > 0");
                    table.CheckConstraint("ck_reversals_fee", "fee_returned >= 0 AND fee_returned < amount");
                    table.ForeignKey(
                        name: "fk_reversals_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_currency",
                schema: "refunds",
                table: "refunds",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_merchant_id_idempotency_key",
                schema: "refunds",
                table: "refunds",
                columns: new[] { "merchant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_id_created_at",
                schema: "refunds",
                table: "refunds",
                columns: new[] { "payment_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_pending",
                schema: "refunds",
                table: "refunds",
                column: "created_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_reversals_currency",
                schema: "refunds",
                table: "reversals",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_reversals_merchant_id_idempotency_key",
                schema: "refunds",
                table: "reversals",
                columns: new[] { "merchant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reversals_payment_id",
                schema: "refunds",
                table: "reversals",
                column: "payment_id",
                unique: true);

            // Financial records may change status but are never deleted (REF-6, R9).
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(PaymentsModule.Schema, "payments"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(RefundsModule.Schema, "refunds"));
            migrationBuilder.Sql(AppendOnlySql.PreventDeletion(RefundsModule.Schema, "reversals"));

            // Reversals are immutable once written.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_reversals_append_only
                    BEFORE UPDATE ON refunds.reversals
                    FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
                """);

            // Cross-module foreign keys.
            migrationBuilder.Sql("""
                ALTER TABLE refunds.refunds
                    ADD CONSTRAINT fk_refunds_payments_payment_id FOREIGN KEY (payment_id) REFERENCES payments.payments (id),
                    ADD CONSTRAINT fk_refunds_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id);
                ALTER TABLE refunds.reversals
                    ADD CONSTRAINT fk_reversals_payments_payment_id FOREIGN KEY (payment_id) REFERENCES payments.payments (id),
                    ADD CONSTRAINT fk_reversals_merchants_merchant_id FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE refunds.reversals DROP CONSTRAINT IF EXISTS fk_reversals_merchants_merchant_id, DROP CONSTRAINT IF EXISTS fk_reversals_payments_payment_id;
                ALTER TABLE refunds.refunds DROP CONSTRAINT IF EXISTS fk_refunds_merchants_merchant_id, DROP CONSTRAINT IF EXISTS fk_refunds_payments_payment_id;
                DROP TRIGGER IF EXISTS trg_reversals_append_only ON refunds.reversals;
                """);
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(RefundsModule.Schema, "reversals"));
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(RefundsModule.Schema, "refunds"));
            migrationBuilder.Sql(AppendOnlySql.AllowDeletion(PaymentsModule.Schema, "payments"));

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "refunds");

            migrationBuilder.DropTable(
                name: "reversals",
                schema: "refunds");
        }
    }
}
