using DockFlow.Application;
using Npgsql;

namespace DockFlow.Infrastructure;

internal sealed class PostgresCompletedUnloadingsReader(NpgsqlDataSource source) : ICompletedUnloadingsReader
{
    public async Task<long> CountAsync(Guid tenant, DateTimeOffset day, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT count(*) FROM public.audit_log
            WHERE tenant_id=@tenant AND kind='UnloadingCompleted' AND occurred_at>=@day AND occurred_at<@end
            """);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("end", day.AddDays(1));
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
