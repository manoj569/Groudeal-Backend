using Xunit;

namespace GroupBuy.IntegrationTests;

public sealed class TestDatabaseSafetyTests
{
    [Theory]
    [InlineData("Host=localhost;Port=55432;Database=groupbuy_sprint51;Username=test")]
    [InlineData("Host=groudeal-sprint51-postgres;Database=postgres;Username=test")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=postgres;Username=test")]
    public void TestDatabaseOverrideRejectsProtectedDatabaseEndpoint(string connectionString)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DatabaseFixture.ValidateAdminConnectionIsolated(connectionString));

        Assert.Equal("GROUPBUY_TEST_POSTGRES cannot target the protected Sprint 5.1 database endpoint.", error.Message);
        Assert.DoesNotContain("Username=test", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TestDatabaseOverrideAllowsDisposableLocalPostgresEndpoint()
    {
        DatabaseFixture.ValidateAdminConnectionIsolated(
            "Host=127.0.0.1;Port=55433;Database=postgres;Username=postgres");
    }

    [Fact]
    public void RuntimeRoleConnectionUsesItsTestCredentialAndDisablesPooling()
    {
        const string fixturePassword = "synthetic-fixture-only-password";
        var runtimePassword = DatabaseFixture.CreateTestRuntimePassword();
        var settings = DatabaseFixture.CreateRuntimeConnectionSettings(
            "Host=localhost;Port=55433;Database=groupbuy_test;Username=fixture_admin;Password=" + fixturePassword,
            "runtime_test",
            runtimePassword);

        Assert.True(settings.Password.AsSpan().SequenceEqual(runtimePassword.AsSpan()));
        Assert.False(settings.Password.AsSpan().SequenceEqual(fixturePassword.AsSpan()));
        Assert.Equal("runtime_test", settings.Username);
        Assert.False(settings.Pooling);
    }
}
