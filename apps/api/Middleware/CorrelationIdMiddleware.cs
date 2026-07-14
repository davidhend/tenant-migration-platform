namespace MigrationPlatform.Api.Middleware;

/// <summary>
/// Assigns a correlation ID to every request: reuses an inbound
/// <c>X-Correlation-ID</c> header when present, otherwise generates one. The ID
/// is pushed into the logging scope (so every log line for the request carries
/// it) and echoed back in the response header for client-side correlation and
/// distributed tracing hand-off.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var inbound)
                            && !string.IsNullOrWhiteSpace(inbound)
            ? inbound.ToString()
            : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;

        // Echo back before the response starts.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }
}
