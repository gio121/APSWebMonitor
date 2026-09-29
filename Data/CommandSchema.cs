using System.Data;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Data;

public static class CommandSchema
{
    public static void EnsureControlCommands(ApsDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(Commands)";
            var columns = new HashSet<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(1));
            foreach (var (name, type) in new[] { ("FrameType", "INTEGER"), ("Subcommand", "INTEGER"), ("SignalId", "INTEGER"), ("VariableValue", "REAL") })
            {
                if (columns.Contains(name)) continue;
                command.CommandText = $"ALTER TABLE Commands ADD COLUMN {name} {type} NULL";
                command.ExecuteNonQuery();
            }
        }
        finally { if (openedHere) connection.Close(); }
    }
}
