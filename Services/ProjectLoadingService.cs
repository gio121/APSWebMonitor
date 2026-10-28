namespace ApsMonitor.Services;

public sealed class ProjectLoadingService(ProjectDatabaseService projects, ProjectBrandingService branding,
    MonitorStateService monitor)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task LoadAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var project = projects.Find(id);
            if (project.Id == projects.Current?.Id) return;
            await ProjectDatabaseService.PrepareAsync(project);
            await monitor.ChangeProjectAsync(async () =>
            {
                await branding.ChangeProjectAsync(new ApsMonitor.Data.ProjectDbContextFactory(project.ConnectionString),
                    () => projects.Activate(project));
                projects.NotifyChanged();
            });
        }
        finally { _gate.Release(); }
    }
}
