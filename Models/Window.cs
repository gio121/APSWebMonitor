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

    // ── Subcomandos Start/Stop/Reset ───────────────────────────────────────────
    /// <summary>FrameType para comandos start/stop (por defecto 0x7A).</summary>
    public string CmdFrameType { get; set; } = "0x7A";
    /// <summary>SubCmd para parar el converter (ej: Inversor=0x0005, LVPS=0x0003, Chopper=0x00FE).</summary>
    public string StopSubCmd { get; set; } = "";
    /// <summary>SubCmd para arrancar el converter (ej: Inversor=0x0006, LVPS=0x0004, Chopper=0x00FF).</summary>
    public string StartSubCmd { get; set; } = "";
    /// <summary>FrameType para reset (por defecto 0x2A).</summary>
    public string ResetFrameType { get; set; } = "0x2A";
    /// <summary>SubCmd para reset de fallos (ej: Chopper=0x0000, LVPS=0x0001, Inversor=0x0004).</summary>
    public string ResetSubCmd { get; set; } = "";
}

