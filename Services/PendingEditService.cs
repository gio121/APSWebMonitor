namespace ApsMonitor.Services;

// Per browser circuit; prevents a shared project switch from destroying an open form.
public sealed class PendingEditService
{
    private readonly HashSet<object> _dirtyForms = [];
    public bool HasChanges => _dirtyForms.Count > 0;
    public void Set(object form, bool dirty)
    {
        if (dirty) _dirtyForms.Add(form);
        else _dirtyForms.Remove(form);
    }
}
