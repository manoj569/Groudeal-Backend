using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceVariantSellerConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_group_buy_campaigns_product_variants_ProductVariantId",
                table: "group_buy_campaigns");

            migrationBuilder.DropForeignKey(
                name: "FK_product_variants_products_ProductId",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "IX_product_variants_ProductId",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "IX_group_buy_campaigns_ProductVariantId",
                table: "group_buy_campaigns");

            migrationBuilder.AddColumn<Guid>(
                name: "SellerId",
                table: "product_variants",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE product_variants AS variant SET \"SellerId\" = product.\"SellerId\" FROM products AS product WHERE product.\"Id\" = variant.\"ProductId\";");

            migrationBuilder.AlterColumn<Guid>(
                name: "SellerId",
                table: "product_variants",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_products_SellerId_Id",
                table: "products",
                columns: new[] { "SellerId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_product_variants_SellerId_Id",
                table: "product_variants",
                columns: new[] { "SellerId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_SellerId_ProductId",
                table: "product_variants",
                columns: new[] { "SellerId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_SellerId_ProductVariantId",
                table: "group_buy_campaigns",
                columns: new[] { "SellerId", "ProductVariantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_group_buy_campaigns_product_variants_SellerId_ProductVarian~",
                table: "group_buy_campaigns",
                columns: new[] { "SellerId", "ProductVariantId" },
                principalTable: "product_variants",
                principalColumns: new[] { "SellerId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_variants_products_SellerId_ProductId",
                table: "product_variants",
                columns: new[] { "SellerId", "ProductId" },
                principalTable: "products",
                principalColumns: new[] { "SellerId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_group_buy_campaigns_product_variants_SellerId_ProductVarian~",
                table: "group_buy_campaigns");

            migrationBuilder.DropForeignKey(
                name: "FK_product_variants_products_SellerId_ProductId",
                table: "product_variants");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_products_SellerId_Id",
                table: "products");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_product_variants_SellerId_Id",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "IX_product_variants_SellerId_ProductId",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "IX_group_buy_campaigns_SellerId_ProductVariantId",
                table: "group_buy_campaigns");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "product_variants");

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_ProductId",
                table: "product_variants",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_group_buy_campaigns_ProductVariantId",
                table: "group_buy_campaigns",
                column: "ProductVariantId");

            migrationBuilder.AddForeignKey(
                name: "FK_group_buy_campaigns_product_variants_ProductVariantId",
                table: "group_buy_campaigns",
                column: "ProductVariantId",
                principalTable: "product_variants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_variants_products_ProductId",
                table: "product_variants",
                column: "ProductId",
                principalTable: "products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
