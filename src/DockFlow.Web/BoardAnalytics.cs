using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using StackExchange.Redis;

namespace DockFlow.Web;

public sealed class BoardAnalytics(NpgsqlDataSource postgres, IDistributedCache cache, IMeterFactory meters)
{
    internal const string InstrumentationName = "DockFlow.Operations";
    internal static readonly ActivitySource Activities = new(InstrumentationName);
    private readonly Counter<long> cacheReads = meters.Create(InstrumentationName).CreateCounter<long>("dockflow.analytics.cache.reads");

    internal async Task<long> CompletedTodayAsync(Guid tenant, DateTimeOffset now, CancellationToken ct)
    {
        using var span = Activities.StartActivity("analytics.completed_today");
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var key = $"dockflow:completed:v1:{tenant:N}:{day:yyyyMMdd}";
        var cached = await WithCacheBudget(token => cache.GetStringAsync(key, token), ct);
        if (long.TryParse(cached, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0)
        {
            cacheReads.Add(1, new KeyValuePair<string, object?>("outcome", "hit"));
            return count;
        }
        cacheReads.Add(1, new KeyValuePair<string, object?>("outcome", "fallback"));
        await using var command = postgres.CreateCommand("""
            SELECT count(*) FROM public.audit_log
            WHERE tenant_id=@tenant AND kind='UnloadingCompleted' AND occurred_at>=@day AND occurred_at<@end
            """);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("end", day.AddDays(1));
        count = (long)(await command.ExecuteScalarAsync(ct))!;
        await WithCacheBudget(async token =>
        {
            await cache.SetStringAsync(key, count.ToString(CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15) }, token);
            return null;
        }, ct);
        return count;
    }

    private static async Task<string?> WithCacheBudget(Func<CancellationToken, Task<string?>> work, CancellationToken request)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(request);
        budget.CancelAfter(TimeSpan.FromMilliseconds(150));
        try { return await work(budget.Token).WaitAsync(budget.Token); }
        catch (OperationCanceledException) when (!request.IsCancellationRequested) { return null; }
        catch (RedisException) { return null; }
        catch (TimeoutException) { return null; }
    }
}
