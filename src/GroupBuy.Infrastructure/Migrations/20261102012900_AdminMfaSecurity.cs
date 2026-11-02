using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdminMfaSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaEnrollmentAllowedUntilUtc",
                table: "identity_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_mfa_accepted_codes",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_mfa_accepted_codes", x => new { x.UserId, x.CodeHash });
                    table.ForeignKey(
                        name: "FK_admin_mfa_accepted_codes_identity_accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "identity_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "admin_mfa_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenVersion = table.Column<int>(type: "integer", nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Enrollment = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_mfa_challenges", x => x.Id);
                    table.CheckConstraint("ck_mfa_attempts", "\"FailedAttempts\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_admin_mfa_challenges_identity_accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "identity_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_mfa_challenges_TokenHash",
                table: "admin_mfa_challenges",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_mfa_challenges_UserId_ExpiresAtUtc",
                table: "admin_mfa_challenges",
                columns: new[] { "UserId", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_mfa_accepted_codes");

            migrationBuilder.DropTable(
                name: "admin_mfa_challenges");

            migrationBuilder.DropColumn(
                name: "MfaEnrollmentAllowedUntilUtc",
                table: "identity_accounts");

}
    }
}
