using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    private const string LastPreOwnershipMigration = "20261102012900_AdminMfaSecurity";

    [Fact, Trait("Sprint52", "MigrationUpgrade")]
    public async Task Ownership_migrations_preserve_representative_legacy_data_and_reject_cross_seller_links()
    {
        var name = "sprint52_upgrade_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.Connection);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Database = name, Pooling = false }.ConnectionString;
            await using var db = CreateMigrationDb(connection);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(LastPreOwnershipMigration);
            var ids = await InsertLegacyRowsAsync(connection, includeSecondSeller: true, crossSellerCampaign: false);

            await migrator.MigrateAsync();

            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM sellers WHERE \"Id\" = @id", ids.SellerA));
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM products WHERE \"Id\" = @id AND \"SellerId\" = @seller", ids.ProductA, ids.SellerA));
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM product_variants WHERE \"Id\" = @id AND \"SellerId\" = @seller", ids.VariantA, ids.SellerA));
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM inventory WHERE \"Id\" = @id AND \"SellerId\" = @seller AND \"ProductVariantId\" = @variant", ids.Inventory, ids.SellerA, ids.VariantA));
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM group_buy_campaigns WHERE \"Id\" = @id AND \"ProductVariantId\" = @variant", ids.Campaign, ids.VariantA));
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM group_buy_reservations WHERE \"Id\" = @id", ids.Reservation));
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM audit_logs WHERE \"Id\" = @id AND \"Action\" = 'Sprint52LegacyAuditProof'", ids.Audit));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
                "UPDATE group_buy_campaigns SET \"ProductVariantId\" = @variant WHERE \"Id\" = @id", ids.VariantB, ids.Campaign));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
                "UPDATE inventory SET \"ProductVariantId\" = @variant WHERE \"Id\" = @id", ids.VariantB, ids.Inventory));
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", owner); await drop.ExecuteNonQueryAsync(); }
    }

    [Fact, Trait("Sprint52", "MigrationUpgrade")]
    public async Task Invalid_legacy_cross_seller_campaign_data_fails_transactionally_without_reassignment()
    {
        var name = "sprint52_invalid_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.Connection);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Database = name, Pooling = false }.ConnectionString;
            await using var db = CreateMigrationDb(connection);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(LastPreOwnershipMigration);
            var ids = await InsertLegacyRowsAsync(connection, includeSecondSeller: true, crossSellerCampaign: true);

            var migrationError = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync());
            Assert.Equal("23503", FindPostgresException(migrationError)?.SqlState);

            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal(0, await ScalarAsync<long>(connection,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'product_variants' AND column_name = 'SellerId'"));
            Assert.Equal(ids.VariantB, await ScalarAsync<Guid>(connection,
                "SELECT \"ProductVariantId\" FROM group_buy_campaigns WHERE \"Id\" = @id", ids.Campaign));
            Assert.Equal(1, await ScalarAsync<long>(connection,
                "SELECT count(*) FROM audit_logs WHERE \"Id\" = @id AND \"Action\" = 'Sprint52LegacyAuditProof'", ids.Audit));
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", owner); await drop.ExecuteNonQueryAsync(); }
    }

    private static MarketplaceDb CreateMigrationDb(string connection) =>
        new(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(connection).Options);

    private static async Task<LegacyIds> InsertLegacyRowsAsync(string connection, bool includeSecondSeller, bool crossSellerCampaign)
    {
        var ids = new LegacyIds(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(@"
INSERT INTO identity_accounts (""Id"",""TokenVersion"",""Email"",""NormalizedEmail"",""EmailConfirmed"",""SecurityStamp"",""ConcurrencyStamp"",""PhoneNumberConfirmed"",""TwoFactorEnabled"",""LockoutEnabled"",""AccessFailedCount"") VALUES
(@ownerA,0,'owner-a@example.invalid','OWNER-A@EXAMPLE.INVALID',false,'s','c',false,false,true,0),
(@ownerB,0,'owner-b@example.invalid','OWNER-B@EXAMPLE.INVALID',false,'s','c',false,false,true,0),
(@customer,0,'customer@example.invalid','CUSTOMER@EXAMPLE.INVALID',false,'s','c',false,false,true,0);
INSERT INTO users (""Id"",""Email"",""PhoneNumberVerified"",""EmailVerified"",""DisplayName"",""Status"",""CreatedAt"",""UpdatedAt"") VALUES
(@ownerA,'owner-a@example.invalid',false,false,'Synthetic owner A',0,now(),now()),
(@ownerB,'owner-b@example.invalid',false,false,'Synthetic owner B',0,now(),now()),
(@customer,'customer@example.invalid',false,false,'Synthetic customer',0,now(),now());
INSERT INTO sellers (""Id"",""OwnerUserId"",""BusinessName"",""SellerType"",""Description"",""ContactEmail"",""ContactPhone"",""VerificationStatus"",""Status"",""CreatedAt"",""UpdatedAt"") VALUES
(@sellerA,@ownerA,'Synthetic seller A',0,'synthetic','a@example.invalid','+911111111111',1,0,now(),now()),
(@sellerB,@ownerB,'Synthetic seller B',0,'synthetic','b@example.invalid','+922222222222',1,0,now(),now());
INSERT INTO seller_locations (""Id"",""SellerId"",""AddressLine1"",""Area"",""City"",""State"",""PostalCode"",""IsPickupEnabled"",""IsActive"") VALUES
(@locationA,@sellerA,'Synthetic road','Area','City','State','110001',true,true),
(@locationB,@sellerB,'Synthetic road','Area','City','State','110002',true,true);
INSERT INTO products (""Id"",""SellerId"",""CategoryId"",""Name"",""Slug"",""Description"",""ProductType"",""Status"",""CreatedAt"",""UpdatedAt"") VALUES
(@productA,@sellerA,'10000000-0000-0000-0000-000000000001','Synthetic product A','s52-a','synthetic',0,0,now(),now()),
(@productB,@sellerB,'10000000-0000-0000-0000-000000000001','Synthetic product B','s52-b','synthetic',0,0,now(),now());
INSERT INTO product_variants (""Id"",""ProductId"",""SKU"",""VariantName"",""UnitOfMeasure"",""UnitQuantity"",""AttributesJson"",""IsActive"") VALUES
(@variantA,@productA,'s52-sku-a','one unit',0,1,'{}',true),
(@variantB,@productB,'s52-sku-b','one unit',0,1,'{}',true);
INSERT INTO inventory (""Id"",""SellerId"",""ProductVariantId"",""AvailableQuantity"",""Version"",""CreatedAt"",""UpdatedAt"") VALUES
(@inventory,@sellerA,@variantA,12,1,now(),now());
INSERT INTO group_buy_campaigns (""Id"",""SellerId"",""ProductVariantId"",""Title"",""Description"",""ReferenceUnitPrice"",""GroupUnitPrice"",""TargetQuantity"",""ReservedQuantity"",""MinimumReservationQuantity"",""MaximumReservationQuantity"",""StartsAtUtc"",""ExpiresAtUtc"",""Status"",""FulfillmentMode"",""SellerLocationId"",""CreatedAt"",""UpdatedAt"") VALUES
(@campaign,@sellerA,@campaignVariant,'Synthetic campaign','synthetic',100,80,20,2,1,10,now()-interval '1 hour',now()+interval '1 day',3,0,@locationA,now(),now());
INSERT INTO group_buy_reservations (""Id"",""CampaignId"",""CustomerUserId"",""Quantity"",""UnitPriceSnapshot"",""TotalPriceSnapshot"",""Status"",""IdempotencyKey"",""CreatedAt"",""UpdatedAt"") VALUES
(@reservation,@campaign,@customer,2,80,160,0,'sprint52-upgrade-proof',now(),now());
INSERT INTO audit_logs (""Id"",""Action"",""EntityType"",""EntityId"",""TimestampUtc"",""CorrelationId"",""SafeMetadataJson"") VALUES
(@audit,'Sprint52LegacyAuditProof','SyntheticCampaign',@campaign,now(),'sprint52-test','{}');", db);
        foreach (var parameter in new[] {
            ("ownerA", ids.OwnerA), ("ownerB", ids.OwnerB), ("customer", ids.Customer), ("sellerA", ids.SellerA), ("sellerB", ids.SellerB),
            ("locationA", ids.LocationA), ("locationB", ids.LocationB), ("productA", ids.ProductA), ("productB", ids.ProductB),
            ("variantA", ids.VariantA), ("variantB", ids.VariantB), ("inventory", ids.Inventory), ("campaign", ids.Campaign),
            ("reservation", ids.Reservation), ("audit", ids.Audit), ("campaignVariant", crossSellerCampaign ? ids.VariantB : ids.VariantA)
        }) command.Parameters.AddWithValue(parameter.Item1, parameter.Item2);
        await command.ExecuteNonQueryAsync();
        return ids;
    }

    private static async Task<T> ScalarAsync<T>(string connection, string sql, params object[] values)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        var names = System.Text.RegularExpressions.Regex.Matches(sql, "@([A-Za-z0-9_]+)").Select(match => match.Groups[1].Value).Distinct().ToArray();
        if (names.Length != values.Length) throw new InvalidOperationException("Synthetic migration assertion parameter mismatch.");
        for (var i = 0; i < names.Length; i++) command.Parameters.AddWithValue(names[i], values[i]);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connection, string sql, params object[] values)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        var names = System.Text.RegularExpressions.Regex.Matches(sql, "@([A-Za-z0-9_]+)").Select(match => match.Groups[1].Value).Distinct().ToArray();
        if (names.Length != values.Length) throw new InvalidOperationException("Synthetic migration assertion parameter mismatch.");
        for (var i = 0; i < names.Length; i++) command.Parameters.AddWithValue(names[i], values[i]);
        await command.ExecuteNonQueryAsync();
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres) return postgres;
        return null;
    }

    private sealed record LegacyIds(Guid OwnerA, Guid OwnerB, Guid Customer, Guid SellerA, Guid SellerB,
        Guid LocationA, Guid LocationB, Guid ProductA, Guid ProductB, Guid VariantA, Guid VariantB,
        Guid Inventory, Guid Campaign, Guid Reservation, Guid Audit);
}
