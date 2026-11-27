using DockFlow.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.ComponentModel.DataAnnotations;

namespace DockFlow.Web;

[Authorize(Policy = Identity.Staff)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DashboardController(IDockOperations operations, BoardUpdates updates, BoardAnalytics analytics) : Controller
{
    private Actor Current => Identity.ActorFor(User)!;

    [AllowAnonymous, HttpGet("/entrar")]
    public IActionResult SignInAtProvider(string? returnUrl) => Challenge(
        new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/" }, Identity.Oidc);

    [HttpPost("/sair")]
    [AllowAnonymous] // The action verifies the cookie; logout remains available to users missing staff roles.
    public async Task<IActionResult> SignOutOfApp()
    {
        if (!(await HttpContext.AuthenticateAsync(Identity.Browser)).Succeeded) return RedirectToAction(nameof(Index));
        await HttpContext.SignOutAsync(Identity.Browser);
        return View("SignedOut");
    }

    [HttpGet("/")]
    public async Task<IActionResult> Index(string? day, CancellationToken ct) => View(await ReadAsync(day, ct));

    [HttpGet("/painel/estado")]
    public async Task<IActionResult> Snapshot(string? day, CancellationToken ct) => PartialView("_Board", await ReadAsync(day, ct));

    private async Task<BoardPage> ReadAsync(string? day, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var board = await operations.BoardAsync(Current, ct);
        var parsed = DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return new(board, now, await analytics.CompletedTodayAsync(Current.TenantId, now, ct),
            parsed && Math.Abs(date.DayNumber - today.DayNumber) <= 365 ? date : today);
    }

    [HttpPost("/painel/agendar"), Authorize(Roles = Identity.Managers)]
    public Task<IActionResult> Schedule([FromForm] BookingForm input, CancellationToken ct)
    {
        if (!ModelState.IsValid ||
            !DateTimeOffset.TryParseExact(input.StartUtc, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var start) ||
            !DateTimeOffset.TryParseExact(input.EndUtc, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var end) || input.RequestKey is null || input.RequestKey == Guid.Empty)
            return Task.FromResult(InvalidForm());
        return Commit(async () => { await operations.BookAsync(Current,
            new Book(input.Vehicle!, input.Cargo!.Value, input.WeightKg!.Value, start, end, input.DockId),
            input.RequestKey.Value.ToString("N"), ct); });
    }

    [HttpPost("/painel/docas/{id:guid}/bloquear"), Authorize(Roles = Identity.Managers)]
    public Task<IActionResult> Block([FromRoute] Guid id, [FromForm] string? reason, CancellationToken ct) =>
        !ModelState.IsValid || string.IsNullOrWhiteSpace(reason) || reason.Length > 200 ? Task.FromResult(InvalidForm()) :
        Commit(() => operations.BlockAsync(Current, id, reason, ct));

    [HttpPost("/painel/visitas/{id:guid}/realocar"), Authorize(Roles = Identity.Managers)]
    public Task<IActionResult> Reassign([FromRoute] Guid id, [FromForm] long? version, CancellationToken ct) =>
        !ModelState.IsValid || version is null ? Task.FromResult(InvalidForm()) :
        Commit(async () => { await operations.ReassignAsync(Current, id, version.Value, ct); });

    [HttpPost("/painel/visitas/{id:guid}/transicao")]
    public Task<IActionResult> Move([FromRoute] Guid id, [FromForm] VisitAction? action, [FromForm] long? version, CancellationToken ct)
    {
        if (!ModelState.IsValid || action is null || version is null || !Enum.IsDefined(action.Value)) return Task.FromResult(InvalidForm());
        if (!Identity.Can(User, BoardPage.Roles(action.Value))) return Task.FromResult<IActionResult>(Forbid(Identity.Browser));
        return Commit(async () => { await operations.TransitionAsync(Current, id, action.Value, version.Value, ct); });
    }

    private IActionResult InvalidForm()
    {
        TempData["error"] = "Confira os dados e tente novamente.";
        return BackToBoard();
    }

    private async Task<IActionResult> Commit(Func<Task> operation)
    {
        try
        {
            await operation();
            await updates.CommittedAsync(Current.TenantId);
            TempData["success"] = "Alteração registrada.";
        }
        catch (OperationRejected e)
        {
            TempData["error"] = e.Status == 409
                ? "A operação conflita com o estado atual. Confira a disponibilidade e tente novamente."
                : "A operação não pôde ser realizada. Confira os dados e o estado da visita.";
        }
        return BackToBoard();
    }

    private IActionResult BackToBoard() => RedirectToAction(nameof(Index), new
    {
        day = DateOnly.TryParseExact(Request.Form["returnDay"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null
    });
}

public sealed record BoardPage(Board Board, DateTimeOffset Now, long CompletedToday, DateOnly Day)
{
    public DateTimeOffset DayStart => new(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public string DayKey => Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool SpansDay(Visit visit) => visit.Start < DayStart.AddDays(1) && visit.End > DayStart;
    public int Minute(DateTimeOffset instant) => (int)Math.Clamp((instant - DayStart).TotalMinutes, 0, 1440);
    public static bool PhysicallyPresent(Visit visit) => visit.Status is VisitStatus.Assigned or VisitStatus.InProgress;
    public static string IncidentLabel(string kind) => kind switch { "Maintenance" => "Manutenção", "Overrun" => "Janela excedida", _ => kind };
    public static string Roles(VisitAction action) => action switch
    {
        VisitAction.CheckIn => Identity.Gate,
        VisitAction.Assign or VisitAction.Start or VisitAction.Complete => Identity.Dock,
        VisitAction.Cancel or VisitAction.NoShow => Identity.Managers,
        _ => ""
    };
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

public sealed class BookingForm
{
    [Required, StringLength(32)] public string? Vehicle { get; set; }
    [Required, EnumDataType(typeof(CargoKind))] public CargoKind? Cargo { get; set; }
    [Required, Range(1, int.MaxValue)] public int? WeightKg { get; set; }
    public string? StartUtc { get; set; }
    public string? EndUtc { get; set; }
    public Guid? DockId { get; set; }
    public Guid? RequestKey { get; set; }
}
