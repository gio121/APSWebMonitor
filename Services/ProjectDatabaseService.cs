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

public sealed partial class ProjectDatabaseService
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

    public IDbContextFactory<ApsDbContext> CaptureFactory() => new DraftDbContextFactory(
        Current ?? throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("Select a project and revision to continue.")), this);

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
        ?? throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The selected project is no longer available."));

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
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The version must use a format such as 0.1 or 1.2.3."));
        if (!IsDatabase(fileName))
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Select a .db, .sqlite or .sqlite3 database."));
        var revisionDirectory = Path.Combine(_directory, projectName, "Revisions", revision);
        EnsureLocalDirectory(revisionDirectory);
        if (Directory.Exists(revisionDirectory) && Directory.EnumerateFiles(revisionDirectory).Any(IsDatabase))
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("This revision already has a database. Enter a new version to preserve the existing one."));
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
                    if (length > MaximumDatabaseBytes) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The database exceeds 100 MiB."));
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (length == 0) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The database is empty."));
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
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Enter a valid project name and version without paths or special file characters."));
        return value;
    }

    private void EnsureLocalDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && IsLink(current.FullName))
                throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The projects folder cannot contain links to other paths."));
            if (string.Equals(current.FullName, _directory, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The folder must be inside the projects directory."));
    }

    public static async Task PrepareAsync(ProjectDatabase project, CancellationToken token = default)
    {
        await using var db = new ProjectDbContextFactory(project.ConnectionString).CreateDbContext();
        await db.Database.OpenConnectionAsync(token);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA quick_check";
            if (!string.Equals(await command.ExecuteScalarAsync(token) as string, "ok", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The database is corrupt."));
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Signals', 'Windows', 'Commands', 'Events')";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 4)
                throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The file is not an APS Monitor database."));
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
            var draftPointer = _selectionFile + ".draft";
            if (File.Exists(draftPointer))
            {
                var folder = File.ReadAllText(draftPointer).Trim();
                if (Guid.TryParseExact(folder, "N", out _))
                {
                    var draft = AsDraft(saved, Path.Combine(_directory, saved.ProjectFolder, ".Working", folder, "aps.db"));
                    EnsureLocalDirectory(Path.GetDirectoryName(draft.DatabasePath)!);
                    if (File.Exists(ManifestPath(draft)))
                    {
                        using var recoveryLock = AcquireFileLock(draft.DatabasePath + ".edit.lock");
                        var manifest = RecoverPublication(draft);
                        if (manifest.SourceId == saved.Id && manifest.PublishedId is { } published) saved = Find(published);
                    }
                }
            }
            _current = OpenDraftAsync(saved).GetAwaiter().GetResult();
            Activate(_current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, ApsMonitor.Resources.MonitorResources.Text("Unable to restore the project. Select an existing project in the configured folder."));
        }
    }
    internal void Activate(ProjectDatabase project)
    {
        Directory.CreateDirectory(_directory);
        WriteAtomicText(_selectionFile, project.Id);
        WriteAtomicText(_selectionFile + ".draft", Path.GetFileName(Path.GetDirectoryName(project.DatabasePath))!);
        Volatile.Write(ref _current, project);
    }

    internal void NotifyChanged()
    {
        foreach (Action subscriber in Changed?.GetInvocationList() ?? [])
            try { subscriber(); } catch { /* Closed circuits cannot undo selection. */ }
    }
}
