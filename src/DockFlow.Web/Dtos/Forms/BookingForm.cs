using DockFlow.Application;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace DockFlow.Web;

public sealed class BookingForm
{
    [Required, StringLength(32)] public string? Vehicle { get; set; }
    [Required, EnumDataType(typeof(CargoKind))] public CargoKind? Cargo { get; set; }
    [Required, Range(1, int.MaxValue)] public int? WeightKg { get; set; }
    public string? StartUtc { get; set; }
    public string? EndUtc { get; set; }
    public Guid? DockId { get; set; }
    public Guid? RequestKey { get; set; }

    internal bool TryToCommand(out Book command, out string key)
    {
        command = null!;
        key = string.Empty;
        if (!DateTimeOffset.TryParseExact(StartUtc, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var start) ||
            !DateTimeOffset.TryParseExact(EndUtc, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var end) || RequestKey is null || RequestKey == Guid.Empty)
            return false;
        command = new Book(Vehicle!, Cargo!.Value, WeightKg!.Value, start, end, DockId);
        key = RequestKey.Value.ToString("N");
        return true;
    }
}
