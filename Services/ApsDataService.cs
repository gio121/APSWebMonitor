using ApsMonitor.Models;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Services;

public class ApsDataService
{
    private readonly IDbContextFactory<Data.ApsDbContext> _dbContextFactory;

    public ApsDataService(IDbContextFactory<Data.ApsDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    // Windows
    public async Task<List<Window>> GetWindowsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Windows.ToListAsync();
    }
    
    public async Task UpdateWindowAsync(Window window)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Windows.Update(window);
        await context.SaveChangesAsync();
    }
    
    public async Task DeleteWindowAsync(int id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var window = await context.Windows.FindAsync(id);
        if (window != null)
        {
            context.Windows.Remove(window);
            await context.SaveChangesAsync();
        }
    }

    // Signals
    public async Task<List<Signal>> GetSignalsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Signals.ToListAsync();
    }

    public async Task<Signal> GetSignalAsync(int id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Signals.FindAsync(id) ?? new Signal();
    }

    public async Task AddSignalAsync(Signal signal)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Signals.Add(signal);
        await context.SaveChangesAsync();
    }

    public async Task UpdateSignalAsync(Signal signal)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Signals.Update(signal);
        await context.SaveChangesAsync();
    }

    public async Task DeleteSignalAsync(int id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var signal = await context.Signals.FindAsync(id);
        if (signal != null)
        {
            context.Signals.Remove(signal);
            await context.SaveChangesAsync();
        }
    }

    // Events
    public async Task<List<EventMessage>> GetEventsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Events.OrderByDescending(e => e.Fecha).Take(10).ToListAsync();
    }
}
