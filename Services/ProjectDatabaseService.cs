using ApsMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Services;

public sealed record ProjectDatabase(string Id, string Name, string FileName, string ConnectionString,
    string ProjectFolder, string Revision)
{
    public string DatabasePath => new SqliteConnectionStringBuilder(ConnectionString).DataSource;
}

public sealed record ProjectRevision(string Version, IReadOnlyList<ProjectDatabase> Databases);
public sealed record ProjectDirectory(string Name, IReadOnlyList<ProjectRevision> Revisions);

public sealed class ProjectDatabaseService
{
    public const long MaximumDatabaseBytes = 100 * 1024 * 1024;
    public const string DefaultRootDirectory = @"C:\SEPSA\ApsWebMonitor";
    private readonly string _directory;
    private readonly string _selectionFile;
    private readonly ILogger<ProjectDatabaseService> _logger;
    private ProjectDatabase? _current;
    public ProjectDatabase? Current => Volatile.Read(ref _current);
    public string RootDirectory => _directory;
    public event Action? Changed;

    public ProjectDatabaseService(IHostEnvironment environment,
        ILogger<ProjectDatabaseService> logger, IConfiguration configuration)
    {
        _directory = Path.GetFullPath(configuration["Projects:RootDirectory"] ?? DefaultRootDirectory,
            environment.ContentRootPath);
        _selectionFile = Path.Combine(_directory, "active-project.txt");
        _logger = logger;
    }

    public IDbContextFactory<ApsDbContext> CaptureFactory() => new ProjectDbContextFactory(
        (Current ?? throw new InvalidOperationException("Selecciona un proyecto y una revisión para continuar.")).ConnectionString);

