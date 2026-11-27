using DockFlow.Application;
using System.Globalization;

namespace DockFlow.Web;

public sealed class BoardPageQuery(IDockOperations operations, BoardAnalytics analytics, TimeProvider clock)
{
    internal async Task<BoardPage> ReadAsync(Actor actor, string? day, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var board = await operations.BoardAsync(actor, ct);
        var parsed = DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new(board, now, await analytics.CompletedTodayAsync(actor.TenantId, now, ct),
            parsed && Math.Abs(date.DayNumber - today.DayNumber) <= 365 ? date : today);
    }
}
