using Npgsql;
using ServiceDesk.Contracts;
using System.Reflection;

namespace TicketApi;

public sealed record StoredTicket(string RequestHash, TicketResponse Response);

public sealed class TicketStore(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ApplyMigrationAsync(dataSource, "001_tickets", "TicketApi.Migrations.001_tickets.sql", cancellationToken);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<StoredTicket?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_hash, id, requester_email, subject, description, category,
                   assigned_team, sla_hours, created_at, notification_status
            FROM tickets
            WHERE idempotency_key = @key
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTicket(reader) : null;
    }

    public async Task<TicketResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_hash, id, requester_email, subject, description, category,
                   assigned_team, sla_hours, created_at, notification_status
            FROM tickets
            WHERE id = @id
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTicket(reader).Response : null;
    }

    public async Task<bool> CreateAsync(
        Guid id,
        string idempotencyKey,
        string requestHash,
        CreateTicketRequest request,
        RoutingResponse routing,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO tickets (
                id, idempotency_key, request_hash, requester_email, subject, description,
                category, assigned_team, sla_hours, created_at, notification_status
            )
            VALUES (
                @id, @key, @hash, @email, @subject, @description,
                @category, @team, @sla, @created_at, 'pending'
            )
            ON CONFLICT (idempotency_key) DO NOTHING
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("hash", requestHash);
        command.Parameters.AddWithValue("email", request.RequesterEmail.Trim());
        command.Parameters.AddWithValue("subject", request.Subject.Trim());
        command.Parameters.AddWithValue("description", request.Description.Trim());
        command.Parameters.AddWithValue("category", TicketRules.NormalizeCategory(request.Category));
        command.Parameters.AddWithValue("team", routing.AssignedTeam);
        command.Parameters.AddWithValue("sla", routing.SlaHours);
        command.Parameters.AddWithValue("created_at", createdAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task SetNotificationStatusAsync(
        Guid id,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE tickets SET notification_status = @status WHERE id = @id");
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StoredTicket ReadTicket(NpgsqlDataReader reader)
    {
        var response = new TicketResponse(
            reader.GetGuid(1),
            "created",
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9));
        return new StoredTicket(reader.GetString(0), response);
    }

    private static async Task ApplyMigrationAsync(
        NpgsqlDataSource source,
        string migrationId,
        string resourceName,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using (var setup = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (id VARCHAR(100) PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())",
            connection))
        {
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('ticket-api-migrations'))",
            connection,
            transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE id = @id)",
            connection,
            transaction);
        check.Parameters.AddWithValue("id", migrationId);
        if (await check.ExecuteScalarAsync(cancellationToken) is true)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using (var migration = new NpgsqlCommand(sql, connection, transaction))
        {
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var record = new NpgsqlCommand(
            "INSERT INTO schema_migrations (id) VALUES (@id)",
            connection,
            transaction);
        record.Parameters.AddWithValue("id", migrationId);
        await record.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
