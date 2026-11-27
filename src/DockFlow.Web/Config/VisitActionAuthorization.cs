using DockFlow.Application;

namespace DockFlow.Web;

internal static class VisitActionAuthorization
{
    public static string Roles(VisitAction action) => action switch
    {
        VisitAction.CheckIn => Identity.Gate,
        VisitAction.Assign or VisitAction.Start or VisitAction.Complete => Identity.Dock,
        VisitAction.Cancel or VisitAction.NoShow => Identity.Managers,
        _ => ""
    };
}
