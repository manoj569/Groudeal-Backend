using Npgsql;
using Testcontainers.PostgreSql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact, Trait("Sprint52", "BackupRestore")]
    public async Task Synthetic_postgres_logical_backup_restores_into_separate_disposable_database()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = "s52_backup_src_" + suffix;
        var restored = "s52_backup_restore_" + suffix;
        var dump = "/tmp/groudeal-sprint52-backup-" + suffix + ".dump";
        var builder = new NpgsqlConnectionStringBuilder(fixture.Connection) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var createSource = new NpgsqlCommand($"CREATE DATABASE {source}", admin)) await createSource.ExecuteNonQueryAsync();
            await using (var createRestore = new NpgsqlCommand($"CREATE DATABASE {restored}", admin)) await createRestore.ExecuteNonQueryAsync();
            var sourceBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = source };
            await using (var data = new NpgsqlConnection(sourceBuilder.ConnectionString))
            {
                await data.OpenAsync();
                await using var seed = new NpgsqlCommand("CREATE TABLE recovery_proof (id integer PRIMARY KEY, payload text NOT NULL); INSERT INTO recovery_proof VALUES (1,'synthetic-row-one'),(2,'synthetic-row-two');", data);
                await seed.ExecuteNonQueryAsync();
            }

            var username = builder.Username ?? "postgres";
            await AssertCommandAsync(fixture.Container, "pg_dump", "-U", username, "--no-owner", "--no-privileges", "-Fc", "-f", dump, source);
            var listing = await fixture.Container.ExecAsync(new List<string> { "pg_restore", "--list", dump });
            Assert.Equal(0, listing.ExitCode);
            Assert.Contains("recovery_proof", listing.Stdout, StringComparison.Ordinal);
            await AssertCommandAsync(fixture.Container, "pg_restore", "-U", username, "--exit-on-error", "-d", restored, dump);

            var restoredBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = restored };
            await using var check = new NpgsqlConnection(restoredBuilder.ConnectionString);
            await check.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*)::integer || ':' || string_agg(id::text || '=' || payload, ',' ORDER BY id) FROM recovery_proof", check);
            Assert.Equal("2:1=synthetic-row-one,2=synthetic-row-two", await command.ExecuteScalarAsync());
            await using var migrations = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'recovery_proof'", check);
            Assert.Equal(1L, (long)(await migrations.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var dropRestored = new NpgsqlCommand($"DROP DATABASE IF EXISTS {restored} WITH (FORCE)", admin);
            await dropRestored.ExecuteNonQueryAsync();
            await using var dropSource = new NpgsqlCommand($"DROP DATABASE IF EXISTS {source} WITH (FORCE)", admin);
            await dropSource.ExecuteNonQueryAsync();
            _ = await fixture.Container.ExecAsync(new List<string> { "rm", "-f", dump });
        }
    }

    private static async Task AssertCommandAsync(PostgreSqlContainer container, params string[] arguments)
    {
        var result = await container.ExecAsync(arguments.ToList());
        Assert.True(result.ExitCode == 0, "Disposable backup command failed; output withheld.");
    }
}
