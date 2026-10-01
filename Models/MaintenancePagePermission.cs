namespace ApsMonitor.Models;

public class MaintenancePagePermission
{
    [System.ComponentModel.DataAnnotations.Key]
    public string PageKey { get; set; } = "";
    public bool Allowed { get; set; }
}
