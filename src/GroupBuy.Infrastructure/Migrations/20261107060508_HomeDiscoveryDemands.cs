using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HomeDiscoveryDemands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "customer_interests",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "DemandId",
                table: "customer_interests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discovery_demands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ServiceArea = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_demands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discovery_demands_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_CustomerId_DemandId",
                table: "customer_interests",
                columns: new[] { "CustomerId", "DemandId" },
                unique: true,
                filter: "\"DemandId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_DemandId",
                table: "customer_interests",
                column: "DemandId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_interest_target",
                table: "customer_interests",
                sql: "(\"ProductId\" IS NOT NULL) <> (\"DemandId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_CategoryId",
                table: "discovery_demands",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_NormalizedTitle_ServiceArea",
                table: "discovery_demands",
                columns: new[] { "NormalizedTitle", "ServiceArea" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_customer_interests_discovery_demands_DemandId",
                table: "customer_interests",
                column: "DemandId",
                principalTable: "discovery_demands",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Home discovery demand data requires a forward fix; destructive downgrade is prohibited.");
        }
    }
}
