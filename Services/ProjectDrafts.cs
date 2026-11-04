using System.Security.Cryptography;
using System.Text.Json;
using ApsMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Services;

public sealed record DraftStatus(bool HasChanges, bool SourceChanged, string NextRevision);
public sealed record DraftManifest(string SourceId, string SourceHash, string BaselineHash,
    DateTime CreatedUtc, string? PublishedId = null, string? Description = null, string? Author = null, bool Discarded = false);

public sealed partial class ProjectDatabaseService
{
    // One lock covers context lifetimes (including transactions), switching and snapshots.
    internal SemaphoreSlim DraftGate { get; } = new(1, 1);
    internal static FileStream AcquireFileLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("Another process is working on this draft. Wait for it to finish and try again."), ex); }
    }
    public event Action? DraftChanged;
    public IDbContextFactory<ApsDbContext> CaptureEventFactory()
    {
        var project = Current ?? throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("No active project."));
        return new ProjectDbContextFactory(RuntimeConnection(project));
    }
    private string RuntimeConnection(ProjectDatabase project) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(_directory, project.ProjectFolder, ".Runtime", "events.db"), Pooling = false
    }.ToString();

    private async Task EnsureRuntimeAsync(ProjectDatabase source)
    {
        var folder = Path.Combine(_directory, source.ProjectFolder, ".Runtime");
        EnsureLocalDirectory(folder);
        Directory.CreateDirectory(folder);
        var connectionString = RuntimeConnection(source);
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (File.Exists(path)) return;
        var temporary = path + $".{Guid.NewGuid():N}.initializing";
        // Failed creation can be retried without marking a partial log database as initialized.
        using (var output = new ProjectDbContextFactory($"Data Source={temporary};Pooling=False").CreateDbContext())
        {
            await output.Database.EnsureCreatedAsync();
            using var input = new ProjectDbContextFactory(new SqliteConnectionStringBuilder(source.ConnectionString)
                { Mode = SqliteOpenMode.ReadOnly }.ToString()).CreateDbContext();
            output.Events.AddRange(await input.Events.AsNoTracking().ToListAsync());
            await output.SaveChangesAsync();
            await output.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            await output.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE");
        }
        File.Move(temporary, path);
    }
    internal void NotifyDraftChanged()
    {
        foreach (Action subscriber in DraftChanged?.GetInvocationList() ?? [])
            try { subscriber(); } catch { }
    }

    internal static string ManifestPath(ProjectDatabase draft) => Path.Combine(Path.GetDirectoryName(draft.DatabasePath)!, "estado.json");
    internal static DraftManifest Manifest(ProjectDatabase draft) =>
        JsonSerializer.Deserialize<DraftManifest>(File.ReadAllText(ManifestPath(draft)))
        ?? throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Text("The draft state is invalid. It is preserved for recovery."));
    private static void WriteManifest(ProjectDatabase draft, DraftManifest manifest)
    {
        var path = ManifestPath(draft);
        WriteAtomicText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteAtomicText(string path, string contents)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    // Windows scanners can briefly hold a newly replaced metadata file.
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task<ProjectDatabase> OpenDraftAsync(ProjectDatabase source)
    {
        await DraftGate.WaitAsync();
        try
        {
            var working = Path.Combine(_directory, source.ProjectFolder, ".Working");
            EnsureLocalDirectory(working);
            Directory.CreateDirectory(working);
            using var catalogLock = AcquireFileLock(Path.Combine(working, ".catalog.lock"));
            await EnsureRuntimeAsync(source);
            foreach (var folder in Directory.EnumerateDirectories(working, "*", SafeEnumeration))
            {
                var candidate = AsDraft(source, Path.Combine(folder, "aps.db"));
                if (!File.Exists(ManifestPath(candidate))) continue; // Incomplete creation is never adopted.
                using var candidateLock = AcquireFileLock(candidate.DatabasePath + ".edit.lock");
                var manifest = RecoverPublication(candidate);
                if (manifest.SourceId != source.Id || manifest.PublishedId is not null || manifest.Discarded) continue;
                if (!File.Exists(candidate.DatabasePath)) throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The draft database is missing. Check the .Working folder before continuing."));
                return candidate;
            }
            var path = Path.Combine(working, Guid.NewGuid().ToString("N"), "aps.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var draft = AsDraft(source, path);
            var hash = ConfigurationHash(source.ConnectionString);
            Backup(source.ConnectionString, path);
            await PrepareAsync(draft); // Schema upgrades only touch the working copy.
            WriteManifest(draft, new(source.Id, hash, ConfigurationHash(draft.ConnectionString), DateTime.UtcNow));
            return draft;
        }
        finally { DraftGate.Release(); }
    }

    private static ProjectDatabase AsDraft(ProjectDatabase source, string path) => source with
    {
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()
    };

    private DraftManifest RecoverPublication(ProjectDatabase draft)
    {
        var manifest = Manifest(draft);
        var folder = Path.GetDirectoryName(draft.DatabasePath)!;
        var pending = Path.Combine(folder, "publication.json");
        if (File.Exists(pending) && manifest.PublishedId is null)
        {
            var publication = JsonSerializer.Deserialize<DraftManifest>(File.ReadAllText(pending))!;
            // Resolve only through the catalog, never trust paths in recovery metadata.
            var published = GetProjects().SingleOrDefault(x => x.Id == publication.PublishedId);
            var receipt = published is null ? "" : Path.Combine(Path.GetDirectoryName(published.DatabasePath)!, "revision.json");
            if (File.Exists(receipt))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(receipt));
                if (json.RootElement.GetProperty("Draft").GetString() == Path.GetFileName(folder))
                {
                    manifest = publication;
                    WriteManifest(draft, manifest);
                }
            }
            File.Delete(pending);
        }
        return manifest;
    }

    internal static void Backup(string connectionString, string destination)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var input = new SqliteConnection(builder.ToString());
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        input.Open(); output.Open();
        input.BackupDatabase(output);
    }

    // Logical comparison ignores SQLite layout, runtime data and account permissions.
    internal static string ConfigurationHash(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(object? value) => hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(value));
        foreach (var table in new[] { "Signals", "Windows", "Commands", "TrdpConfigs", "ProjectBranding", "ImportedControlEventConfigurations" })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            var columns = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var column = reader.GetString(1);
                    if (column != "UltimaModificacion" && !(table == "Signals" && column == "ValorActual")) columns.Add(column);
                }
            Append(table); Append(columns);
            if (columns.Count == 0) continue;
            var projection = string.Join(',', columns.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));
            command.CommandText = $"SELECT {projection} FROM \"{table}\" ORDER BY {projection}";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var values = new object[rows.FieldCount]; rows.GetValues(values);
                Append(values.Select(x => x is DBNull ? null : x).ToArray());
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<DraftStatus> GetDraftStatusAsync()
    {
        await DraftGate.WaitAsync();
        try
        {
            if (Current is not { } draft) return new(false, false, "0.1");
            var manifest = Manifest(draft);
            bool sourceChanged;
            try { sourceChanged = ConfigurationHash(Find(draft.Id).ConnectionString) != manifest.SourceHash; }
            catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException) { sourceChanged = true; }
            var versions = (GetCatalog().FirstOrDefault(x => x.Name == draft.ProjectFolder)?.Revisions ?? [])
                .Select(x => Version.TryParse(x.Version, out var v) ? v : new Version(0, 0)).ToArray();
            var latest = versions.Max() ?? new Version(0, 0);
            return new(ConfigurationHash(draft.ConnectionString) != manifest.BaselineHash, sourceChanged,
                $"{latest.Major}.{latest.Minor + 1}");
        }
        finally { DraftGate.Release(); }
    }

    internal async Task<ProjectDatabase> PublishDraftAsync(string revision, string description, string author)
    {
        revision = ValidateFolder(revision);
        if (!Version.TryParse(revision, out var version)) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Enter a valid revision, for example 0.2."));
        await DraftGate.WaitAsync();
        try
        {
            var draft = Current ?? throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("There is no active project."));
            using var draftLock = AcquireFileLock(draft.DatabasePath + ".edit.lock");
            var manifest = Manifest(draft);
            if (manifest.PublishedId is not null) return Find(manifest.PublishedId);
            if (manifest.Discarded) throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("This draft has been discarded. Reload the revision."));
            if (ConfigurationHash(Find(draft.Id).ConnectionString) != manifest.SourceHash)
                throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("The source revision has changed outside the application. The draft is preserved; restore the source before publishing."));
            if (ConfigurationHash(draft.ConnectionString) == manifest.BaselineHash)
                throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("There are no configuration changes to publish."));
            if (Version.TryParse(draft.Revision, out var original) && version <= original)
                throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The new revision must be later than the source revision."));
            var destination = Path.Combine(_directory, draft.ProjectFolder, "Revisions", revision);
            EnsureLocalDirectory(destination);
            if (Directory.Exists(destination)) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("That revision already exists. Choose another version; existing revisions are never overwritten."));
            var staging = Path.Combine(_directory, draft.ProjectFolder, ".Working", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var staged = AsDraft(draft, Path.Combine(staging, "aps.db"));
            Backup(draft.ConnectionString, staged.DatabasePath);
            await PrepareAsync(staged);
            var published = FromPath(Path.Combine(destination, "aps.db"), draft.ProjectFolder, revision);
            var publication = manifest with { PublishedId = published.Id, Description = description, Author = author };
            File.WriteAllText(Path.Combine(staging, "revision.json"), JsonSerializer.Serialize(new
            {
                Parent = draft.Id, PublishedUtc = DateTime.UtcNow, Author = author, Description = description,
                Draft = Path.GetFileName(Path.GetDirectoryName(draft.DatabasePath))
            }));
            // Write-ahead publication record allows recovery if the process stops after the rename.
            WriteAtomicText(Path.Combine(Path.GetDirectoryName(draft.DatabasePath)!, "publication.json"), JsonSerializer.Serialize(publication));
            try { Directory.Move(staging, destination); } // Atomic; refuses an existing directory, including races.
            catch
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(draft.DatabasePath)!, "publication.json"));
                throw;
            }
            WriteManifest(draft, publication); // Seals old contexts before they can write again.
            return published;
        }
        finally { DraftGate.Release(); }
    }

    internal static bool IsSealed(ProjectDatabase draft)
    {
        var manifest = Manifest(draft);
        if (manifest.PublishedId is not null || manifest.Discarded) return true;
        var pending = Path.Combine(Path.GetDirectoryName(draft.DatabasePath)!, "publication.json");
        if (!File.Exists(pending)) return false;
        // A pending publication is deliberately frozen until recovery resolves it.
        throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("A pending publication needs recovery. Reload the project."));
    }

    internal async Task<ProjectDatabase> DiscardDraftAsync()
    {
        var draft = Current ?? throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("No active project."));
        await DraftGate.WaitAsync();
        try
        {
            using var draftLock = AcquireFileLock(draft.DatabasePath + ".edit.lock");
            WriteManifest(draft, Manifest(draft) with { Discarded = true });
        }
        finally { DraftGate.Release(); }
        return await OpenDraftAsync(Find(draft.Id));
    }
}
