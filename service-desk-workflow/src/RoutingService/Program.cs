using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ServiceDesk.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddSingleton<FaultController>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("routing-service"))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapPost("/api/routing/assignments", AssignAsync);

if (app.Environment.IsDevelopment())
{
    app.MapGet("/__faults", (FaultController faults) => Results.Ok(faults.Current));
    app.MapPost("/__faults/{mode}", (string mode, FaultController faults) =>
    {
        try
        {
            faults.Configure(mode);
            return Results.Ok(faults.Current);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = "mode must be normal, transient, slow, or unavailable" });
        }
    });
}

app.Run();

static async Task<IResult> AssignAsync(
    RoutingRequest request,
    HttpContext context,
    FaultController faults,
    CancellationToken cancellationToken)
{
    var fault = await faults.ApplyAsync(cancellationToken);
    if (fault is not null)
    {
        return Results.Json(new { error = fault }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (request.TicketId == Guid.Empty ||
        string.IsNullOrWhiteSpace(request.Category) ||
        !string.Equals(
            context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
            request.TicketId.ToString("D"),
            StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "a matching Idempotency-Key is required" });
    }

    var assignment = request.Category.ToLowerInvariant() switch
    {
        "account" => new RoutingResponse("Service Desk", 8),
        "hardware" => new RoutingResponse("End User Computing", 24),
        "software" => new RoutingResponse("Application Support", 12),
        "network" => new RoutingResponse("Network Operations", 4),
        "other" => new RoutingResponse("Service Desk", 24),
        _ => null
    };

    return assignment is null
        ? Results.BadRequest(new { error = "unsupported_category" })
        : Results.Ok(assignment);
}
