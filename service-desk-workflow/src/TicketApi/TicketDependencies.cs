using System.Net.Http.Json;
using ServiceDesk.Contracts;

namespace TicketApi;

public sealed class RoutingClient(DependencyInvoker invoker, IConfiguration configuration)
{
    private readonly string _baseAddress = configuration["Services:Routing"]
        ?? throw new InvalidOperationException("Services:Routing must be configured.");

    public Task<RoutingResponse> AssignAsync(Guid ticketId, string category, CancellationToken cancellationToken) =>
        invoker.SendJsonAsync<RoutingResponse>(
            "routing",
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(new Uri(_baseAddress), "/api/routing/assignments"))
                {
                    Content = JsonContent.Create(new RoutingRequest(ticketId, category))
                };
                request.Headers.TryAddWithoutValidation("Idempotency-Key", ticketId.ToString("D"));
                return request;
            },
            cancellationToken);
}

public sealed class NotificationClient(DependencyInvoker invoker, IConfiguration configuration)
{
    private readonly string _baseAddress = configuration["Services:Notifications"]
        ?? throw new InvalidOperationException("Services:Notifications must be configured.");

    public Task<NotificationResponse> NotifyAsync(
        NotificationRequest notification,
        CancellationToken cancellationToken) =>
        invoker.SendJsonAsync<NotificationResponse>(
            "notifications",
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(new Uri(_baseAddress), "/api/notifications"))
                {
                    Content = JsonContent.Create(notification)
                };
                request.Headers.TryAddWithoutValidation(
                    "Idempotency-Key",
                    $"ticket:{notification.TicketId:D}:created");
                return request;
            },
            cancellationToken);
}
