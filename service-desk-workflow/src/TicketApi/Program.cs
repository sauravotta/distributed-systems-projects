using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ServiceDesk.Contracts;
using TicketApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
var connectionString = builder.Configuration.GetConnectionString("Tickets")
    ?? throw new InvalidOperationException("ConnectionStrings:Tickets must be configured.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<TicketStore>();
builder.Services.AddSingleton<DependencyInvoker>();
builder.Services.AddTransient<RoutingClient>();
builder.Services.AddTransient<NotificationClient>();
builder.Services.AddHttpClient("routing", client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient("notifications", client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("ticket-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();
var store = app.Services.GetRequiredService<TicketStore>();
await store.InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (TicketStore ticketStore, CancellationToken cancellationToken) =>
{
    try
    {
        return await ticketStore.IsReadyAsync(cancellationToken)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/tickets", CreateTicketAsync);
app.MapGet("/api/tickets/{id:guid}", GetTicketAsync);
app.MapPost("/api/tickets/{id:guid}/notification-retry", RetryNotificationAsync);
app.Run();

static async Task<IResult> CreateTicketAsync(
    CreateTicketRequest? request,
    HttpContext context,
    TicketStore store,
    RoutingClient routingClient,
    NotificationClient notificationClient,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    if (request is null || !TicketRules.IsValid(request))
    {
        return Results.BadRequest(new
        {
            error = "invalid_ticket",
            message = "Provide a valid email, subject (max 200 characters), description (max 4000 characters), and supported category."
        });
    }

    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 128)
    {
        return Results.BadRequest(new
        {
            error = "invalid_idempotency_key",
            message = "The Idempotency-Key header is required and must be 8 to 128 characters."
        });
    }

    var normalizedRequest = request with
    {
        RequesterEmail = request.RequesterEmail.Trim().ToLowerInvariant(),
        Subject = request.Subject.Trim(),
        Description = request.Description.Trim(),
        Category = TicketRules.NormalizeCategory(request.Category)
    };
    var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalizedRequest)));
    var existing = await store.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
    if (existing is not null)
    {
        return existing.RequestHash == requestHash
            ? Results.Ok(existing.Response)
            : Results.Conflict(new { error = "idempotency_key_reused", message = "This key was already used for a different request." });
    }

    RoutingResponse routing;
    var ticketId = Guid.NewGuid();
    try
    {
        routing = await routingClient.AssignAsync(
            ticketId,
            normalizedRequest.Category,
            cancellationToken);
    }
    catch (DependencyUnavailableException exception)
    {
        return DependencyUnavailable(exception.Dependency);
    }
    catch (DependencyRejectedException exception)
    {
        return Results.Json(
            new { error = "dependency_rejected", dependency = exception.Dependency },
            statusCode: StatusCodes.Status502BadGateway);
    }

    var createdAt = DateTimeOffset.UtcNow;
    var created = await store.CreateAsync(
        ticketId,
        idempotencyKey,
        requestHash,
        normalizedRequest,
        routing,
        createdAt,
        cancellationToken);

    if (!created)
    {
        existing = await store.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException("Ticket insert conflicted but no existing idempotency record was found.");
        return existing.RequestHash == requestHash
            ? Results.Ok(existing.Response)
            : Results.Conflict(new { error = "idempotency_key_reused", message = "This key was already used for a different request." });
    }

    var ticket = new TicketResponse(
        ticketId,
        "created",
        normalizedRequest.RequesterEmail,
        normalizedRequest.Subject,
        normalizedRequest.Description,
        normalizedRequest.Category,
        routing.AssignedTeam,
        routing.SlaHours,
        createdAt,
        "pending");

    try
    {
        await notificationClient.NotifyAsync(
            new NotificationRequest(
                ticketId,
                normalizedRequest.RequesterEmail,
                $"Ticket {ticketId:D} created",
                $"Your ticket was assigned to {routing.AssignedTeam}. Target response: {routing.SlaHours} hours."),
            cancellationToken);
        await store.SetNotificationStatusAsync(ticketId, "sent", cancellationToken);
        ticket = ticket with { NotificationStatus = "sent" };
    }
    catch (DependencyUnavailableException exception)
    {
        logger.LogWarning(exception, "Ticket {TicketId} was created, but notification delivery failed", ticketId);
        await store.SetNotificationStatusAsync(ticketId, "failed", cancellationToken);
        ticket = ticket with { NotificationStatus = "failed" };
    }
    catch (DependencyRejectedException exception)
    {
        logger.LogWarning(exception, "Ticket {TicketId} was created, but notification service rejected delivery", ticketId);
        await store.SetNotificationStatusAsync(ticketId, "failed", cancellationToken);
        ticket = ticket with { NotificationStatus = "failed" };
    }

    return Results.Created($"/api/tickets/{ticketId:D}", ticket);
}

static async Task<IResult> GetTicketAsync(
    Guid id,
    TicketStore store,
    CancellationToken cancellationToken)
{
    var ticket = await store.GetByIdAsync(id, cancellationToken);
    return ticket is null ? Results.NotFound() : Results.Ok(ticket);
}

static async Task<IResult> RetryNotificationAsync(
    Guid id,
    TicketStore store,
    NotificationClient notificationClient,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var ticket = await store.GetByIdAsync(id, cancellationToken);
    if (ticket is null)
    {
        return Results.NotFound();
    }

    if (ticket.NotificationStatus == "sent")
    {
        return Results.Ok(ticket);
    }

    try
    {
        await notificationClient.NotifyAsync(
            new NotificationRequest(
                ticket.Id,
                ticket.RequesterEmail,
                $"Ticket {ticket.Id:D} created",
                $"Your ticket was assigned to {ticket.AssignedTeam}. Target response: {ticket.SlaHours} hours."),
            cancellationToken);
        await store.SetNotificationStatusAsync(ticket.Id, "sent", cancellationToken);
        return Results.Ok(ticket with { NotificationStatus = "sent" });
    }
    catch (DependencyUnavailableException exception)
    {
        logger.LogWarning(exception, "Notification retry failed for ticket {TicketId}", ticket.Id);
        await store.SetNotificationStatusAsync(ticket.Id, "failed", cancellationToken);
        return Results.Json(
            new { error = "notification_unavailable", ticketId = ticket.Id },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (DependencyRejectedException exception)
    {
        logger.LogWarning(exception, "Notification service rejected retry for ticket {TicketId}", ticket.Id);
        await store.SetNotificationStatusAsync(ticket.Id, "failed", cancellationToken);
        return Results.Json(
            new { error = "notification_rejected", ticketId = ticket.Id },
            statusCode: StatusCodes.Status502BadGateway);
    }
}

static IResult DependencyUnavailable(string dependency) =>
    Results.Json(
        new
        {
            error = "dependency_unavailable",
            dependency,
            message = "The required routing dependency is temporarily unavailable; no ticket was created."
        },
        statusCode: StatusCodes.Status503ServiceUnavailable);

public partial class Program;
