using DockFlow.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DockFlow.Web;

[ApiController]
[Authorize(Policy = Identity.Carrier)]
[IgnoreAntiforgeryToken] // This endpoint authenticates exclusively via Authorization: Bearer.
[EnableRateLimiting("carrier-api")]
[RequestSizeLimit(16_384)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/appointments")]
public sealed class AppointmentsController(IDockOperations operations, BoardUpdates updates) : ControllerBase
{
    private Actor Current => Identity.ActorFor(User, true)!;

    [HttpGet]
    public async Task<IReadOnlyList<AppointmentResponse>> List(CancellationToken ct) =>
        (await operations.BoardAsync(Current, ct)).Visits.Select(AppointmentResponse.From).ToArray();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AppointmentResponse>> ById(Guid id, CancellationToken ct) =>
        await operations.FindAsync(Current, id, ct) is { } visit ? Ok(AppointmentResponse.From(visit)) : NotFound();

    [HttpPost]
    public async Task<ActionResult<AppointmentResponse>> Create([FromBody] BookingInput input, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1 ||
            keys[0] is not { Length: > 0 and <= 128 } key || !key.All(c => c is >= '!' and <= '~' && c != ','))
            return Problem(statusCode: 400, title: "Informe um único Idempotency-Key válido.");
        try
        {
            var result = await operations.BookAsync(Current, input.Command(), key, ct);
            await updates.CommittedAsync(Current.TenantId);
            return CreatedAtAction(nameof(ById), new { id = result.Id }, AppointmentResponse.From(result));
        }
        catch (OperationRejected e) { return Rejected(e); }
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<AppointmentResponse>> Cancel(Guid id, [FromBody] VersionInput input, CancellationToken ct)
    {
        if (await operations.FindAsync(Current, id, ct) is null) return NotFound();
        try
        {
            var result = await operations.TransitionAsync(Current, id, VisitAction.Cancel, input.Version!.Value, ct);
            await updates.CommittedAsync(Current.TenantId);
            return Ok(AppointmentResponse.From(result));
        }
        catch (OperationRejected e) { return Rejected(e); }
    }

    private ObjectResult Rejected(OperationRejected e) => Problem(statusCode: e.Status,
        title: "Operação não realizada.", extensions: new Dictionary<string, object?> { ["code"] = e.Code });
}
