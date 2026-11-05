using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceInventoryVariantSellerOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inventory_product_variants_ProductVariantId",
                table: "inventory");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_SellerId_ProductVariantId",
                table: "inventory",
                columns: new[] { "SellerId", "ProductVariantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_product_variants_SellerId_ProductVariantId",
                table: "inventory",
                columns: new[] { "SellerId", "ProductVariantId" },
                principalTable: "product_variants",
                principalColumns: new[] { "SellerId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inventory_product_variants_SellerId_ProductVariantId",
                table: "inventory");

            migrationBuilder.DropIndex(
                name: "IX_inventory_SellerId_ProductVariantId",
                table: "inventory");

            migrationBuilder.AddForeignKey(
                name: "FK_inventory_product_variants_ProductVariantId",
                table: "inventory",
                column: "ProductVariantId",
                principalTable: "product_variants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
