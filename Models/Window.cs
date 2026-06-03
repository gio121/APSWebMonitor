using System.Text.Json;

namespace ApsMonitor.Models;

public class Window
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string Categoria { get; set; } = string.Empty;
    
    // UI badges
    public string Tipo { get; set; } = "Normal"; // e.g., Normal, Sinóptico
    public bool IsActive { get; set; } = true;

    // Synoptic Content (JSON serialized elements)
    public string ContentJson { get; set; } = "[]";

    // Access control
    public bool PermitirMantenimiento { get; set; } = false;
    public string AllowedRolesJson { get; set; } = "[]";

    // Failure configuration (JSON serialized List<FailureCategoryConfig>)
    public string FailuresConfigJson { get; set; } = "[]";

    public List<string> GetAllowedRoles()
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(AllowedRolesJson) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public bool IsRoleAllowed(string role)
    {
        return GetAllowedRoles().Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    public void SetRoleAllowed(string role, bool allowed)
    {
        var roles = GetAllowedRoles();
        roles.RemoveAll(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

        if (allowed)
            roles.Add(role);

        AllowedRolesJson = JsonSerializer.Serialize(roles);
    }
}

public class FailureCategoryConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public int? StatusSignalId { get; set; }
    public int? CountSignalId { get; set; }
    public List<int> SignalIds { get; set; } = new();
}

