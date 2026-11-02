using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Sprint2Operations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CommittedQuantity",
                table: "group_buy_campaigns",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmationDeadlineUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DecisionActorId",
                table: "group_buy_campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DecisionApproved",
                table: "group_buy_campaigns",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecisionAtUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionKey",
                table: "group_buy_campaigns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReason",
                table: "group_buy_campaigns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FulfilledQuantity",
                table: "group_buy_campaigns",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PickupDeadlineUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerErrorCode",
                table: "group_buy_campaigns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkerFailureCount",
                table: "group_buy_campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WorkerRetryAtUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_group_buy_campaigns_SellerId_Id",
                table: "group_buy_campaigns",
                columns: new[] { "SellerId", "Id" });

            migrationBuilder.CreateTable(
                name: "internal_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_internal_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_internal_notifications_users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductVariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvailableQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory", x => x.Id);
                    table.CheckConstraint("ck_inventory_available", "\"AvailableQuantity\" BETWEEN 0 AND 1000000 AND \"Version\" >= 0");
                    table.ForeignKey(
                        name: "FK_inventory_product_variants_ProductVariantId",
                        column: x => x.ProductVariantId,
                        principalTable: "product_variants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pickup_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PaymentPolicy = table.Column<int>(type: "integer", nullable: false),
                    Instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CodeExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CodeIssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CollectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CollectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pickup_records", x => x.Id);
                    table.CheckConstraint("ck_pickup_collected", "(\"Status\" = 2) = (\"CollectedAtUtc\" IS NOT NULL AND \"CollectedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("ck_pickup_status", "\"Status\" BETWEEN 0 AND 4 AND \"PaymentPolicy\" = 0 AND \"FailedAttempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_pickup_records_group_buy_campaigns_SellerId_CampaignId",
                        columns: x => new { x.SellerId, x.CampaignId },
                        principalTable: "group_buy_campaigns",
                        principalColumns: new[] { "SellerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pickup_records_group_buy_reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "group_buy_reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pickup_records_users_CollectedByUserId",
                        column: x => x.CollectedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pickup_records_users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_DecisionActorId",
                table: "group_buy_campaigns",
                column: "DecisionActorId");

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_Status_ConfirmationDeadlineUtc",
                table: "group_buy_campaigns",
                columns: new[] { "Status", "ConfirmationDeadlineUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_Status_PickupDeadlineUtc",
                table: "group_buy_campaigns",
                columns: new[] { "Status", "PickupDeadlineUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_commitments",
                table: "group_buy_campaigns",
                sql: "\"CommittedQuantity\" >= 0 AND \"FulfilledQuantity\" >= 0 AND \"CommittedQuantity\" + \"FulfilledQuantity\" <= \"TargetQuantity\" AND \"WorkerFailureCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_internal_notifications_DeduplicationKey",
                table: "internal_notifications",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_internal_notifications_RecipientUserId_CreatedAtUtc_Id",
                table: "internal_notifications",
                columns: new[] { "RecipientUserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_ProductVariantId",
                table: "inventory",
                column: "ProductVariantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_SellerId_Id",
                table: "inventory",
                columns: new[] { "SellerId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_pickup_records_CampaignId_Status_DeadlineUtc",
                table: "pickup_records",
                columns: new[] { "CampaignId", "Status", "DeadlineUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_pickup_records_CollectedByUserId",
                table: "pickup_records",
                column: "CollectedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_pickup_records_CustomerUserId_Id",
                table: "pickup_records",
                columns: new[] { "CustomerUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_pickup_records_ReservationId",
                table: "pickup_records",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pickup_records_SellerId_CampaignId",
                table: "pickup_records",
                columns: new[] { "SellerId", "CampaignId" });

            migrationBuilder.CreateIndex(
                name: "IX_pickup_records_SellerId_Status_Id",
                table: "pickup_records",
                columns: new[] { "SellerId", "Status", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_group_buy_campaigns_users_DecisionActorId",
                table: "group_buy_campaigns",
                column: "DecisionActorId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_group_buy_campaigns_users_DecisionActorId",
                table: "group_buy_campaigns");

            migrationBuilder.DropTable(
                name: "internal_notifications");

            migrationBuilder.DropTable(
                name: "inventory");

            migrationBuilder.DropTable(
                name: "pickup_records");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_group_buy_campaigns_SellerId_Id",
                table: "group_buy_campaigns");

            migrationBuilder.DropIndex(
                name: "IX_group_buy_campaigns_DecisionActorId",
                table: "group_buy_campaigns");

            migrationBuilder.DropIndex(
                name: "IX_group_buy_campaigns_Status_ConfirmationDeadlineUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropIndex(
                name: "IX_group_buy_campaigns_Status_PickupDeadlineUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_commitments",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "CommittedQuantity",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "ConfirmationDeadlineUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "DecisionActorId",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "DecisionApproved",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "DecisionAtUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "DecisionKey",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "DecisionReason",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "FulfilledQuantity",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "PickupDeadlineUtc",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "WorkerErrorCode",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "WorkerFailureCount",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "WorkerRetryAtUtc",
                table: "group_buy_campaigns");
        }
    }
}
