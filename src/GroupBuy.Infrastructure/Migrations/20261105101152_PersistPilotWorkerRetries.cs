using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersistPilotWorkerRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pilot_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CycleAttempts = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pilot_jobs", x => x.Id);
                    table.CheckConstraint("ck_pilot_job_retry", "\"Attempts\" >= 0 AND \"CycleAttempts\" BETWEEN 0 AND 10 AND \"RetryCount\" >= 0 AND \"State\" BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateIndex(
                name: "IX_pilot_jobs_Stage_ItemId",
                table: "pilot_jobs",
                columns: new[] { "Stage", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pilot_jobs_State_NextAttemptAtUtc",
                table: "pilot_jobs",
                columns: new[] { "State", "NextAttemptAtUtc" });
            // Preserve pre-ledger failures/exhaustion without changing financial records.
            migrationBuilder.Sql("""
                INSERT INTO pilot_jobs ("Id","Stage","ItemId","State","Attempts","CycleAttempts","RetryCount","LastAttemptAtUtc","NextAttemptAtUtc","LeaseId","ErrorCode")
                SELECT gen_random_uuid(), 'payment_reconciliation', "Id", CASE WHEN "Attempts">=10 THEN 3 ELSE 2 END, "Attempts", LEAST("Attempts",10), 0, NULL, CASE WHEN "Attempts">=10 THEN NULL ELSE "RetryAtUtc" END, NULL, 'legacy_reconciliation_required'
                FROM payment_orders WHERE "CapturedAtUtc" IS NULL AND ("Attempts">=10 OR "ErrorCode" IS NOT NULL);
                INSERT INTO pilot_jobs ("Id","Stage","ItemId","State","Attempts","CycleAttempts","RetryCount","LastAttemptAtUtc","NextAttemptAtUtc","LeaseId","ErrorCode")
                SELECT gen_random_uuid(), 'webhook_reconciliation', "Id", CASE WHEN "Attempts">=10 THEN 3 ELSE 2 END, "Attempts", LEAST("Attempts",10), 0, NULL, CASE WHEN "Attempts">=10 THEN NULL ELSE "RetryAtUtc" END, NULL, 'legacy_webhook_reconciliation_required'
                FROM payment_webhooks WHERE "ProcessedAtUtc" IS NULL AND "Attempts">0;
                INSERT INTO pilot_jobs ("Id","Stage","ItemId","State","Attempts","CycleAttempts","RetryCount","LastAttemptAtUtc","NextAttemptAtUtc","LeaseId","ErrorCode")
                SELECT gen_random_uuid(), 'refund_processing', "Id", CASE WHEN "State"=3 THEN 3 ELSE 2 END, "Attempts", LEAST("Attempts",10), 0, NULL, CASE WHEN "State"=3 THEN NULL ELSE "RetryAtUtc" END, NULL, 'legacy_refund_processing_required'
                FROM payment_refunds WHERE "State"<>2 AND ("State"=3 OR "ErrorCode" IS NOT NULL);
                INSERT INTO pilot_jobs ("Id","Stage","ItemId","State","Attempts","CycleAttempts","RetryCount","LastAttemptAtUtc","NextAttemptAtUtc","LeaseId","ErrorCode")
                SELECT gen_random_uuid(), CASE WHEN "Status"=2 THEN 'campaign_opening' ELSE 'campaign_finalization' END, "Id", CASE WHEN "WorkerFailureCount">=10 THEN 3 ELSE 2 END, "WorkerFailureCount", LEAST("WorkerFailureCount",10), 0, NULL, CASE WHEN "WorkerFailureCount">=10 THEN NULL ELSE "WorkerRetryAtUtc" END, NULL, 'legacy_campaign_worker_failure'
                FROM group_buy_campaigns WHERE "UsesTierPricing" AND "WorkerFailureCount">0 AND "FinalizedAtUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM pilot_jobs) THEN
                        RAISE EXCEPTION 'Durable pilot retry evidence exists; rollback refused';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropTable(
                name: "pilot_jobs");
        }
    }
}
