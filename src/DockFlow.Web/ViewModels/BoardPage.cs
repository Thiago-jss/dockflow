using DockFlow.Application;
using System.Globalization;

namespace DockFlow.Web;

public sealed record BoardPage(Board Board, DateTimeOffset Now, long CompletedToday, DateOnly Day)
{
    public DateTimeOffset DayStart => new(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public string DayKey => Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool SpansDay(Visit visit) => visit.Start < DayStart.AddDays(1) && visit.End > DayStart;
    public int Minute(DateTimeOffset instant) => (int)Math.Clamp((instant - DayStart).TotalMinutes, 0, 1440);
    public static bool PhysicallyPresent(Visit visit) => visit.Status is VisitStatus.Assigned or VisitStatus.InProgress;
    public static string IncidentLabel(string kind) => kind switch { "Maintenance" => "Manutenção", "Overrun" => "Janela excedida", _ => kind };
    public static bool Active(Visit v) => v.Status is VisitStatus.Scheduled or VisitStatus.CheckedIn or VisitStatus.Assigned or VisitStatus.InProgress;
    public string DockState(DockView dock) => Board.Visits.Any(v => v.DockId == dock.Id && PhysicallyPresent(v)) ? "Ocupada" :
        !dock.Operational ? "Bloqueada" : Board.Visits.Any(v => v.DockId == dock.Id && Active(v) && v.Start <= Now && Now < v.End)
            ? "Janela reservada" : "Livre agora";
    public static string Stamp(DateTimeOffset date) => date.ToUniversalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public static string CargoLabel(CargoKind cargo) => cargo == CargoKind.Refrigerated ? "Refrigerada" : "Geral";
    public static string StatusLabel(VisitStatus status) => status switch
    {
        VisitStatus.Scheduled => "Agendada", VisitStatus.CheckedIn => "Na fila", VisitStatus.Assigned => "Alocada",
        VisitStatus.InProgress => "Em operação", VisitStatus.Completed => "Concluída", VisitStatus.Cancelled => "Cancelada",
        VisitStatus.NoShow => "Ausente", _ => "Desconhecido"
    };
    public static IEnumerable<(VisitAction Action, string Label)> Actions(VisitStatus status) => status switch
    {
        VisitStatus.Scheduled => [(VisitAction.CheckIn,"Registrar chegada"), (VisitAction.Cancel,"Cancelar"), (VisitAction.NoShow,"Marcar ausência")],
        VisitStatus.CheckedIn => [(VisitAction.Assign,"Alocar"), (VisitAction.Cancel,"Cancelar")],
        VisitStatus.Assigned => [(VisitAction.Start,"Iniciar")],
        VisitStatus.InProgress => [(VisitAction.Complete,"Concluir")],
        _ => []
    };
}
