using FluentValidation;
using GroupBuy.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupBuy.API;

public sealed class Errors(IProblemDetailsService problems, ILogger<Errors> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch { BusinessException b => b.StatusCode, ValidationException => 400, BadHttpRequestException b => b.StatusCode, DbUpdateConcurrencyException => 409, DbUpdateException { InnerException: PostgresException { SqlState: "23505" or "23514" or "23503" } } => 409, _ => 500 };
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = status == 500 ? "An unexpected error occurred." : exception is BusinessException business ? business.Code : "Request could not be processed.", Detail = exception is BusinessException ? exception.Message : null, Instance = context.Request.Path };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (exception is BusinessException coded) problem.Extensions["code"] = coded.ErrorCode ?? coded.Code;
        if (exception is ValidationException v) problem.Extensions["errors"] = v.Errors.GroupBy(x => x.PropertyName).ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray());
        if (status == 500) logger.LogError("Request failed {ErrorType} {TraceId}; diagnostic chain: {ExceptionDiagnostics}", exception.GetType().Name, context.TraceIdentifier, SafeExceptionDiagnostics(exception));
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    private static string SafeExceptionDiagnostics(Exception exception)
    {
        var chain = new List<string>();
        var current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            // Exception messages are arbitrary provider/application text and can contain
            // credentials or customer data. Keep the exception types and structured,
            // bounded PostgreSQL classifications instead of trying to redact prose.
            var item = $"Type={SafeIdentifier(current.GetType().FullName)}";
            if (current is PostgresException postgres)
            {
                item += $"; SqlState={SafeSqlState(postgres.SqlState)}; Severity={SafeIdentifier(postgres.InvariantSeverity)}; Schema={SafeIdentifier(postgres.SchemaName)}; Table={SafeIdentifier(postgres.TableName)}; Column={SafeIdentifier(postgres.ColumnName)}; Constraint={SafeIdentifier(postgres.ConstraintName)}; DataType={SafeIdentifier(postgres.DataTypeName)}; Routine={SafeIdentifier(postgres.Routine)}";
            }
            chain.Add(item);
        }
        if (current is not null) chain.Add("InnerExceptionChain=truncated");
        return string.Join(" --> ", chain);
    }

    private static string SafeSqlState(string? value) => value is { Length: 5 } && value.All(char.IsLetterOrDigit) ? value : "withheld";

    private static string SafeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var safe = new string(value.Take(96).Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '_').ToArray());
        return safe.Length == 0 ? "none" : safe;
    }
}
