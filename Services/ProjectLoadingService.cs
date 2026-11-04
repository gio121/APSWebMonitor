namespace ApsMonitor.Services;

public sealed class ProjectLoadingService(ProjectDatabaseService projects, ProjectBrandingService branding,
    MonitorStateService monitor, PageAccessService? access = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task LoadAsync(string id, bool keepDraft = false)
    {
        await _gate.WaitAsync();
        try
        {
            var project = projects.Find(id);
            if (project.Id == projects.Current?.Id) return;
            if (!keepDraft && projects.Current is not null && (await projects.GetDraftStatusAsync()).HasChanges)
                throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("There are pending changes. Create a new revision or keep the draft before switching."));
            project = await projects.OpenDraftAsync(project);
            await monitor.ChangeProjectAsync(async () =>
            {
                await branding.ChangeProjectAsync(new ApsMonitor.Data.ProjectDbContextFactory(project.ConnectionString),
                    () => projects.Activate(project));
                projects.NotifyChanged();
            });
        }
        finally { _gate.Release(); }
    }

    public async Task PublishAsync(string revision, string description, System.Security.Claims.ClaimsPrincipal user)
    {
        await RequireEditor(user);
        await _gate.WaitAsync();
        try
        {
            await monitor.ChangeProjectAsync(async () =>
            {
                var published = await projects.PublishDraftAsync(revision, description, user.Identity.Name ?? "Administrador");
                var draft = await projects.OpenDraftAsync(published);
                await branding.ChangeProjectAsync(new ApsMonitor.Data.ProjectDbContextFactory(draft.ConnectionString),
                    () => projects.Activate(draft));
                projects.NotifyChanged();
            });
        }
        finally { _gate.Release(); }
    }

    private async Task RequireEditor(System.Security.Claims.ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated == true)
        {
            if (user.IsInRole("Administrador")) return;
            var role = user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            if (role is not null && access is not null)
            {
                var permissions = await access.GetAsync(role);
                if (new[] { "senales", "comandos", "trdp" }.Any(key => permissions.GetValueOrDefault(key))) return;
            }
        }
        throw new UnauthorizedAccessException(ApsMonitor.Resources.MonitorResources.Text("You need configuration editing permissions to publish or discard a draft."));
    }

    public async Task DiscardAsync(System.Security.Claims.ClaimsPrincipal user)
    {
        await RequireEditor(user);
        await _gate.WaitAsync();
        try
        {
            await monitor.ChangeProjectAsync(async () =>
            {
                var draft = await projects.DiscardDraftAsync();
                await branding.ChangeProjectAsync(new ApsMonitor.Data.ProjectDbContextFactory(draft.ConnectionString),
                    () => projects.Activate(draft));
                projects.NotifyChanged();
            });
        }
        finally { _gate.Release(); }
    }
}
