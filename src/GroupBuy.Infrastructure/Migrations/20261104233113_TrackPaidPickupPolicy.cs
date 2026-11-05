using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackPaidPickupPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pickup_status",
                table: "pickup_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pickup_status",
                table: "pickup_records",
                sql: "\"Status\" BETWEEN 0 AND 4 AND \"PaymentPolicy\" IN (0, 1) AND \"FailedAttempts\" >= 0");
            migrationBuilder.Sql("""
                UPDATE pickup_records AS p SET "PaymentPolicy" = 1
                FROM order_fulfillments AS f JOIN payment_orders AS o ON o."Id" = f."PaymentOrderId"
                WHERE f."PickupId" = p."Id" AND o."CapturedAtUtc" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM pickup_records WHERE "PaymentPolicy" = 1) THEN
                        RAISE EXCEPTION 'Paid pickup records cannot become untracked; rollback refused';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_pickup_status",
                table: "pickup_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pickup_status",
                table: "pickup_records",
                sql: "\"Status\" BETWEEN 0 AND 4 AND \"PaymentPolicy\" = 0 AND \"FailedAttempts\" >= 0");
        }
    }
}
