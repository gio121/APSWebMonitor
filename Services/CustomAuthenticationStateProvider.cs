using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.EntityFrameworkCore;
using ApsMonitor.Data;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public class CustomAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly ProtectedSessionStorage _sessionStorage;
    private readonly IDbContextFactory<ApsDbContext> _dbContextFactory;
    private ClaimsPrincipal _anonymous = new ClaimsPrincipal(new ClaimsIdentity());
    private ClaimsPrincipal? _cachedUser;

    public CustomAuthenticationStateProvider(
        ProtectedSessionStorage sessionStorage,
        IDbContextFactory<ApsDbContext> dbContextFactory)
    {
        _sessionStorage = sessionStorage;
        _dbContextFactory = dbContextFactory;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_cachedUser != null)
            return new AuthenticationState(_cachedUser);

        try
        {
            var storedUsername = await _sessionStorage.GetAsync<string>("auth_username");
            if (storedUsername.Success && !string.IsNullOrEmpty(storedUsername.Value))
            {
                using var context = await _dbContextFactory.CreateDbContextAsync();
                var user = await context.Users.FirstOrDefaultAsync(u => u.Username == storedUsername.Value);
                if (user != null)
                {
                    _cachedUser = CreateClaimsPrincipal(user);
                    return new AuthenticationState(_cachedUser);
                }
            }
        }
        catch (Exception)
        {
            // ProtectedSessionStorage can throw on prerender — ignore
        }

        return new AuthenticationState(_anonymous);
    }

    public async Task<User?> LoginAsync(string username, string password)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null || !PasswordHasher.Verify(password, user.PasswordHash))
            return null;

        await _sessionStorage.SetAsync("auth_username", user.Username);
        _cachedUser = CreateClaimsPrincipal(user);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_cachedUser)));
        return user;
    }

    public async Task LogoutAsync()
    {
        await _sessionStorage.DeleteAsync("auth_username");
        _cachedUser = null;
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
    }

    private ClaimsPrincipal CreateClaimsPrincipal(User user)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.Nombre),
            new Claim(ClaimTypes.NameIdentifier, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var identity = new ClaimsIdentity(claims, "CustomAuth");
        return new ClaimsPrincipal(identity);
    }
}
