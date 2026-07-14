using MassTransit;

namespace CatalogAPI;

public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";

    public static string Normalize(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128
            ? value.Trim()
            : Guid.NewGuid().ToString("N");

    public static string From(Headers headers) => Normalize(headers.Get<string>(HeaderName));
}

public sealed class CorrelationContext
{
    public string Value { get; private set; } = Guid.NewGuid().ToString("N");

    public void Set(string value) => Value = CorrelationId.Normalize(value);
}

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext, CorrelationContext correlationContext)
    {
        var correlationId = CorrelationId.Normalize(httpContext.Request.Headers[CorrelationId.HeaderName].FirstOrDefault());
        correlationContext.Set(correlationId);
        httpContext.Response.Headers[CorrelationId.HeaderName] = correlationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(httpContext);
        }
    }
}
