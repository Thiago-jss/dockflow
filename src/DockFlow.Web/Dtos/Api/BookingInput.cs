using System.ComponentModel.DataAnnotations;
using DockFlow.Application;

namespace DockFlow.Web;

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
