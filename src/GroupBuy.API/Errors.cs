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
        if (exception is ValidationException v) problem.Extensions["errors"] = v.Errors.GroupBy(x => x.PropertyName).ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray());
        if (status == 500) logger.LogError("Request failed {ErrorType} {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}
