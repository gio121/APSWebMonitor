using System.Text.Json;
using ApsMonitor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ApsMonitor.Data;

// Each page scope retains the values it saw, even when dialogs edit detached copies.
public sealed class DraftDbContextFactory(ProjectDatabase draft, ProjectDatabaseService projects) : IDbContextFactory<ApsDbContext>
{
    private readonly ProjectDatabase _draft = draft;
    private readonly ProjectDatabaseService _projects = projects;
    private readonly Dictionary<string, string> _seen = [];
    public ApsDbContext CreateDbContext()
    {
        projects.DraftGate.Wait();
        return CreateLocked();
    }
    public async Task<ApsDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        await projects.DraftGate.WaitAsync(cancellationToken);
        return CreateLocked();
    }
    private ApsDbContext CreateLocked()
    {
        try { return new DraftContext(new DbContextOptionsBuilder<ApsDbContext>().UseSqlite(draft.ConnectionString).Options, this,
            ProjectDatabaseService.AcquireFileLock(draft.DatabasePath + ".edit.lock")); }
        catch { projects.DraftGate.Release(); throw; }
    }

    private sealed class DraftContext(DbContextOptions<ApsDbContext> options, DraftDbContextFactory owner, FileStream fileLock) : ApsDbContext(options)
    {
        private bool _disposed;
        private bool _wrote;
        private readonly Dictionary<string, (string Snapshot, Type Type, object?[] Keys)> _saved = [];
        private HashSet<object> _changedEntities = new(ReferenceEqualityComparer.Instance);
        private static bool Configuration(EntityEntry entry) => entry.Metadata.ClrType.Name is
            "Signal" or "Window" or "ScadaCommand" or "TrdpSessionConfig" or "ProjectBranding" or "ImportedControlEventConfiguration";
        private static string Key(EntityEntry entry) => entry.Metadata.Name + ":" +
            string.Join('|', entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue));
        private static string Snapshot(PropertyValues values) => JsonSerializer.Serialize(values.Properties
            .OrderBy(x => x.Name).Select(p => new { p.Name, Value = values[p] }).ToArray());

        private void CheckWrites()
        {
            ChangeTracker.DetectChanges();
            _changedEntities = ChangeTracker.Entries().Where(Configuration)
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
            if (!ChangeTracker.HasChanges()) return;
            if (ProjectDatabaseService.IsSealed(owner._draft))
                throw new InvalidOperationException("Este borrador ya se publicó. Recarga la página para editar la nueva revisión.");
            foreach (var entry in ChangeTracker.Entries().Where(Configuration).Where(e => e.State is EntityState.Modified or EntityState.Deleted))
            {
                var current = entry.GetDatabaseValues();
                var key = Key(entry);
                var expected = _saved.TryGetValue(key, out var saved) ? saved.Snapshot : owner._seen.GetValueOrDefault(key);
                if (current is null || (expected is not null && Snapshot(current) != expected))
                    throw new InvalidOperationException("Otro usuario ha cambiado estos datos. Tus cambios no se han guardado. Recarga la página y revisa la versión actual antes de volver a guardar.");
            }
        }
        private void Remember()
        {
            foreach (var entry in ChangeTracker.Entries().Where(e => _changedEntities.Contains(e.Entity)))
                _saved[Key(entry)] = (Snapshot(entry.CurrentValues), entry.Metadata.ClrType,
                    entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray());
        }
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            CheckWrites();
            var count = base.SaveChanges(acceptAllChangesOnSuccess);
            _wrote |= count > 0; Remember(); return count;
        }
        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            CheckWrites();
            var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            _wrote |= count > 0; Remember(); return count;
        }
        private void Release()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // Read back after context/transaction disposal. A rolled-back import must not
                // advance a page's expected values to changes that were never committed.
                if (_wrote)
                {
                    using var persisted = new ProjectDbContextFactory(owner._draft.ConnectionString).CreateDbContext();
                    foreach (var (key, value) in _saved)
                    {
                        var entity = persisted.Find(value.Type, value.Keys);
                        if (entity is null) owner._seen.Remove(key);
                        else owner._seen[key] = Snapshot(persisted.Entry(entity).CurrentValues);
                    }
                }
            }
            finally
            {
                fileLock.Dispose();
                owner._projects.DraftGate.Release();
                if (_wrote) owner._projects.NotifyDraftChanged();
            }
        }
        private void RememberReads()
        {
            // Do not replace earlier snapshots with values fetched by a delete/import operation.
            if (_wrote || ChangeTracker.HasChanges()) return;
            foreach (var entry in ChangeTracker.Entries().Where(Configuration))
                owner._seen.TryAdd(Key(entry), Snapshot(entry.OriginalValues));
        }
        public override void Dispose()
        {
            if (_disposed) return;
            try { RememberReads(); base.Dispose(); } finally { Release(); }
        }
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            try { RememberReads(); await base.DisposeAsync(); } finally { Release(); }
        }
    }
}
