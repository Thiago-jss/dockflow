namespace DockFlow.Application;

public enum CargoKind { General, Refrigerated }
public enum VisitStatus { Scheduled, CheckedIn, Assigned, InProgress, Completed, Cancelled, NoShow }
public record Actor(Guid TenantId, Guid? CarrierId, string Subject);
public record Book(string Vehicle, CargoKind Cargo, int WeightKg, DateTimeOffset Start, DateTimeOffset End, Guid? DockId = null);
public enum VisitAction { CheckIn, Assign, Start, Complete, Cancel, NoShow }
public record Visit(Guid Id, Guid DockId, string DockCode, string Vehicle, CargoKind Cargo, int WeightKg, DateTimeOffset Start, DateTimeOffset End, VisitStatus Status, long Version);
public record DockView(Guid Id, string Code, CargoKind Cargo, int MaxWeightKg, bool Operational);
public record IncidentView(Guid Id, Guid DockId, Guid? VisitId, string Kind, DateTimeOffset DetectedAt, string Status);
public record Board(IReadOnlyList<DockView> Docks, IReadOnlyList<Visit> Visits, IReadOnlyList<IncidentView> Incidents);

public interface IDockOperations
{
    Task<Board> BoardAsync(Actor a, CancellationToken ct);
    Task<Visit> BookAsync(Actor a, Book input, string requestKey, CancellationToken ct);
    Task<Visit?> FindAsync(Actor a, Guid id, CancellationToken ct);
    Task<Visit> TransitionAsync(Actor a, Guid id, VisitAction action, long version, CancellationToken ct);
    Task BlockAsync(Actor a, Guid dockId, string reason, CancellationToken ct);
    Task<Visit> ReassignAsync(Actor a, Guid id, long version, CancellationToken ct);
}

public sealed class OperationRejected(string code, int status, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
