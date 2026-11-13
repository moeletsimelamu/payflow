using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Merchants;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PayFlow.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "merchants");

            migrationBuilder.CreateTable(
                name: "fee_plans",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    percentage_rate = table.Column<decimal>(type: "numeric(7,6)", precision: 7, scale: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_plans", x => x.id);
                    table.CheckConstraint("ck_fee_plans_rate", "percentage_rate >= 0 AND percentage_rate < 1");
                });

            migrationBuilder.CreateTable(
                name: "fee_plan_currency_terms",
                schema: "merchants",
                columns: table => new
                {
                    fee_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    fixed_fee = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    min_payment_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_plan_currency_terms", x => new { x.fee_plan_id, x.currency });
                    table.CheckConstraint("ck_fee_plan_terms_fixed_fee", "fixed_fee >= 0");
                    table.CheckConstraint("ck_fee_plan_terms_minimum", "min_payment_amount > fixed_fee");
                    table.ForeignKey(
                        name: "fk_fee_plan_currency_terms_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fee_plan_currency_terms_fee_plan_fee_plan_id",
                        column: x => x.fee_plan_id,
                        principalSchema: "merchants",
                        principalTable: "fee_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trading_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    country_code = table.Column<string>(type: "char(2)", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    fee_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchants", x => x.id);
                    table.CheckConstraint("ck_merchants_status", "status IN ('PendingActivation', 'Active', 'Suspended', 'Closed')");
                    table.ForeignKey(
                        name: "fk_merchants_fee_plans_fee_plan_id",
                        column: x => x.fee_plan_id,
                        principalSchema: "merchants",
                        principalTable: "fee_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_key_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.CheckConstraint("ck_api_keys_hash", "length(secret_hash) = 32");
                    table.CheckConstraint("ck_api_keys_prefix", "length(prefix) = 8");
                    table.ForeignKey(
                        name: "fk_api_keys_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalSchema: "merchants",
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "merchant_currencies",
                schema: "merchants",
                columns: table => new
                {
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    max_payment_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    operator_refund_limit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchant_currencies", x => new { x.merchant_id, x.currency });
                    table.CheckConstraint("ck_merchant_currencies_max", "max_payment_amount > 0");
                    table.CheckConstraint("ck_merchant_currencies_refund_limit", "operator_refund_limit IS NULL OR operator_refund_limit >= 0");
                    table.ForeignKey(
                        name: "fk_merchant_currencies_currencies_currency",
                        column: x => x.currency,
                        principalSchema: "platform",
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_merchant_currencies_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalSchema: "merchants",
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "merchants",
                table: "fee_plans",
                columns: new[] { "id", "created_at", "name", "percentage_rate", "version" },
                values: new object[] { new Guid("0192f000-0000-7000-8000-000000000001"), new DateTimeOffset(new DateTime(2026, 11, 12, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Standard", 0.029m, 1 });

            migrationBuilder.InsertData(
                schema: "merchants",
                table: "fee_plan_currency_terms",
                columns: new[] { "currency", "fee_plan_id", "fixed_fee", "min_payment_amount" },
                values: new object[,]
                {
                    { "EUR", new Guid("0192f000-0000-7000-8000-000000000001"), 0.25m, 1.00m },
                    { "GBP", new Guid("0192f000-0000-7000-8000-000000000001"), 0.20m, 1.00m },
                    { "JPY", new Guid("0192f000-0000-7000-8000-000000000001"), 30m, 100m },
                    { "KWD", new Guid("0192f000-0000-7000-8000-000000000001"), 0.100m, 1.000m },
                    { "USD", new Guid("0192f000-0000-7000-8000-000000000001"), 0.30m, 1.00m },
                    { "ZAR", new Guid("0192f000-0000-7000-8000-000000000001"), 2.00m, 5.00m }
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_merchant_id",
                schema: "merchants",
                table: "api_keys",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_prefix",
                schema: "merchants",
                table: "api_keys",
                column: "prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fee_plan_currency_terms_currency",
                schema: "merchants",
                table: "fee_plan_currency_terms",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_fee_plans_name_version",
                schema: "merchants",
                table: "fee_plans",
                columns: new[] { "name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_merchant_currencies_currency",
                schema: "merchants",
                table: "merchant_currencies",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_merchants_created_at",
                schema: "merchants",
                table: "merchants",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_merchants_fee_plan_id",
                schema: "merchants",
                table: "merchants",
                column: "fee_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_merchants_status",
                schema: "merchants",
                table: "merchants",
                column: "status");

            // Memberships live in the Identity module, which cannot reference Merchants in code; the
            // database still enforces the relationship. Deferred so both rows can be written in one commit.
            migrationBuilder.Sql("""
                ALTER TABLE identity.merchant_memberships
                    ADD CONSTRAINT fk_merchant_memberships_merchants_merchant_id
                    FOREIGN KEY (merchant_id) REFERENCES merchants.merchants (id)
                    DEFERRABLE INITIALLY DEFERRED;
                """);

            // Fee plans are immutable versions (docs/PAYMENT_LIFECYCLE.md §2).
            migrationBuilder.Sql(AppendOnlySql.Protect(MerchantsModule.Schema, "fee_plans"));
            migrationBuilder.Sql(AppendOnlySql.Protect(MerchantsModule.Schema, "fee_plan_currency_terms"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AppendOnlySql.Unprotect(MerchantsModule.Schema, "fee_plan_currency_terms"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect(MerchantsModule.Schema, "fee_plans"));
            migrationBuilder.Sql("ALTER TABLE identity.merchant_memberships DROP CONSTRAINT IF EXISTS fk_merchant_memberships_merchants_merchant_id;");

            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "fee_plan_currency_terms",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "merchant_currencies",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "merchants",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "fee_plans",
                schema: "merchants");
        }
    }
}
