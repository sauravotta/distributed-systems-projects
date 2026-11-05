using Npgsql;
using ServiceDesk.Contracts;
using System.Reflection;

namespace NotificationService;

public sealed class NotificationStore(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var setup = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (id VARCHAR(100) PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())",
            connection))
        {
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('notification-service-migrations'))",
            connection,
            transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string migrationId = "001_notifications";
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

        await using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("NotificationService.Migrations.001_notifications.sql")
            ?? throw new InvalidOperationException("The notifications migration resource was not found.");
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

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<NotificationResponse> SaveAsync(
        NotificationRequest notification,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notifications (
                id, ticket_id, idempotency_key, recipient, subject, body, created_at
            )
            VALUES (
                @id, @ticket_id, @key, @recipient, @subject, @body, @created_at
            )
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING id
            """;

        await using var command = dataSource.CreateCommand(sql);
        var id = Guid.NewGuid();
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("ticket_id", notification.TicketId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("recipient", notification.Recipient);
        command.Parameters.AddWithValue("subject", notification.Subject);
        command.Parameters.AddWithValue("body", notification.Body);
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);

        var storedId = await command.ExecuteScalarAsync(cancellationToken);
        if (storedId is Guid insertedId)
        {
            return new NotificationResponse(insertedId, "accepted");
        }

        await using var existingCommand = dataSource.CreateCommand(
            "SELECT id FROM notifications WHERE idempotency_key = @key");
        existingCommand.Parameters.AddWithValue("key", idempotencyKey);
        var existingId = await existingCommand.ExecuteScalarAsync(cancellationToken);
        if (existingId is Guid duplicateId)
        {
            return new NotificationResponse(duplicateId, "accepted");
        }

        throw new InvalidOperationException("Notification insert conflicted but no idempotency record was found.");
    }
}
