namespace ApsMonitor.Models;

public class RoleDefinition
{
    [System.ComponentModel.DataAnnotations.Key]
    public string Name { get; set; } = "";
}

public class RolePagePermission
{
    public string RoleName { get; set; } = "";
    public string PageKey { get; set; } = "";
    public bool Allowed { get; set; }
}
