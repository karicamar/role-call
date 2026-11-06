using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using RoleCall.Web.Data;
using RoleCall.Web.Services;

namespace RoleCall.Tests;

/// <summary>The real application in memory, with its own SQLite file and a clock the test controls.</summary>
public sealed class RoleCallApp(bool demoLogin = true, bool seed = false, string environment = "Testing") : WebApplicationFactory<Program>
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"role-call-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
    public DateOnly Today => new(2026, 11, 6);

    public static readonly Actor Lena = new("lena.iam", "E1010", new HashSet<string> { Roles.Iam });
    public static readonly Actor Anna = new("anna.audit", "E1050", new HashSet<string> { Roles.Auditor });
    public static readonly Actor Tom = new("tom.payments", "E1020", new HashSet<string>());
    public static readonly Actor Nina = new("nina.treasury", "E1030", new HashSet<string>());
    public static readonly Actor Clara = new("clara.ceo", "E1001", new HashSet<string>());
    public static readonly Actor Jonas = new("jonas.ciso", "E1002", new HashSet<string>());
    public static readonly Actor Mia = new("mia.payments", "E1021", new HashSet<string>());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:RoleCall", $"Data Source={dbPath};Pooling=false");
        builder.UseSetting("RoleCall:DemoLogin", demoLogin ? "true" : "false");
        builder.UseSetting("RoleCall:SeedDemoData", seed ? "true" : "false");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }

    public ReviewService Reviews => Services.CreateScope().ServiceProvider.GetRequiredService<ReviewService>();

    public RoleCallDb Db() => Services.GetRequiredService<IDbContextFactory<RoleCallDb>>().CreateDbContext();

    public static string Policy(DateOnly today) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "policy.json"))
            .Replace("TODAY+20", today.AddDays(20).ToString("yyyy-MM-dd")).Replace("TODAY-15", today.AddDays(-15).ToString("yyyy-MM-dd"));

    /// <summary>Imports the demo bank as of today (HR feed, four applications, policy) through the service.</summary>
    public async Task ImportDemoAsync()
    {
        var r = Reviews;
        await r.ImportPeopleAsync(Lena, "hr.csv", DemoData.Hr(Today));
        foreach (var (app, csv) in DemoData.Apps(Today)) await r.ImportAccountsAsync(Lena, app, $"{app}.csv", csv);
        await r.ImportPolicyAsync(Lena, "policy.json", Policy(Today));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
