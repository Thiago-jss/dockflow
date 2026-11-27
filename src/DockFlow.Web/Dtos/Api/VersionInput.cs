using System.ComponentModel.DataAnnotations;

namespace DockFlow.Web;

public sealed class VersionInput
{
    [Required] public long? Version { get; set; }
}
