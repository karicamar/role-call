using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RoleCall.Domain.Reviews;
using RoleCall.Web.Services;

namespace RoleCall.Web.Data;

/// <summary>
/// A fictional bank with four applications and a review campaign in progress. All dates are relative to today, so the
/// demo always shows the same situation: a leaver who still logs in, a clerk who can create and approve payments, a
/// mover who kept their old access, a shared admin account, and a manager who clicked through their review.
/// </summary>
public static class DemoData
{
    public static readonly Actor Seed = new("demo seed", null, new HashSet<string> { Roles.Iam });

    public static async Task SeedAsync(IDbContextFactory<RoleCallDb> dbs, DateTimeOffset now, IOptions<RoleCallOptions> options)
    {
        await using (var db = await dbs.CreateDbContextAsync())
            if (await db.People.AnyAsync()) return;

        var time = new ManualTime(now.AddDays(-9).AddHours(-2));
        var service = new ReviewService(dbs, new Clock(time, options));
        var today = new Clock(TimeProvider.System, options).Today;
        var policy = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "policy.json")))
            .Replace("TODAY+20", D(today.AddDays(20))).Replace("TODAY-15", D(today.AddDays(-15)));

        // The exports are from the day the campaign started, nine days ago
        var basis = today.AddDays(-9);
        await service.ImportPeopleAsync(Seed, "hr-export.csv", Hr(basis));
        foreach (var (app, csv) in Apps(basis)) await service.ImportAccountsAsync(Seed, app, $"{app.ToLowerInvariant()}-accounts.csv", csv);
        await service.ImportPolicyAsync(Seed, "policy.json", policy);

        time.Now = time.Now.AddMinutes(20);
        var campaign = await service.StartCampaignAsync(new Actor("lena.iam", "E1010", new HashSet<string> { Roles.Iam }), "Q4 2026 access recertification", today.AddDays(5));

        await using var read = await dbs.CreateDbContextAsync();
        var items = await read.Items.AsNoTracking().Where(i => i.CampaignId == campaign.Id).OrderBy(i => i.Id).ToListAsync();

        // Tom reviews properly: one item at a time, with time to look at each
        time.Now = now.AddDays(-6).AddHours(-3);
        var tom = Reviewer("tom.payments", "E1020");
        foreach (var i in items.Where(i => i.ReviewerId == "E1020" && i.SubjectId is "E1022" or "E1024"))
        {
            time.Now = time.Now.AddSeconds(35 + i.Id % 40);
            await service.DecideAsync(tom, i.Id, i.Version, Decision.Keep, null);
        }

        // Nina keeps everything without risk in seconds: the auditor sees this as a rubber-stamping signal
        time.Now = now.AddDays(-2).AddHours(-5);
        var nina = Reviewer("nina.treasury", "E1030");
        foreach (var i in items.Where(i => i.ReviewerId == "E1030" && ReviewRules.Check("E1030", i.ReviewerId, i.SubjectId, i.Risks, Decision.Keep, null, true) is null))
        {
            time.Now = time.Now.AddSeconds(2 + i.Id % 2);
            await service.DecideAsync(nina, i.Id, i.Version, Decision.Keep, null);
        }
    }

    static Actor Reviewer(string name, string id) => new(name, id, new HashSet<string>());

    static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Hr(DateOnly today) => $"""
        employee_id,name,email,department,position,manager_id,leave_date,previous_department,moved_on
        E1001,Dr. Clara Wendt,clara.wendt@mainufer-bank.example,Management,Chief Executive Officer,,,,
        E1002,Jonas Weber,jonas.weber@mainufer-bank.example,Information Security,Chief Information Security Officer,E1001,,,
        E1010,Lena Hoffmann,lena.hoffmann@mainufer-bank.example,IAM,Head of Identity and Access Management,E1002,,,
        E1020,Tom Becker,tom.becker@mainufer-bank.example,Payments,Head of Payments,E1001,,,
        E1021,Mia Schulz,mia.schulz@mainufer-bank.example,Payments,Payments Clerk,E1020,,,
        E1022,Felix Wagner,felix.wagner@mainufer-bank.example,Payments,Payments Clerk,E1020,,,
        E1023,Sara Yilmaz,sara.yilmaz@mainufer-bank.example,Payments,Senior Payments Clerk,E1020,,,
        E1024,Jan Krüger,jan.krueger@mainufer-bank.example,Payments,Payments Clerk,E1020,,,
        E1025,Julia Hahn,julia.hahn@mainufer-bank.example,Payments,Team Lead Payments,E1020,,,
        E1030,Nina Koch,nina.koch@mainufer-bank.example,Treasury,Head of Treasury,E1001,,,
        E1031,Paul Richter,paul.richter@mainufer-bank.example,Treasury,Dealer,E1030,,Payments,{D(today.AddDays(-60))}
        E1032,Lukas Braun,lukas.braun@mainufer-bank.example,Treasury,Dealer,E1030,{D(today.AddDays(-5))},,
        E1033,Emma Schäfer,emma.schaefer@mainufer-bank.example,Treasury Operations,Settlement Officer,E1030,,,
        E1034,Max Wolf,max.wolf@mainufer-bank.example,Treasury,Dealer,E1030,,,
        E1035,Sophie Lang,sophie.lang@mainufer-bank.example,Treasury,Dealer,E1030,,,
        E1036,Tim Berger,tim.berger@mainufer-bank.example,Treasury Operations,Settlement Officer,E1030,,,
        E1040,Daniel Klein,daniel.klein@mainufer-bank.example,IT Operations,Head of IT Operations,E1001,,,
        E1041,Eva Neumann,eva.neumann@mainufer-bank.example,Development,Software Developer,E1040,,,
        E1042,Leon Schmitt,leon.schmitt@mainufer-bank.example,IT Operations,System Administrator,E1040,,,
        E1050,Anna Vogel,anna.vogel@mainufer-bank.example,Internal Audit,Internal Auditor,E1001,,,
        """;

    public static IEnumerable<(string App, string Csv)> Apps(DateOnly today)
    {
        string Ago(int days) => D(today.AddDays(-days));
        yield return ("Payments", $"""
            account_id,employee_id,email,enabled,privileged,created,last_login,entitlements
            t.becker,E1020,,yes,no,{Ago(900)},{Ago(1)},ViewReports|Approve|Release
            m.schulz,E1021,,yes,no,{Ago(400)},{Ago(1)},Create|Approve|ViewReports
            f.wagner,E1022,,yes,no,{Ago(300)},{Ago(2)},Create|ViewReports
            s.yilmaz,E1023,,yes,no,{Ago(700)},{Ago(1)},Create|Release|ViewReports
            j.krueger,E1024,,yes,no,{Ago(200)},{Ago(3)},Create|ViewReports
            j.hahn,E1025,,yes,no,{Ago(650)},{Ago(1)},Create|Approve|ViewReports
            p.richter,E1031,,yes,no,{Ago(500)},{Ago(20)},Create|ViewReports
            j.schmidt,E0999,,yes,no,{Ago(800)},{Ago(150)},Create|Approve
            pay-admin,E1042,,yes,yes,{Ago(600)},{Ago(30)},AdminUsers
            c.wendt,E1001,,yes,no,{Ago(900)},{Ago(14)},ViewReports
            """);
        yield return ("Treasury", $"""
            account_id,employee_id,email,enabled,privileged,created,last_login,entitlements
            nkoch,E1030,,yes,no,{Ago(1000)},{Ago(1)},Trade|View|Confirm
            prichter,E1031,,yes,no,{Ago(60)},{Ago(1)},Trade|View
            lbraun,E1032,,yes,no,{Ago(800)},{Ago(6)},Trade|View
            eschaefer,E1033,,yes,no,{Ago(500)},{Ago(1)},Confirm|Settle|View
            mwolf,E1034,,yes,no,{Ago(450)},{Ago(140)},Trade|View
            slang,E1035,,yes,no,{Ago(300)},{Ago(2)},Trade|View
            tberger,E1036,,yes,no,{Ago(250)},{Ago(1)},Confirm|Settle|View
            """);
        yield return ("CoreBanking", $"""
            account_id,employee_id,email,enabled,privileged,created,last_login,entitlements
            dklein,E1040,,yes,yes,{Ago(1200)},{Ago(1)},Read|DeployProd
            eneumann,E1041,,yes,no,{Ago(400)},{Ago(1)},Read|Develop|DeployProd
            lschmitt,,leon.schmitt@mainufer-bank.example,yes,yes,{Ago(700)},{Ago(4)},Read|DeployProd|DBA
            avogel,E1050,,yes,no,{Ago(300)},{Ago(9)},Read
            jweber,E1002,,yes,no,{Ago(500)},{Ago(30)},Read
            """);
        yield return ("Directory", $"""
            account_id,employee_id,email,enabled,privileged,created,last_login,entitlements
            cwendt,E1001,,yes,no,{Ago(1500)},{Ago(1)},User
            jweber,E1002,,yes,no,{Ago(900)},{Ago(1)},User
            lhoffmann,E1010,,yes,no,{Ago(700)},{Ago(1)},User|IdentityAdmin
            tbecker,E1020,,yes,no,{Ago(1100)},{Ago(1)},User
            mschulz,E1021,,yes,no,{Ago(400)},{Ago(1)},User
            fwagner,E1022,,yes,no,{Ago(300)},{Ago(1)},User
            syilmaz,E1023,,yes,no,{Ago(700)},{Ago(1)},User
            jkrueger,E1024,,yes,no,{Ago(200)},{Ago(1)},User
            jhahn,E1025,,yes,no,{Ago(650)},{Ago(1)},User
            nkoch,E1030,,yes,no,{Ago(1000)},{Ago(1)},User
            prichter,E1031,,yes,no,{Ago(500)},{Ago(1)},User
            lbraun,E1032,,yes,no,{Ago(800)},{Ago(3)},User|VPN
            eschaefer,E1033,,yes,no,{Ago(500)},{Ago(1)},User
            mwolf,E1034,,yes,no,{Ago(450)},{Ago(140)},User
            slang,E1035,,yes,no,{Ago(300)},{Ago(1)},User
            tberger,E1036,,yes,no,{Ago(250)},{Ago(1)},User
            dklein,E1040,,yes,no,{Ago(1200)},{Ago(1)},User|HelpdeskReset
            eneumann,E1041,,yes,no,{Ago(400)},{Ago(1)},User
            lschmitt,E1042,,yes,no,{Ago(700)},{Ago(1)},User|HelpdeskReset
            adm-lschmitt,E1042,,yes,yes,{Ago(700)},{Ago(5)},DomainAdmin
            avogel,E1050,,yes,no,{Ago(300)},{Ago(1)},User
            svc_backup,,,yes,yes,{Ago(2000)},{Ago(1)},DomainAdmin
            old-intern,,,no,no,{Ago(900)},{Ago(600)},User
            """);
    }

    /// <summary>A clock the seed moves by hand, so decisions get realistic timestamps.</summary>
    sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
