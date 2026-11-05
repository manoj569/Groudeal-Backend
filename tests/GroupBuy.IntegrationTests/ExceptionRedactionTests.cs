using GroupBuy.API;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public sealed class ExceptionRedactionTests
{
    [Fact]
    public async Task Unexpected_errors_return_generic_problem_and_log_only_safe_diagnostics()
    {
        const string password = "SYNTHETIC_DB_PASSWORD_92F1";
        const string token = "SYNTHETIC_BEARER_TOKEN_72AC";
        const string connection = "Host=private-db;Username=runtime;Password=SYNTHETIC_CONN_PASSWORD_881B";
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.synthetic-signature";

        using var loggerProvider = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(loggerProvider))
            .AddProblemDetails()
            .BuildServiceProvider();
        var handler = new Errors(services.GetRequiredService<IProblemDetailsService>(),
            services.GetRequiredService<ILogger<Errors>>());
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "trace-synthetic-52" };
        context.Response.Body = new MemoryStream();

        var postgres = new PostgresException(
            $"duplicate key; {connection}; Bearer {token}; {jwt}", "ERROR", "ERROR", "23505",
            schemaName: "public", tableName: "synthetic_orders", constraintName: "uq_synthetic_order");
        var exception = new InvalidOperationException(
            $"outer password={password}", new Exception($"nested token={token}; {connection}; {jwt}", postgres));

        Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));
        context.Response.Body.Position = 0;
        var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var log = string.Join("\n", loggerProvider.Messages);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("An unexpected error occurred.", response);
        Assert.Contains("trace-synthetic-52", response);
        Assert.Contains("trace-synthetic-52", log);
        Assert.Contains("System.InvalidOperationException", log);
        Assert.Contains("System.Exception", log);
        Assert.Contains("23505", log);
        Assert.Contains("synthetic_orders", log);
        Assert.Contains("uq_synthetic_order", log);
        Assert.DoesNotContain("duplicate key", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username=", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGci", log, StringComparison.Ordinal);
        Assert.DoesNotContain(password, log, StringComparison.Ordinal);
        Assert.DoesNotContain(token, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_CONN_PASSWORD_881B", log, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-signature", log, StringComparison.Ordinal);
        Assert.DoesNotContain("System.InvalidOperationException", response, StringComparison.Ordinal);
        Assert.DoesNotContain("23505", response, StringComparison.Ordinal);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }
}
