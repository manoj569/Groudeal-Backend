using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact, Trait("Sprint5", "Security")]
    public async Task Additive_MFA_migration_preserves_legacy_audit_during_upgrade_and_empty_security_rollback()
    {
        var name = "sprint5_migrations_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Database = name, Pooling = false }.ConnectionString;
            await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(connection).Options);
            var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261101131443_Sprint2Operations");
            var proof = new AuditLog { EntityId = Guid.NewGuid(), EntityType = "MigrationFixture", Action = "LegacyAuditProof" }; db.AuditLogs.Add(proof); await db.SaveChangesAsync();
            await migrator.MigrateAsync(); Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Id == proof.Id)); Assert.False(await db.MfaChallenges.AnyAsync());
            await migrator.MigrateAsync("20261101131443_Sprint2Operations"); Assert.True(await db.AuditLogs.AnyAsync(a => a.Id == proof.Id));
            await migrator.MigrateAsync(); Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync()); Assert.True(await db.AuditLogs.AnyAsync(a => a.Id == proof.Id));
            // Rollback is safe here only because this disposable security schema has never enrolled users.
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", owner); await drop.ExecuteNonQueryAsync(); }
    }
}
