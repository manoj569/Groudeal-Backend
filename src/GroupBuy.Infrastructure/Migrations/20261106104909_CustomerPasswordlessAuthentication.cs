using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CustomerPasswordlessAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_auth_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Destination = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenVersion = table.Column<int>(type: "integer", nullable: true),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_auth_challenges", x => x.Id);
                    table.CheckConstraint("ck_customer_challenge_attempts", "\"FailedAttempts\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_customer_auth_challenges_identity_accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "identity_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_consents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_consents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_consents_identity_accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "identity_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_identity_accounts_PhoneNumber",
                table: "identity_accounts",
                column: "PhoneNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_auth_challenges_Channel_Destination_CreatedAtUtc",
                table: "customer_auth_challenges",
                columns: new[] { "Channel", "Destination", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_auth_challenges_UserId",
                table: "customer_auth_challenges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_consents_UserId_DocumentType_DocumentVersion",
                table: "user_consents",
                columns: new[] { "UserId", "DocumentType", "DocumentVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_auth_challenges");

            migrationBuilder.DropTable(
                name: "user_consents");

            migrationBuilder.DropIndex(
                name: "IX_identity_accounts_PhoneNumber",
                table: "identity_accounts");
        }
    }
}
