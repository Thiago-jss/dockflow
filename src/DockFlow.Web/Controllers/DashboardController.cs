using DockFlow.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace DockFlow.Web;

[Authorize(Policy = Identity.Staff)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DashboardController(IDockOperations operations, BoardUpdates updates, BoardPageQuery query) : Controller
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
    public async Task<IActionResult> Index(string? day, CancellationToken ct) => View(await query.ReadAsync(Current, day, ct));

    [HttpGet("/painel/estado")]
    public async Task<IActionResult> Snapshot(string? day, CancellationToken ct) => PartialView("_Board", await query.ReadAsync(Current, day, ct));

    [HttpPost("/painel/agendar"), Authorize(Roles = Identity.Managers)]
    public Task<IActionResult> Schedule([FromForm] BookingForm input, CancellationToken ct)
    {
        if (!ModelState.IsValid || !input.TryToCommand(out var command, out var key))
            return Task.FromResult(InvalidForm());
        return Commit(() => operations.BookAsync(Current, command, key, ct));
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
        if (!Identity.Can(User, VisitActionAuthorization.Roles(action.Value))) return Task.FromResult<IActionResult>(Forbid(Identity.Browser));
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
