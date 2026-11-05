using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordFounderStockHandover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SellerHandedOverAtUtc",
                table: "order_fulfillments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM order_fulfillments WHERE "SellerHandedOverAtUtc" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Recorded seller handovers require an approved restore procedure; rollback refused';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropColumn(
                name: "SellerHandedOverAtUtc",
                table: "order_fulfillments");
        }
    }
}
