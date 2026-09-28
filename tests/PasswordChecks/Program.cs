using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using ApsMonitor.Data;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.JSInterop;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

const string password = "Prueba ñ🔐 con espacios ";
var hash = PasswordHasher.Hash(password);
var secondHash = PasswordHasher.Hash(password);
Check(hash.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$"), "Wrong algorithm/parameters");
Check(hash != secondHash, "Salts must differ");
Check(PasswordHasher.Verify(password, hash, out var rehash) && !rehash, "Argon2 verification");
Check(PasswordHasher.Verify(password, secondHash), "Second salt verification");
Check(!PasswordHasher.Verify(password.Trim(), hash), "Password must not be trimmed");
Check(!PasswordHasher.Verify("incorrect", hash, out rehash) && !rehash, "Wrong password accepted");
var legacy = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
Check(PasswordHasher.Verify(password, legacy, out rehash) && rehash, "Legacy migration flag");
Check(!PasswordHasher.Verify("incorrect", legacy, out rehash) && !rehash, "Wrong legacy password");
foreach (var invalid in new[] { "", "invalid", "$argon2id$", "$argon2id$v=19$m=oops,t=2,p=1$x$y",
    hash.Replace("m=19456", "m=2147483647"), hash.Replace("t=2", "t=999999999999999"),
    hash.Replace("p=1", "p=999"), hash.Replace("argon2id", "argon2i"),
    Convert.ToBase64String(new byte[31]), new string('x', 513) })
    Check(!PasswordHasher.Verify(password, invalid, out rehash) && !rehash, "Malformed hash accepted");

// SQLite stays in memory: these checks never open the monitor's aps.db.
using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<ApsDbContext>().UseSqlite(connection).Options;
var factory = new TestDbFactory(options);
using (var db = factory.CreateDbContext())
{
    db.Database.EnsureCreated();
    db.Users.Add(new User { Username = "test", Nombre = "Test", PasswordHash = legacy });
    db.SaveChanges();
}
var js = new TestJsRuntime();
var storage = new ProtectedSessionStorage(js, new EphemeralDataProtectionProvider());
var auth = new CustomAuthenticationStateProvider(storage, factory);
Check(await auth.LoginAsync("missing", password) is null, "Unknown user accepted");
Check(await auth.LoginAsync("test", "incorrect") is null, "Incorrect login accepted");
using (var db = factory.CreateDbContext())
    Check((await db.Users.SingleAsync()).PasswordHash == legacy, "Failed login modified database");
Check(js.Writes == 0, "Failed login created session");
var user = await auth.LoginAsync("test", password);
Check(user is not null && js.Writes == 1, "Successful legacy login/session");
string migrated;
using (var db = factory.CreateDbContext())
{
    migrated = (await db.Users.SingleAsync()).PasswordHash;
    Check(migrated.StartsWith("$argon2id$") && PasswordHasher.Verify(password, migrated), "Migration not persisted");
}
Check(await auth.LoginAsync("test", password) is not null, "Migrated login failed");
using (var db = factory.CreateDbContext())
{
    var saved = await db.Users.SingleAsync();
    Check(saved.PasswordHash == migrated, "Argon2 login unnecessarily rewrote hash");
    saved.PasswordHash = PasswordHasher.Hash("New password");
    await db.SaveChangesAsync();
}
Check(await auth.LoginAsync("test", password) is null, "Old password accepted after reset");
Check(await auth.LoginAsync("test", "New password") is not null, "New password rejected");
using (var db = factory.CreateDbContext())
    await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, legacy));
var resetHash = PasswordHasher.Hash("Concurrent reset");
var raceOptions = new DbContextOptionsBuilder<ApsDbContext>().UseSqlite(connection)
    .AddInterceptors(new ConcurrentResetInterceptor(resetHash)).Options;
var writesBeforeRace = js.Writes;
var raceAuth = new CustomAuthenticationStateProvider(storage, new TestDbFactory(raceOptions));
Check(await raceAuth.LoginAsync("test", password) is null, "Concurrent reset must reject stale login");
Check(js.Writes == writesBeforeRace, "Concurrent reset created stale session");
using (var db = factory.CreateDbContext())
    Check((await db.Users.SingleAsync()).PasswordHash == resetHash, "Migration overwrote concurrent reset");
Console.WriteLine("PASS: Argon2id, random salts, Unicode, legacy verification, malformed/cost-limited hashes, SQLite login migration, failed login, password reset, concurrent reset.");

sealed class ConcurrentResetInterceptor(string resetHash) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
        {
            using var reset = command.Connection!.CreateCommand();
            reset.CommandText = "UPDATE Users SET PasswordHash = $hash";
            var parameter = reset.CreateParameter();
            parameter.ParameterName = "$hash";
            parameter.Value = resetHash;
            reset.Parameters.Add(parameter);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        return result;
    }
}

sealed class TestDbFactory(DbContextOptions<ApsDbContext> options) : IDbContextFactory<ApsDbContext>
{
    public ApsDbContext CreateDbContext() => new(options);
}

sealed class TestJsRuntime : IJSRuntime
{
    public int Writes { get; private set; }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, default, args);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier == "sessionStorage.setItem") Writes++;
        return ValueTask.FromResult(default(TValue)!);
    }
}
