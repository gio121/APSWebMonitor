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
    
    public async Task<Window> AddWindowAsync(Window window)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Windows.Add(window);
        await context.SaveChangesAsync();
        return window;
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
    public async Task<List<Signal>> GetSignalsAsync(bool includeDeleted = false)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await (includeDeleted ? context.Signals.IgnoreQueryFilters() : context.Signals).ToListAsync();
    }

    public async Task DeleteAllSignalsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        await context.Signals.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true));
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
            signal.IsDeleted = true;
            await context.SaveChangesAsync();
        }
    }

    // Reimport by logical identity without replacing the IDs used by windows.
    public async Task ImportSignalsAsync(IEnumerable<Signal> importedSignals)
    {
        var incoming = importedSignals.ToList();
        if (incoming.Any(s => string.IsNullOrWhiteSpace(s.Tag)))
            throw new ArgumentException("Las señales importadas deben tener un keyname.");

        using var context = await _dbContextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var existing = (await context.Signals.IgnoreQueryFilters().ToListAsync())
            .GroupBy(s => (s.NodoNumero, Tag: s.Tag.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var signal in incoming)
        {
            var key = (signal.NodoNumero, Tag: signal.Tag.Trim().ToUpperInvariant());
            signal.IsDeleted = false;
            if (existing.TryGetValue(key, out var matches))
            {
                // Older imports may have duplicates. Keep every existing ID because
                // different windows can reference different copies of the same signal.
                var values = context.Entry(signal).CurrentValues;
                foreach (var match in matches)
                    foreach (var property in values.Properties.Where(p => p.Name != nameof(Signal.Id)))
                        context.Entry(match).Property(property.Name).CurrentValue = values[property];
            }
            else
            {
                signal.Id = 0;
                context.Signals.Add(signal);
                existing.Add(key, [signal]);
            }
        }
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // Events
    public async Task<List<EventMessage>> GetEventsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Events.OrderByDescending(e => e.Fecha).Take(10).ToListAsync();
    }

    public async Task<List<EventMessage>> GetAllEventsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Events.OrderByDescending(e => e.Fecha).ToListAsync();
    }

    public async Task AddEventAsync(EventMessage evento)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Events.Add(evento);
        await context.SaveChangesAsync();
    }

    // Commands
    public async Task<List<ScadaCommand>> GetCommandsAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Commands.ToListAsync();
    }

    public async Task AddCommandAsync(ScadaCommand command)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Commands.Add(command);
        await context.SaveChangesAsync();
    }

    public async Task UpdateCommandAsync(ScadaCommand command)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Commands.Update(command);
        await context.SaveChangesAsync();
    }

    public async Task DeleteCommandAsync(int id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var command = await context.Commands.FindAsync(id);
        if (command != null)
        {
            context.Commands.Remove(command);
            await context.SaveChangesAsync();
        }
    }

    // Users
    public async Task<List<User>> GetUsersAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Users.ToListAsync();
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Users.FirstOrDefaultAsync(u => u.Username == username);
    }

    public async Task AddUserAsync(User user)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Users.Add(user);
        await context.SaveChangesAsync();
    }

    public async Task UpdateUserAsync(User user)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.Users.Update(user);
        await context.SaveChangesAsync();
    }

    public async Task DeleteUserAsync(int id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FindAsync(id);
        if (user != null)
        {
            context.Users.Remove(user);
            await context.SaveChangesAsync();
        }
    }
}
