using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CatalogAPI;

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var isBadRequest = exception is BadHttpRequestException;
        if (isBadRequest)
        {
            logger.LogWarning(exception, "Requisição inválida: {Message}", exception.Message);
        }
        else
        {
            logger.LogError(exception, "Uma exceção não tratada ocorreu: {Message}", exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = isBadRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError,
            Title = isBadRequest ? "Requisição inválida." : "Erro interno no servidor.",
            Detail = exception.Message
        };

        context.Response.StatusCode = problem.Status.Value;
        await context.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}

