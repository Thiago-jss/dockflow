namespace DockFlow.Application;

public interface ICompletedUnloadingsReader
{
    Task<long> CountAsync(Guid tenantId, DateTimeOffset utcDayStart, CancellationToken ct);
}
