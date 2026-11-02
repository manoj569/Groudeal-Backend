using System.Net;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    [Fact, Trait("Sprint5", "Security")]
    public async Task Runtime_grants_template_denies_Admin_assignment_MFA_reset_audit_tampering_and_migrations()
    {
        var role = "sprint5_runtime_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/grant-runtime.sql"));
            var sql = (await File.ReadAllTextAsync(path)).Replace(":'app_role'", "@runtime_role");
            await using (var grants = new NpgsqlCommand(sql, owner)) { grants.Parameters.AddWithValue("runtime_role", role); await grants.ExecuteNonQueryAsync(); }
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Username = role, Password = "", Pooling = false }.ConnectionString;
            using var f = new ApiFactory(connection); using var client = f.CreateClient(); var user = await Register(client, "hardened-runtime"); await Login(client, user);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
            await using var runtime = new NpgsqlConnection(connection); await runtime.OpenAsync();
            foreach (var statement in new[] { "DELETE FROM audit_logs WHERE false", "UPDATE \"__EFMigrationsHistory\" SET \"ProductVersion\" = \"ProductVersion\" WHERE false", "CREATE TABLE public.forbidden_runtime_table(id int)", $"INSERT INTO identity_user_roles (\"UserId\", \"RoleId\") VALUES ('{user.Id}', '00000000-0000-0000-0000-000000000003')" })
            {
                await using var denied = new NpgsqlCommand(statement, runtime); var error = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync()); Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
            // Only the operator identity assigns Admin; runtime then cannot reopen its enrollment window.
            await using (var assign = new NpgsqlCommand($"INSERT INTO identity_user_roles (\"UserId\",\"RoleId\") VALUES ('{user.Id}','00000000-0000-0000-0000-000000000003')", owner)) await assign.ExecuteNonQueryAsync();
            await using var reset = new NpgsqlCommand($"UPDATE identity_accounts SET \"MfaEnrollmentAllowedUntilUtc\" = now() + interval '15 minutes' WHERE \"Id\" = '{user.Id}'", runtime);
            var blocked = await Assert.ThrowsAsync<PostgresException>(() => reset.ExecuteNonQueryAsync()); Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, blocked.SqlState);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP TRIGGER IF EXISTS groudeal_runtime_admin_security ON identity_accounts; DROP FUNCTION IF EXISTS public.groudeal_guard_admin_security(); DROP POLICY IF EXISTS groudeal_runtime_select ON identity_user_roles; DROP POLICY IF EXISTS groudeal_runtime_insert ON identity_user_roles; DROP POLICY IF EXISTS groudeal_runtime_delete ON identity_user_roles; ALTER TABLE identity_user_roles DISABLE ROW LEVEL SECURITY; DROP OWNED BY {role}; DROP ROLE {role};", owner); await cleanup.ExecuteNonQueryAsync();
        }
    }
}
