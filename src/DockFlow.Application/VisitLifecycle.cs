namespace DockFlow.Application;

internal static class VisitLifecycle
{
    internal static (VisitStatus Status, string EventKind) Apply(VisitStatus current, VisitAction action, DateTimeOffset end, DateTimeOffset now) =>
        (current, action) switch
        {
            (VisitStatus.Scheduled, VisitAction.CheckIn) => (VisitStatus.CheckedIn, "VehicleCheckedIn"),
            (VisitStatus.CheckedIn, VisitAction.Assign) => (VisitStatus.Assigned, "DockAssigned"),
            (VisitStatus.Assigned, VisitAction.Start) => (VisitStatus.InProgress, "UnloadingStarted"),
            (VisitStatus.InProgress, VisitAction.Complete) => (VisitStatus.Completed, "UnloadingCompleted"),
            (VisitStatus.Scheduled or VisitStatus.CheckedIn, VisitAction.Cancel) => (VisitStatus.Cancelled, "AppointmentCancelled"),
            (VisitStatus.Scheduled, VisitAction.NoShow) when now >= end => (VisitStatus.NoShow, "AppointmentNoShow"),
            _ => throw new OperationRejected("invalid_transition", 409)
        };
}
