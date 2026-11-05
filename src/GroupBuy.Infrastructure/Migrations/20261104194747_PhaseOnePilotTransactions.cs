using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PhaseOnePilotTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExtensionCount",
                table: "group_buy_campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "FinalQuantity",
                table: "group_buy_campaigns",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinalUnitPrice",
                table: "group_buy_campaigns",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalizedAtUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NearTargetAccepted",
                table: "group_buy_campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "NearTargetQuantity",
                table: "group_buy_campaigns",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RescueNotifiedAtUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceArea",
                table: "group_buy_campaigns",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "UsesTierPricing",
                table: "group_buy_campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "campaign_price_tiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_price_tiers", x => x.Id);
                    table.CheckConstraint("ck_tier_values", "\"Quantity\" > 0 AND \"UnitPrice\" > 0");
                    table.ForeignKey(
                        name: "FK_campaign_price_tiers_group_buy_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "group_buy_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_interests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceArea = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DesiredQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_interests", x => x.Id);
                    table.CheckConstraint("ck_interest_quantity", "\"DesiredQuantity\" > 0 AND \"DesiredQuantity\" <= 1000000");
                    table.ForeignKey(
                        name: "FK_customer_interests_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_interests_users_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    MaximumUnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaximumAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryFeeMinor = table.Column<long>(type: "bigint", nullable: false),
                    FinalAmountMinor = table.Column<long>(type: "bigint", nullable: true),
                    RefundedMinor = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProviderOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderPaymentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReconciledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RetryAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    FulfillmentMode = table.Column<int>(type: "integer", nullable: false),
                    AddressCiphertext = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    AddressDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_orders", x => x.Id);
                    table.CheckConstraint("ck_payment_amounts", "\"Quantity\" > 0 AND \"MaximumUnitPrice\" > 0 AND \"MaximumAmountMinor\" > 0 AND \"DeliveryFeeMinor\" >= 0 AND \"RefundedMinor\" BETWEEN 0 AND \"MaximumAmountMinor\" AND (\"FinalAmountMinor\" IS NULL OR \"FinalAmountMinor\" BETWEEN 0 AND \"MaximumAmountMinor\")");
                    table.CheckConstraint("ck_payment_state", "\"State\" BETWEEN 0 AND 7 AND \"FulfillmentMode\" BETWEEN 0 AND 1 AND \"Attempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_payment_orders_group_buy_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "group_buy_campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_orders_group_buy_reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "group_buy_reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_orders_users_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EventId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RetryAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_webhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_payment_receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RefundedMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_payment_receipts", x => x.Id);
                    table.UniqueConstraint("AK_sandbox_payment_receipts_LocalOrderId", x => x.LocalOrderId);
                    table.CheckConstraint("ck_sandbox_amount", "\"AmountMinor\" > 0 AND \"RefundedMinor\" BETWEEN 0 AND \"AmountMinor\"");
                });

            migrationBuilder.CreateTable(
                name: "order_fulfillments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PickupId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ChoiceConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BackupCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SecretExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SecretIssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    VerifiedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CodeVersion = table.Column<int>(type: "integer", nullable: false),
                    ExceptionReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_fulfillments", x => x.Id);
                    table.CheckConstraint("ck_fulfillment_values", "\"State\" BETWEEN 0 AND 10 AND \"FailedAttempts\" BETWEEN 0 AND 5 AND \"CodeVersion\" >= 0");
                    table.ForeignKey(
                        name: "FK_order_fulfillments_payment_orders_PaymentOrderId",
                        column: x => x.PaymentOrderId,
                        principalTable: "payment_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_fulfillments_pickup_records_PickupId",
                        column: x => x.PickupId,
                        principalTable: "pickup_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_fulfillments_users_VerifiedBy",
                        column: x => x.VerifiedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ProviderRefundId = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RetryAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_refunds", x => x.Id);
                    table.CheckConstraint("ck_refund_values", "\"AmountMinor\" > 0 AND \"Attempts\" >= 0 AND \"State\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_payment_refunds_payment_orders_PaymentOrderId",
                        column: x => x.PaymentOrderId,
                        principalTable: "payment_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_refund_receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RefundId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_refund_receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sandbox_refund_receipts_sandbox_payment_receipts_LocalOrder~",
                        column: x => x.LocalOrderId,
                        principalTable: "sandbox_payment_receipts",
                        principalColumn: "LocalOrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_phase_one_campaign",
                table: "group_buy_campaigns",
                sql: "\"ExtensionCount\" BETWEEN 0 AND 1 AND (\"FinalUnitPrice\" IS NULL OR \"FinalUnitPrice\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_campaign_price_tiers_CampaignId_Quantity",
                table: "campaign_price_tiers",
                columns: new[] { "CampaignId", "Quantity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_CustomerId_ProductId_ServiceArea",
                table: "customer_interests",
                columns: new[] { "CustomerId", "ProductId", "ServiceArea" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_ProductId",
                table: "customer_interests",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_ServiceArea_Active_UpdatedAt",
                table: "customer_interests",
                columns: new[] { "ServiceArea", "Active", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_order_fulfillments_PaymentOrderId",
                table: "order_fulfillments",
                column: "PaymentOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_fulfillments_PickupId",
                table: "order_fulfillments",
                column: "PickupId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_fulfillments_State_UpdatedAt",
                table: "order_fulfillments",
                columns: new[] { "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_order_fulfillments_TokenHash",
                table: "order_fulfillments",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_fulfillments_VerifiedBy",
                table: "order_fulfillments",
                column: "VerifiedBy");

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_CampaignId_State",
                table: "payment_orders",
                columns: new[] { "CampaignId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_CustomerId_CampaignId_IdempotencyKey",
                table: "payment_orders",
                columns: new[] { "CustomerId", "CampaignId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_Provider_ProviderOrderId",
                table: "payment_orders",
                columns: new[] { "Provider", "ProviderOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_Provider_ProviderPaymentId",
                table: "payment_orders",
                columns: new[] { "Provider", "ProviderPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_ReservationId",
                table: "payment_orders",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_orders_State_RetryAtUtc_ExpiresAtUtc",
                table: "payment_orders",
                columns: new[] { "State", "RetryAtUtc", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_PaymentOrderId_Reason",
                table: "payment_refunds",
                columns: new[] { "PaymentOrderId", "Reason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_State_RetryAtUtc",
                table: "payment_refunds",
                columns: new[] { "State", "RetryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_webhooks_Provider_EventId",
                table: "payment_webhooks",
                columns: new[] { "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_payment_receipts_LocalOrderId",
                table: "sandbox_payment_receipts",
                column: "LocalOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_payment_receipts_ProviderOrderId",
                table: "sandbox_payment_receipts",
                column: "ProviderOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_refund_receipts_LocalOrderId",
                table: "sandbox_refund_receipts",
                column: "LocalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_refund_receipts_RefundId",
                table: "sandbox_refund_receipts",
                column: "RefundId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM payment_orders) OR EXISTS (SELECT 1 FROM payment_webhooks)
                    OR EXISTS (SELECT 1 FROM payment_refunds) THEN
                        RAISE EXCEPTION 'Phase One financial records require an approved archival/restore procedure; destructive rollback refused';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropTable(
                name: "campaign_price_tiers");

            migrationBuilder.DropTable(
                name: "customer_interests");

            migrationBuilder.DropTable(
                name: "order_fulfillments");

            migrationBuilder.DropTable(
                name: "payment_refunds");

            migrationBuilder.DropTable(
                name: "payment_webhooks");

            migrationBuilder.DropTable(
                name: "sandbox_refund_receipts");

            migrationBuilder.DropTable(
                name: "payment_orders");

            migrationBuilder.DropTable(
                name: "sandbox_payment_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_phase_one_campaign",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "ExtensionCount",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "FinalQuantity",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "FinalUnitPrice",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "FinalizedAtUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "NearTargetAccepted",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "NearTargetQuantity",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "RescueNotifiedAtUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "ServiceArea",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "UsesTierPricing",
                table: "group_buy_campaigns");
        }
    }
}
