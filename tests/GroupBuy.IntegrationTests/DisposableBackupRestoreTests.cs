using Npgsql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact, Trait("Sprint52", "BackupRestore")]
    public async Task Synthetic_postgres_logical_backup_restores_into_separate_disposable_database()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = "s52_backup_src_" + suffix;
        var restored = "s52_backup_restore_" + suffix;
        var native = Environment.GetEnvironmentVariable("GROUPBUY_TEST_PG_BIN");
        var dump = native is null ? "/tmp/groudeal-sprint52-backup-" + suffix + ".dump" : Path.Combine(Path.GetTempPath(),"groudeal-backup-"+suffix+".dump");
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
            await PgTool("pg_dump", "-U", username, "--no-owner", "--no-privileges", "-Fc", "-f", dump, source);
            var listing = await PgTool("pg_restore", "--list", dump);
            Assert.Equal(0, listing.ExitCode);
            Assert.Contains("recovery_proof", listing.Stdout, StringComparison.Ordinal);
            await PgTool("pg_restore", "-U", username, "--exit-on-error", "-d", restored, dump);

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
            if(native is null) _ = await fixture.Container.ExecAsync(new List<string> { "rm", "-f", dump });
            else if(File.Exists(dump)) File.Delete(dump);
        }

        async Task<(long ExitCode,string Stdout)> PgTool(string tool,params string[] args)
        {
            if(native is null)
            {
                var result=await fixture.Container.ExecAsync(new[]{tool}.Concat(args).ToList());
                Assert.Equal(0,result.ExitCode);return(result.ExitCode ?? -1,result.Stdout);
            }
            // An explicit test-only path enables the identical backup/restore proof
            // on a fresh native PostgreSQL cluster. Never invoke a shell or log credentials.
            DatabaseFixture.ValidateAdminConnectionIsolated(builder.ConnectionString);
            Assert.True(builder.Host is "127.0.0.1" or "localhost" or "::1");
            var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(native,tool+(OperatingSystem.IsWindows()?".exe":""))) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(var argument in args)start.ArgumentList.Add(argument);
            start.Environment["PGHOST"]=builder.Host;start.Environment["PGPORT"]=builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["PGUSER"]=builder.Username;start.Environment["PGPASSWORD"]=builder.Password;
            using var process=System.Diagnostics.Process.Start(start)!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch(OperationCanceledException){process.Kill(entireProcessTree:true);throw;}
            await error;Assert.Equal(0,process.ExitCode);return(process.ExitCode,await output);
        }
    }

}
