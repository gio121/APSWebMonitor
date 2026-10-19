using System.ComponentModel.DataAnnotations;

namespace ApsMonitor.Models;

// Binary record layout and enumeration metadata retained by the signal import.
// Names, types, units, scales and bit texts are resolved from the current Signals.
public sealed class ImportedControlEventConfiguration
{
    [Key]
    public int NodeNumber { get; set; }
    public string ConfigurationJson { get; set; } = "";
}
