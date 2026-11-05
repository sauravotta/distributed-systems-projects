using Npgsql;
using NotificationService;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ServiceDesk.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var connectionString = builder.Configuration.GetConnectionString("Notifications")
    ?? throw new InvalidOperationException("ConnectionStrings:Notifications must be configured.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<FaultController>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("notification-service"))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();
var store = app.Services.GetRequiredService<NotificationStore>();
await store.InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (NotificationStore notificationStore, CancellationToken cancellationToken) =>
{
    try
    {
        return await notificationStore.IsReadyAsync(cancellationToken)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/notifications", AcceptNotificationAsync);

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

static async Task<IResult> AcceptNotificationAsync(
    NotificationRequest? notification,
    HttpContext context,
    FaultController faults,
    NotificationStore store,
    CancellationToken cancellationToken)
{
    var fault = await faults.ApplyAsync(cancellationToken);
    if (fault is not null)
    {
        return Results.Json(new { error = fault }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (notification is null ||
        notification.TicketId == Guid.Empty ||
        string.IsNullOrWhiteSpace(notification.Recipient) ||
        notification.Recipient.Length > 254 ||
        !notification.Recipient.Contains('@', StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(notification.Subject) ||
        notification.Subject.Length > 200 ||
        string.IsNullOrWhiteSpace(notification.Body) ||
        notification.Body.Length > 4000)
    {
        return Results.BadRequest(new { error = "invalid_notification" });
    }

    var expectedKey = $"ticket:{notification.TicketId:D}:created";
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (!string.Equals(expectedKey, idempotencyKey, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_idempotency_key" });
    }

    var result = await store.SaveAsync(notification, expectedKey, cancellationToken);
    return Results.Accepted($"/api/notifications/{result.Id:D}", result);
}
