using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GroupDealDemandOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DemandOfferNotifiedAtUtc",
                table: "group_buy_campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferTerms",
                table: "group_buy_campaigns",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "OriginatingDemandId",
                table: "group_buy_campaigns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_type",
                table: "products",
                sql: "\"ProductType\" BETWEEN 0 AND 2");

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_OriginatingDemandId_ServiceArea",
                table: "group_buy_campaigns",
                columns: new[] { "OriginatingDemandId", "ServiceArea" });

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_OriginatingDemandId_Status_Id",
                table: "group_buy_campaigns",
                columns: new[] { "OriginatingDemandId", "Status", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_group_buy_campaigns_discovery_demands_OriginatingDemandId_S~",
                table: "group_buy_campaigns",
                columns: new[] { "OriginatingDemandId", "ServiceArea" },
                principalTable: "discovery_demands",
                principalColumns: new[] { "Id", "ServiceArea" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Group deal data requires a forward fix; destructive downgrade is prohibited.");
        }
    }
}
