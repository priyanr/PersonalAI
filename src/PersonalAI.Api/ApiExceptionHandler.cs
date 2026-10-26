using Microsoft.AspNetCore.Diagnostics;
using PersonalAI.Core.Models;

namespace PersonalAI.Api;

/// <summary>The one place a service's request exception becomes an HTTP error, with the { message } body the UI reads.</summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            InvalidRequestException => StatusCodes.Status400BadRequest,
            NotFoundException => StatusCodes.Status404NotFound,
            ConflictException => StatusCodes.Status409Conflict,
            UpstreamUnavailableException => StatusCodes.Status503ServiceUnavailable,
            _ => 0
        };
        if (status == 0) return false;

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message }, ct);
        return true;
    }
}
