using System.Net.Mail;

namespace ServiceDesk.Contracts;

public sealed record CreateTicketRequest(
    string RequesterEmail,
    string Subject,
    string Description,
    string Category);

public sealed record TicketResponse(
    Guid Id,
    string Status,
    string RequesterEmail,
    string Subject,
    string Description,
    string Category,
    string AssignedTeam,
    int SlaHours,
    DateTimeOffset CreatedAt,
    string NotificationStatus);

public sealed record RoutingRequest(Guid TicketId, string Category);

public sealed record RoutingResponse(string AssignedTeam, int SlaHours);

public sealed record NotificationRequest(
    Guid TicketId,
    string Recipient,
    string Subject,
    string Body);

public sealed record NotificationResponse(Guid Id, string Status);

public sealed record FaultConfiguration(string Mode, int TransientFailuresRemaining);

public sealed class FaultController
{
    private int _mode;
    private int _transientFailuresRemaining;

    public FaultConfiguration Current => new(ModeName(Volatile.Read(ref _mode)), Volatile.Read(ref _transientFailuresRemaining));

    public void Configure(string mode)
    {
        var parsedMode = mode.ToLowerInvariant() switch
        {
            "normal" => 0,
            "transient" => 1,
            "slow" => 2,
            "unavailable" => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), "Mode must be normal, transient, slow, or unavailable.")
        };

        Volatile.Write(ref _transientFailuresRemaining, parsedMode == 1 ? 2 : 0);
        Volatile.Write(ref _mode, parsedMode);
    }

    public async Task<string?> ApplyAsync(CancellationToken cancellationToken)
    {
        switch (Volatile.Read(ref _mode))
        {
            case 1:
                while (true)
                {
                    var remaining = Volatile.Read(ref _transientFailuresRemaining);
                    if (remaining == 0)
                    {
                        return null;
                    }

                    if (Interlocked.CompareExchange(ref _transientFailuresRemaining, remaining - 1, remaining) == remaining)
                    {
                        return "transient_failure";
                    }
                }
            case 2:
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return null;
            case 3:
                return "dependency_unavailable";
            default:
                return null;
        }
    }

    private static string ModeName(int mode) => mode switch
    {
        1 => "transient",
        2 => "slow",
        3 => "unavailable",
        _ => "normal"
    };
}

public static class TicketRules
{
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        "account",
        "hardware",
        "software",
        "network",
        "other"
    };

    public static bool IsValid(CreateTicketRequest request) =>
        IsValidEmail(request.RequesterEmail) &&
        !string.IsNullOrWhiteSpace(request.Subject) &&
        request.Subject.Length <= 200 &&
        !string.IsNullOrWhiteSpace(request.Description) &&
        request.Description.Length <= 4000 &&
        !string.IsNullOrWhiteSpace(request.Category) &&
        Categories.Contains(request.Category.Trim());

    public static string NormalizeCategory(string category) => category.Trim().ToLowerInvariant();

    private static bool IsValidEmail(string email) =>
        !string.IsNullOrWhiteSpace(email) &&
        email.Length <= 254 &&
        MailAddress.TryCreate(email.Trim(), out var parsed) &&
        string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
}
