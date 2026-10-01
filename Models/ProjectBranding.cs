namespace ApsMonitor.Models;

public sealed class ProjectBranding
{
    public int Id { get; set; } = 1;
    public string ProjectName { get; set; } = "APS Monitor";
    public string? LogoDataUrl { get; set; }
}