    public IReadOnlyList<ProjectDirectory> GetCatalog()
    {
        var projects = new List<ProjectDirectory>();
        if (Directory.Exists(_directory))
            foreach (var projectDirectory in Directory.EnumerateDirectories(_directory, "*", SafeEnumeration))
            {
                var projectName = Path.GetFileName(projectDirectory);
                if (string.Equals(projectName, ".uploads", StringComparison.OrdinalIgnoreCase)) continue;
                var versions = new List<ProjectRevision>();
                var revisions = Path.Combine(projectDirectory, "Revisions");
                if (Directory.Exists(revisions) && !IsLink(revisions))
                    foreach (var revisionDirectory in Directory.EnumerateDirectories(revisions, "*", SafeEnumeration))
                    {
                        var version = Path.GetFileName(revisionDirectory);
                        var databases = Directory.EnumerateFiles(revisionDirectory, "*", SafeEnumeration).Where(IsDatabase)
                            .Order(StringComparer.OrdinalIgnoreCase).Select(path => FromPath(path, projectName, version)).ToArray();
                        versions.Add(new(version, databases));
                    }
                projects.Add(new(projectName, versions
                    .OrderByDescending(x => Version.TryParse(x.Version, out var version) ? version : new Version(0, 0))
                    .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        return projects.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<ProjectDatabase> GetProjects() => GetCatalog().SelectMany(x => x.Revisions)
        .SelectMany(x => x.Databases).Select(ReadName).ToArray();

    private static readonly EnumerationOptions SafeEnumeration = new() { AttributesToSkip = FileAttributes.ReparsePoint };
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsDatabase(string path) => new[] { ".db", ".sqlite", ".sqlite3" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private ProjectDatabase FromPath(string path, string project, string revision) => new(
        Path.GetRelativePath(_directory, path).Replace('\\', '/'), project, Path.GetFileName(path),
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString(),
        project, revision);

    public ProjectDatabase Find(string id) => GetProjects().SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("El proyecto seleccionado ya no está disponible.");

    private ProjectDatabase ReadName(ProjectDatabase project)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(project.ConnectionString)
                { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ProjectName FROM ProjectBranding WHERE Id = 1";
            if (command.ExecuteScalar() is string name && !string.IsNullOrWhiteSpace(name))
                return project with { Name = name };
        }
        catch (SqliteException) { /* Legacy projects can have no saved identity yet. */ }
        return project;
    }

    public async Task<ProjectDatabase> ImportAsync(string projectName, string revision, string fileName,
        Stream source, CancellationToken token = default)
    {
        projectName = ValidateFolder(projectName);
        revision = ValidateFolder(revision);
        if (!Version.TryParse(revision, out _))
            throw new ArgumentException("La versión debe tener un formato como 0.1 o 1.2.3.");
        if (!IsDatabase(fileName))
            throw new ArgumentException("Selecciona una base de datos .db, .sqlite o .sqlite3.");
        var revisionDirectory = Path.Combine(_directory, projectName, "Revisions", revision);
        EnsureLocalDirectory(revisionDirectory);
        if (Directory.Exists(revisionDirectory) && Directory.EnumerateFiles(revisionDirectory).Any(IsDatabase))
            throw new ArgumentException("Esta revisión ya tiene una base de datos. Indica una versión nueva para conservar la existente.");
        var path = Path.Combine(revisionDirectory, "aps.db");
        var uploadDirectory = Path.Combine(_directory, ".uploads");
        EnsureLocalDirectory(uploadDirectory);
        Directory.CreateDirectory(uploadDirectory);
        var temporary = Path.Combine(uploadDirectory, $"{Guid.NewGuid():N}.upload");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    length += read;
                    if (length > MaximumDatabaseBytes) throw new ArgumentException("La base de datos supera 100 MiB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (length == 0) throw new ArgumentException("La base de datos está vacía.");
            }
            var project = FromPath(temporary, projectName, revision);
            await PrepareAsync(project, token);
            EnsureLocalDirectory(revisionDirectory);
            Directory.CreateDirectory(revisionDirectory);
            File.Move(temporary, path);
            var imported = FromPath(path, projectName, revision);
            return ReadName(imported);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(temporary + suffix)) File.Delete(temporary + suffix);
        }
    }

    private static string ValidateFolder(string value)
    {
        value = value.Trim();
        var device = value.Split('.')[0];
        if (value.Length is 0 or > 80 || value is "." or ".." || value.EndsWith('.') ||
            value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Indica un nombre de proyecto y una versión válidos, sin rutas ni caracteres especiales de archivo.");
        return value;
    }

    private void EnsureLocalDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && IsLink(current.FullName))
                throw new ArgumentException("La carpeta de proyectos no puede contener enlaces a otras rutas.");
            if (string.Equals(current.FullName, _directory, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new ArgumentException("La carpeta debe estar dentro de la ruta de proyectos.");
    }

    public static async Task PrepareAsync(ProjectDatabase project, CancellationToken token = default)
    {
        await using var db = new ProjectDbContextFactory(project.ConnectionString).CreateDbContext();
        await db.Database.OpenConnectionAsync(token);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA quick_check";
            if (!string.Equals(await command.ExecuteScalarAsync(token) as string, "ok", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("La base de datos está dañada.");
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Signals', 'Windows', 'Commands', 'Events')";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 4)
                throw new ArgumentException("El archivo no es una base de datos de APS Monitor.");
            // Imported standalone databases must not retain uncheckpointed WAL data.
            command.CommandText = "PRAGMA journal_mode=DELETE";
            await command.ExecuteScalarAsync(token);
        }
        ApsDatabaseInitializer.Initialize(db);
        // Read all mapped columns to reject incompatible databases before activation.
        await db.Signals.IgnoreQueryFilters().Take(1).ToListAsync(token);
        await db.Windows.Take(1).ToListAsync(token);
        await db.Commands.Take(1).ToListAsync(token);
        await db.Events.Take(1).ToListAsync(token);
        await db.TrdpConfigs.Take(1).ToListAsync(token);
        await db.ProjectBranding.Take(1).ToListAsync(token);
    }

    public void RestoreSelection()
    {
        _current = null;
        if (!File.Exists(_selectionFile)) return;
        try
        {
            var saved = Find(File.ReadAllText(_selectionFile).Trim());
            PrepareAsync(saved).GetAwaiter().GetResult();
            _current = saved;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo recuperar el proyecto. Selecciona un proyecto existente en la carpeta configurada.");
        }
    }
    internal void Activate(ProjectDatabase project)
    {
        Directory.CreateDirectory(_directory);
        var temporary = _selectionFile + ".tmp";
        File.WriteAllText(temporary, project.Id);
        File.Move(temporary, _selectionFile, overwrite: true);
        Volatile.Write(ref _current, project);
    }

    internal void NotifyChanged()
    {
        foreach (Action subscriber in Changed?.GetInvocationList() ?? [])
            try { subscriber(); } catch { /* Closed circuits cannot undo selection. */ }
    }
}
