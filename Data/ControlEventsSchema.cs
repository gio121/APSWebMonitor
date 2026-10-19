using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Data;

public static class ControlEventsSchema
{
    public static void EnsureImportedConfiguration(ApsDbContext context) => context.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS ImportedControlEventConfigurations (NodeNumber INTEGER NOT NULL PRIMARY KEY, ConfigurationJson TEXT NOT NULL)");
}
