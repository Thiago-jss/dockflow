using DockFlow.Application;

namespace DockFlow.Web;

public sealed record AppointmentResponse(Guid Id, Guid DockId, string DockCode, string Vehicle, CargoKind Cargo,
    int WeightKg, DateTimeOffset Start, DateTimeOffset End, VisitStatus Status, long Version)
{
    internal static AppointmentResponse From(Visit visit) => new(visit.Id, visit.DockId, visit.DockCode, visit.Vehicle,
        visit.Cargo, visit.WeightKg, visit.Start, visit.End, visit.Status, visit.Version);
}
