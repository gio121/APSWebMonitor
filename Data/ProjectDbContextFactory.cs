using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ApsMonitor.Services;

namespace ApsMonitor.Data;

public sealed class ProjectDbContextFactory(string connectionString) : IDbContextFactory<ApsDbContext>
{
    public ApsDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApsDbContext>()
        .UseSqlite(connectionString).Options);
}

public sealed class ApplicationDbContextFactory : IDbContextFactory<ApsDbContext>
{
    public string ConnectionString { get; }

    public ApplicationDbContextFactory(IConfiguration configuration, IHostEnvironment environment)
    {
        var connection = new SqliteConnectionStringBuilder(
            configuration.GetConnectionString("DefaultConnection") ?? "Data Source=aps.db");
        connection.DataSource = Path.GetFullPath(connection.DataSource, environment.ContentRootPath);
        ConnectionString = connection.ToString();
    }

    public ApsDbContext CreateDbContext() => new ProjectDbContextFactory(ConnectionString).CreateDbContext();
}

// This factory follows the shared monitor. Page services capture a fixed factory
// so an event still finishing in an old view can only save to its original project.
public sealed class ActiveProjectDbContextFactory(ProjectDatabaseService projects) : IDbContextFactory<ApsDbContext>
{
    public ApsDbContext CreateDbContext() => projects.CaptureFactory().CreateDbContext();
}
