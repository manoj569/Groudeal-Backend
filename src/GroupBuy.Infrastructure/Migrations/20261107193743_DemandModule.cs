using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GroupBuy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DemandModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_discovery_demands_NormalizedTitle_ServiceArea",
                table: "discovery_demands");

            migrationBuilder.DropIndex(
                name: "IX_customer_interests_DemandId",
                table: "customer_interests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_interest_target",
                table: "customer_interests");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAtUtc",
                table: "discovery_demands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosureReason",
                table: "discovery_demands",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "discovery_demands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DemandType",
                table: "discovery_demands",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsCancelled",
                table: "discovery_demands",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LegacyIdentity",
                table: "discovery_demands",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "discovery_demands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RequestedQuantity",
                table: "customer_interests",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedUnit",
                table: "customer_interests",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequirementDetails",
                table: "customer_interests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequirementText",
                table: "customer_interests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_discovery_demands_Id_ServiceArea",
                table: "discovery_demands",
                columns: new[] { "Id", "ServiceArea" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_CreatedBy_CreatedAt_Id",
                table: "discovery_demands",
                columns: new[] { "CreatedBy", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_DemandType_CategoryId_ProductId_Normalize~",
                table: "discovery_demands",
                columns: new[] { "DemandType", "CategoryId", "ProductId", "NormalizedTitle", "ServiceArea" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_Id_ProductId_ServiceArea",
                table: "discovery_demands",
                columns: new[] { "Id", "ProductId", "ServiceArea" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_NormalizedTitle_ServiceArea",
                table: "discovery_demands",
                columns: new[] { "NormalizedTitle", "ServiceArea" },
                unique: true,
                filter: "\"LegacyIdentity\"");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_ProductId_ServiceArea",
                table: "discovery_demands",
                columns: new[] { "ProductId", "ServiceArea" },
                unique: true,
                filter: "\"ProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_demands_ServiceArea_IsOpen_DemandType_CreatedAt_Id",
                table: "discovery_demands",
                columns: new[] { "ServiceArea", "IsOpen", "DemandType", "CreatedAt", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_demand_lifecycle",
                table: "discovery_demands",
                sql: "NOT \"IsCancelled\" OR NOT \"IsOpen\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_demand_product_type",
                table: "discovery_demands",
                sql: "\"ProductId\" IS NULL OR (\"DemandType\" = 0 AND \"NormalizedTitle\" = '')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_demand_type",
                table: "discovery_demands",
                sql: "\"DemandType\" IN (0,1)");

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_CustomerId_CreatedAt_Id",
                table: "customer_interests",
                columns: new[] { "CustomerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_interests_DemandId_ServiceArea",
                table: "customer_interests",
                columns: new[] { "DemandId", "ServiceArea" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_interest_requirement",
                table: "customer_interests",
                sql: "(\"RequestedQuantity\" IS NULL AND \"RequestedUnit\" IS NULL) OR (\"RequestedQuantity\" IS NOT NULL AND \"RequestedUnit\" IS NOT NULL AND \"RequestedQuantity\" > 0 AND \"RequestedQuantity\" <= 1000000 AND \"RequestedUnit\" IN ('kg','l','piece','pack','household','project','hour','visit','square_metre'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_interest_target",
                table: "customer_interests",
                sql: "\"ProductId\" IS NOT NULL OR \"DemandId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_customer_interests_discovery_demands_DemandId_ServiceArea",
                table: "customer_interests",
                columns: new[] { "DemandId", "ServiceArea" },
                principalTable: "discovery_demands",
                principalColumns: new[] { "Id", "ServiceArea" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_discovery_demands_products_ProductId",
                table: "discovery_demands",
                column: "ProductId",
                principalTable: "products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_discovery_demands_users_CreatedBy",
                table: "discovery_demands",
                column: "CreatedBy",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Preserve all legacy product interests and attach their single aggregate identity.
            migrationBuilder.Sql("""
                UPDATE discovery_demands d SET "CreatedBy" = (
                  SELECT a."ActorUserId" FROM audit_logs a WHERE a."EntityId"=d."Id"
                    AND a."EntityType"='DiscoveryDemand' AND a."Action"='DiscoveryDemandCreated'
                    AND EXISTS(SELECT 1 FROM users u WHERE u."Id"=a."ActorUserId")
                  ORDER BY a."TimestampUtc",a."Id" LIMIT 1);
                INSERT INTO discovery_demands ("Id","Title","NormalizedTitle","ServiceArea","CategoryId","IsOpen","CreatedAt","UpdatedAt","DemandType","LegacyIdentity","IsCancelled","ProductId","CreatedBy")
                SELECT gen_random_uuid(),left(p."Name",100),'',i."ServiceArea",p."CategoryId",true,min(i."CreatedAt"),max(i."UpdatedAt"),0,false,false,p."Id",
                  (SELECT first_interest."CustomerId" FROM customer_interests first_interest WHERE first_interest."ProductId"=p."Id" AND first_interest."ServiceArea"=i."ServiceArea" ORDER BY first_interest."CreatedAt",first_interest."Id" LIMIT 1)
                FROM customer_interests i JOIN products p ON p."Id"=i."ProductId"
                GROUP BY p."Id",p."Name",p."CategoryId",i."ServiceArea";
                UPDATE customer_interests i SET "DemandId"=d."Id" FROM discovery_demands d
                  WHERE d."ProductId"=i."ProductId" AND d."ServiceArea"=i."ServiceArea";
                ALTER TABLE customer_interests ADD CONSTRAINT fk_interest_demand_product
                  FOREIGN KEY ("DemandId","ProductId","ServiceArea") REFERENCES discovery_demands ("Id","ProductId","ServiceArea") ON DELETE RESTRICT;
                CREATE FUNCTION public.groudeal_guard_demand_identity() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $guard$
                BEGIN
                  IF TG_OP='UPDATE' AND (NEW."ProductId",NEW."CategoryId",NEW."ServiceArea",NEW."DemandType",NEW."NormalizedTitle")
                    IS DISTINCT FROM (OLD."ProductId",OLD."CategoryId",OLD."ServiceArea",OLD."DemandType",OLD."NormalizedTitle") THEN
                    RAISE EXCEPTION 'Demand canonical identity is immutable' USING ERRCODE='23514';
                  END IF;
                  IF NEW."ProductId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM public.products p WHERE p."Id"=NEW."ProductId" AND p."CategoryId"=NEW."CategoryId") THEN
                    RAISE EXCEPTION 'Demand product/category mismatch' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $guard$;
                CREATE TRIGGER groudeal_demand_identity BEFORE INSERT OR UPDATE ON discovery_demands
                  FOR EACH ROW EXECUTE FUNCTION public.groudeal_guard_demand_identity();
                CREATE FUNCTION public.groudeal_guard_interest_demand() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $guard$
                BEGIN
                  IF NEW."DemandId" IS NOT NULL AND EXISTS(SELECT 1 FROM public.discovery_demands d WHERE d."Id"=NEW."DemandId" AND d."ProductId" IS DISTINCT FROM NEW."ProductId") THEN
                    RAISE EXCEPTION 'Interest must match aggregate product identity' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $guard$;
                CREATE TRIGGER groudeal_interest_demand BEFORE INSERT OR UPDATE OF "ProductId","DemandId","ServiceArea" ON customer_interests
                  FOR EACH ROW EXECUTE FUNCTION public.groudeal_guard_interest_demand();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Demand module data requires a forward fix; destructive downgrade is prohibited.");
        }
    }
}
