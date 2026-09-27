using Microsoft.AspNetCore.Diagnostics;
using MinecraftManager.Server.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace MinecraftManager.Server.Api.Errors;

public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails, ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = 409, Title = "The resource changed during this request.", Extensions = { ["code"] = "concurrency_conflict", ["traceId"] = context.TraceIdentifier } } });
        }
        if (exception is not DomainRuleException domain) return false;
        var status = domain.Code.EndsWith("not_found", StringComparison.Ordinal) ? StatusCodes.Status404NotFound : domain.Code.Contains("exists", StringComparison.Ordinal) || domain.Code.Contains("state", StringComparison.Ordinal) || domain.Code.Contains("immutable", StringComparison.Ordinal) ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
        logger.LogWarning("Domain request rejected with {ErrorCode}; trace {TraceId}", domain.Code, context.TraceIdentifier);
        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Title = "The request could not be completed.", Detail = domain.Message, Extensions = { ["code"] = domain.Code, ["traceId"] = context.TraceIdentifier } } });
    }
}
