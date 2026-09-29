using System.Data;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Data;

public static class SignalSchema
{
    // Existing installations use EnsureCreated plus incremental schema updates.
    public static void EnsurePersistentBindings(ApsDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(Signals)";
            bool exists = false;
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    exists |= reader.GetString(1) == "IsDeleted";
            if (!exists)
            {
                command.CommandText = "ALTER TABLE Signals ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0";
                command.ExecuteNonQuery();
            }
        }
        finally { if (openedHere) connection.Close(); }
    }
}
