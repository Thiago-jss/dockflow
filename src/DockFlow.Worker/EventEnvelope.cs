using System.Text.Json;

namespace DockFlow.Worker;

internal sealed record EventEnvelope(Guid Id, Guid TenantId, string Kind, Guid AggregateId,
    DateTimeOffset OccurredAt, int Version)
{
    internal const int CurrentVersion = 1;
    internal const string DockUnavailable = "DockUnavailable";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true
    };

    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    internal static EventEnvelope? Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > 64 * 1024) return null;
        try
        {
            var message = JsonSerializer.Deserialize<EventEnvelope>(utf8, Json);
            return message is { Version: CurrentVersion } && message.Id != Guid.Empty &&
                message.TenantId != Guid.Empty && message.AggregateId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(message.Kind) && message.OccurredAt != default ? message : null;
        }
        catch (JsonException) { return null; }
    }
}
