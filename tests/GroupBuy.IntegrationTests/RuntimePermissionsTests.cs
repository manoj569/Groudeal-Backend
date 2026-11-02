using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact]
    public async Task Runtime_role_can_register_and_login_without_DDL_or_audit_delete_rights()
    {
        var role = "sprint4_runtime_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE; GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role}; REVOKE UPDATE, DELETE ON audit_logs FROM {role};", owner))
            await create.ExecuteNonQueryAsync();
        try
        {
            // This login exists only in the loopback trust-authenticated disposable fixture.
            var settings = new NpgsqlConnectionStringBuilder(fixture.Connection) { Username = role, Password = "", Pooling = false };
            using (var runtime = new ApiFactory(settings.ConnectionString))
            using (var client = runtime.CreateClient())
            {
                var user = await Register(client, "limited-runtime"); await Login(client, user);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
            }
            await using var limited = new NpgsqlConnection(settings.ConnectionString); await limited.OpenAsync();
            foreach (var sql in new[] { "CREATE TABLE public.forbidden_runtime_table(id int)", "DELETE FROM audit_logs WHERE false" })
            {
                await using var denied = new NpgsqlCommand(sql, limited);
                var error = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP OWNED BY {role}; DROP ROLE {role};", owner); await cleanup.ExecuteNonQueryAsync();
        }
    }
}

