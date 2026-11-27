using Npgsql;

namespace DockFlow.Worker;

internal sealed class WorkerStore(NpgsqlDataSource dataSource)
{
    internal async Task<IReadOnlyList<EventEnvelope>> ReadOutboxAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, tenant_id, kind, aggregate_id, occurred_at
            FROM public.outbox WHERE published_at IS NULL ORDER BY occurred_at, id LIMIT 50
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var messages = new List<EventEnvelope>();
        while (await reader.ReadAsync(ct))
            messages.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4), EventEnvelope.CurrentVersion));
        return messages;
    }

    internal async Task MarkPublishedAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.outbox SET published_at = statement_timestamp() WHERE id = $1 AND published_at IS NULL
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task ApplyAsync(EventEnvelope message, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var inbox = new NpgsqlCommand("""
            INSERT INTO public.inbox (id) VALUES ($1) ON CONFLICT (id) DO NOTHING
            """, connection, transaction);
        inbox.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.Id });

        if (await inbox.ExecuteNonQueryAsync(ct) == 1 && message.Kind == EventEnvelope.DockUnavailable)
        {
            await using var incidents = new NpgsqlCommand("""
                INSERT INTO public.incidents (id, tenant_id, dock_id, visit_id, kind, detected_at, status)
                SELECT gen_random_uuid(), v.tenant_id, v.dock_id, v.id, 'Maintenance', statement_timestamp(), 'Open'
                FROM public.visits v
                JOIN public.docks d ON d.id = v.dock_id AND d.tenant_id = v.tenant_id
                WHERE v.tenant_id = $1 AND v.dock_id = $2 AND NOT d.operational
                  AND v.status::text IN ('Scheduled', 'CheckedIn', 'Assigned', 'InProgress')
                ON CONFLICT (tenant_id, dock_id, visit_id, kind) DO NOTHING
                """, connection, transaction);
            incidents.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.TenantId });
            incidents.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.AggregateId });
            await incidents.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    internal async Task<int> ScanOverrunsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.incidents (id, tenant_id, dock_id, visit_id, kind, detected_at, status)
            SELECT gen_random_uuid(), v.tenant_id, v.dock_id, v.id, 'Overrun', statement_timestamp(), 'Open'
            FROM public.visits v
            WHERE v.status::text = 'InProgress' AND v.ends_at < statement_timestamp()
              AND NOT EXISTS (SELECT 1 FROM public.incidents i WHERE i.tenant_id = v.tenant_id
                  AND i.dock_id = v.dock_id AND i.visit_id = v.id AND i.kind = 'Overrun')
            ORDER BY v.ends_at, v.id LIMIT 100
            ON CONFLICT (tenant_id, dock_id, visit_id, kind) DO NOTHING
            """);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
