using System.Text;
using System.Text.Json;
using DockFlow.Worker;

static void Assert(bool condition, string behavior)
{
    if (!condition) throw new InvalidOperationException(behavior);
}

var envelope = new EventEnvelope(Guid.NewGuid(), Guid.NewGuid(), "DockUnavailable", Guid.NewGuid(), DateTimeOffset.UtcNow, 1);
var bytes = envelope.Encode();
using var document = JsonDocument.Parse(bytes);
Assert(document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(
    ["id", "tenantId", "kind", "aggregateId", "occurredAt", "version"]), "Wire field names must match the v1 contract");
Assert(EventEnvelope.Decode(bytes) == envelope, "Valid events must round-trip");
Assert(EventEnvelope.Decode((envelope with { Version = 2 }).Encode()) is null, "Unsupported versions must be poison");
Assert(EventEnvelope.Decode((envelope with { TenantId = Guid.Empty }).Encode()) is null, "Empty tenant must be poison");
Assert(EventEnvelope.Decode(Encoding.UTF8.GetBytes("{\"version\":1}")) is null, "Missing fields must be poison");
Assert(EventEnvelope.Decode(Encoding.UTF8.GetBytes("not JSON")) is null, "Malformed JSON must be poison");
Console.WriteLine("PASS: v1 wire contract and poison validation");

if (args is ["--integration"])
    await Integration.RunAsync(Environment.GetEnvironmentVariable("TestDatabase")!,
        int.Parse(Environment.GetEnvironmentVariable("TestBrokerPort")!));
