using System.ComponentModel.DataAnnotations;
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
    public async Task<IReadOnlyList<Visit>> List(CancellationToken ct) => (await operations.BoardAsync(Current, ct)).Visits;

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Visit>> ById(Guid id, CancellationToken ct) =>
        await operations.FindAsync(Current, id, ct) is { } visit ? Ok(visit) : NotFound();

    [HttpPost]
    public async Task<ActionResult<Visit>> Create([FromBody] BookingInput input, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1 ||
            keys[0] is not { Length: > 0 and <= 128 } key || !key.All(c => c is >= '!' and <= '~' && c != ','))
            return Problem(statusCode: 400, title: "Informe um único Idempotency-Key válido.");
        try
        {
            var result = await operations.BookAsync(Current, input.Command(), key, ct);
            await updates.CommittedAsync(Current.TenantId);
            return CreatedAtAction(nameof(ById), new { id = result.Id }, result);
        }
        catch (OperationRejected e) { return Rejected(e); }
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<Visit>> Cancel(Guid id, [FromBody] VersionInput input, CancellationToken ct)
    {
        if (await operations.FindAsync(Current, id, ct) is null) return NotFound();
        try
        {
            var result = await operations.TransitionAsync(Current, id, VisitAction.Cancel, input.Version!.Value, ct);
            await updates.CommittedAsync(Current.TenantId);
            return Ok(result);
        }
        catch (OperationRejected e) { return Rejected(e); }
    }

    private ObjectResult Rejected(OperationRejected e) => Problem(statusCode: e.Status,
        title: "Operação não realizada.", extensions: new Dictionary<string, object?> { ["code"] = e.Code });
}

public sealed class BookingInput
{
    [Required, StringLength(32)] public string? Vehicle { get; set; }
    [Required, EnumDataType(typeof(CargoKind))] public CargoKind? Cargo { get; set; }
    [Required, Range(1, int.MaxValue)] public int? WeightKg { get; set; }
    [Required] public DateTimeOffset? Start { get; set; }
    [Required] public DateTimeOffset? End { get; set; }
    public Guid? DockId { get; set; }
    public Book Command() => new(Vehicle!, Cargo!.Value, WeightKg!.Value, Start!.Value, End!.Value, DockId);
}

public sealed class VersionInput
{
    [Required] public long? Version { get; set; }
}
