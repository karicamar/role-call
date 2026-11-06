using RoleCall.Domain.Analysis;
using RoleCall.Domain.Model;

namespace RoleCall.Tests;

/// <summary>Small builders so each test shows only what matters for it.</summary>
static class Build
{
    public static readonly DateOnly Today = new(2026, 11, 6);

    public static Person Person(string id, string dept = "Payments", string? manager = "M1", DateOnly? left = null, string? from = null, DateOnly? movedOn = null) =>
        new(id, $"Person {id}", $"{id.ToLowerInvariant()}@bank.example", dept, "Clerk", manager, left, from, movedOn);

    public static Account Account(string app, string id, string? owner, params string[] entitlements) =>
        new(app, id, owner, null, true, false, Today.AddDays(-400), Today.AddDays(-1), entitlements.Select(e => $"{app}:{e}").ToList());

    public static readonly SodRule CreateApprove = new("SOD-01", "Create and approve payments", ["Payments:Create"], ["Payments:Approve", "Payments:Release"], Severity.Critical, "Four eyes.");
    public static readonly SodRule TradeSettle = new("SOD-02", "Trade and settle", ["Treasury:Trade"], ["Treasury:Settle"], Severity.High, "Front and back office.");

    public static Policy Policy(params Mitigation[] mitigations) => new()
    {
        SodRules = [CreateApprove, TradeSettle],
        Mitigations = mitigations,
        FallbackReviewer = "CISO",
        AppOwners = new Dictionary<string, string> { ["Payments"] = "OWN", ["Treasury"] = "OWN2" },
    };

    public static readonly Person Manager = Person("M1", manager: null);
    public static readonly Person Owner = Person("OWN", manager: null);
    public static readonly Person Owner2 = Person("OWN2", "Treasury", manager: null);
    public static readonly Person Ciso = Person("CISO", "Security", manager: null);

    public static Snapshot Snapshot(IEnumerable<Person> people, params Account[] accounts) =>
        new([Manager, Owner, Owner2, Ciso, .. people], accounts);

    public static IReadOnlyList<Finding> Analyze(Snapshot s, Policy? p = null, DateOnly? today = null) =>
        Analyzer.Analyze(s, p ?? Policy(), today ?? Today);
}

public class AnalyzerTests
{
    [Fact]
    public void A_clean_snapshot_has_no_findings()
    {
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "e1", "E1", "Create"));
        Assert.Empty(Build.Analyze(s));
    }

    [Fact]
    public void An_account_nobody_owns_is_orphaned_and_a_privileged_one_is_critical()
    {
        var s = Build.Snapshot([],
            Build.Account("Payments", "ghost", "E999", "Create"),
            Build.Account("Directory", "svc_backup", null, "DomainAdmin") with { Privileged = true },
            Build.Account("Directory", "old", null, "User") with { Enabled = false });
        var f = Build.Analyze(s);
        Assert.Equal(2, f.Count);
        Assert.Equal((FindingKind.UnownedPrivileged, Severity.Critical), (f[0].Kind, f[0].Severity));
        Assert.Equal((FindingKind.Orphan, Severity.High), (f[1].Kind, f[1].Severity));
        Assert.Contains("employee id E999 is unknown", f[1].Message);
    }

    [Fact]
    public void A_leaver_gets_one_day_of_grace_but_a_privileged_account_none()
    {
        var left = Build.Today.AddDays(-1);
        var s = Build.Snapshot([Build.Person("E1", left: left)],
            Build.Account("Payments", "e1", "E1", "Create") with { LastLogin = left.AddDays(-1) },
            Build.Account("Directory", "adm-e1", "E1", "DomainAdmin") with { Privileged = true, LastLogin = left.AddDays(-1) });
        var f = Build.Analyze(s);
        var single = Assert.Single(f);
        Assert.Equal(FindingKind.LeaverActive, single.Kind);
        Assert.Equal("Directory/adm-e1", single.AccountKey);
        Assert.Equal(Severity.Critical, single.Severity);

        Assert.Equal(2, Build.Analyze(s, today: Build.Today.AddDays(1)).Count(x => x.Kind == FindingKind.LeaverActive));
    }

    [Fact]
    public void A_login_after_the_last_working_day_makes_a_leaver_critical()
    {
        var left = Build.Today.AddDays(-5);
        var s = Build.Snapshot([Build.Person("E1", left: left)],
            Build.Account("Payments", "e1", "E1", "Create") with { LastLogin = Build.Today.AddDays(-1) },
            Build.Account("Treasury", "e1", "E1", "View") with { LastLogin = left.AddDays(-1) });
        var f = Build.Analyze(s).Where(x => x.Kind == FindingKind.LeaverActive).ToList();
        Assert.Equal(Severity.Critical, f.Single(x => x.AccountKey == "Payments/e1").Severity);
        Assert.Contains("after the last working day", f.Single(x => x.AccountKey == "Payments/e1").Message);
        Assert.Equal(Severity.High, f.Single(x => x.AccountKey == "Treasury/e1").Severity);
    }

    [Fact]
    public void A_leaver_with_disabled_accounts_is_fine_and_gets_no_other_findings()
    {
        var s = Build.Snapshot([Build.Person("E1", left: Build.Today.AddDays(-40))],
            Build.Account("Payments", "e1", "E1", "Create", "Approve") with { Enabled = false });
        Assert.Empty(Build.Analyze(s));
    }

    [Fact]
    public void Accounts_unused_beyond_the_limit_are_dormant()
    {
        var s = Build.Snapshot([Build.Person("E1"), Build.Person("E2"), Build.Person("E3")],
            Build.Account("Payments", "e1", "E1", "Create") with { LastLogin = Build.Today.AddDays(-91) },
            Build.Account("Payments", "e2", "E2", "Create") with { LastLogin = Build.Today.AddDays(-90) },
            Build.Account("Payments", "e3", "E3", "Create") with { LastLogin = null, Created = Build.Today.AddDays(-100) });
        var f = Build.Analyze(s);
        Assert.Equal(["Payments/e1", "Payments/e3"], f.Where(x => x.Kind == FindingKind.Dormant).Select(x => x.AccountKey).Order());
        Assert.Contains("never been used", f.Single(x => x.AccountKey == "Payments/e3").Message);
    }

    [Fact]
    public void Segregation_of_duties_is_checked_across_applications()
    {
        var rule = new SodRule("SOD-04", "Admin and approve", ["Directory:DomainAdmin"], ["Payments:Approve"], Severity.Critical, "x");
        var s = Build.Snapshot([Build.Person("E1")],
            Build.Account("Payments", "e1", "E1", "Approve"),
            Build.Account("Directory", "adm-e1", "E1", "DomainAdmin"));
        var f = Build.Analyze(s, Build.Policy() with { SodRules = [rule] });
        var sod = Assert.Single(f);
        Assert.Equal(FindingKind.SodConflict, sod.Kind);
        Assert.Equal("SOD-04", sod.RuleId);
        Assert.Null(sod.AccountKey);
        Assert.Contains("Across", sod.Message);
        Assert.Equal(["Directory:DomainAdmin", "Payments:Approve"], sod.Entitlements);
    }

    [Fact]
    public void A_valid_mitigation_accepts_the_conflict_and_warns_before_it_runs_out()
    {
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "e1", "E1", "Create", "Release"));
        var later = new Mitigation("SOD-01", "E1", "Daily check", "CISO", Build.Today.AddDays(90));
        Assert.Empty(Build.Analyze(s, Build.Policy(later)));

        var soon = later with { Expires = Build.Today.AddDays(10) };
        var f = Assert.Single(Build.Analyze(s, Build.Policy(soon)));
        Assert.Equal((FindingKind.MitigationExpiring, Severity.Medium), (f.Kind, f.Severity));

        var expired = later with { Expires = Build.Today.AddDays(-1) };
        f = Assert.Single(Build.Analyze(s, Build.Policy(expired)));
        Assert.Equal(FindingKind.SodConflict, f.Kind);
        Assert.Contains("mitigation ran out", f.Message);
    }

    [Fact]
    public void An_expired_mitigation_without_a_conflict_is_a_housekeeping_note()
    {
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "e1", "E1", "Create"));
        var f = Assert.Single(Build.Analyze(s, Build.Policy(new Mitigation("SOD-01", "E1", "x", "CISO", Build.Today.AddDays(-3)))));
        Assert.Equal((FindingKind.MitigationExpiring, Severity.Low), (f.Kind, f.Severity));
    }

    [Fact]
    public void A_mover_keeps_old_access_only_during_the_handover()
    {
        var policy = Build.Policy() with
        {
            Baselines = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Payments"] = ["Payments:Create"],
                ["Treasury"] = ["Treasury:Trade"],
            },
        };
        Account[] accounts = [Build.Account("Payments", "e1", "E1", "Create"), Build.Account("Treasury", "e1", "E1", "Trade")];

        var recent = Build.Snapshot([Build.Person("E1", "Treasury", from: "Payments", movedOn: Build.Today.AddDays(-30))], accounts);
        Assert.DoesNotContain(Build.Analyze(recent, policy), f => f.Kind == FindingKind.MoverRetained);

        var old = Build.Snapshot([Build.Person("E1", "Treasury", from: "Payments", movedOn: Build.Today.AddDays(-31))], accounts);
        var f = Assert.Single(Build.Analyze(old, policy));
        Assert.Equal((FindingKind.MoverRetained, Severity.High, "Payments/e1"), (f.Kind, f.Severity, f.AccountKey));
    }

    [Fact]
    public void Access_outside_the_department_baseline_must_be_confirmed()
    {
        var policy = Build.Policy() with { Baselines = new Dictionary<string, IReadOnlyList<string>> { ["payments"] = ["Payments:Create"] } };
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "e1", "E1", "Create", "ViewReports", "Export"));
        var f = Assert.Single(Build.Analyze(s, policy));
        Assert.Equal(FindingKind.OutsideBaseline, f.Kind);
        Assert.Equal(["Payments:ViewReports", "Payments:Export"], f.Entitlements);
        Assert.Contains("are not part of the Payments baseline", f.Message);
    }

    [Fact]
    public void Without_baselines_nothing_is_outside_them()
    {
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "e1", "E1", "Anything"));
        Assert.Empty(Build.Analyze(s));
    }

    [Fact]
    public void Matches_by_email_when_the_employee_id_is_missing_and_says_so()
    {
        var s = Build.Snapshot([Build.Person("E1")],
            Build.Account("CoreBanking", "x", null, "Read") with { Email = "E1@BANK.example" });
        var owned = Analyzer.Match(s).Single();
        Assert.Equal("E1", owned.Owner?.EmployeeId);
        Assert.True(owned.ByEmail);
        Assert.Equal(FindingKind.WeakMatch, Assert.Single(Build.Analyze(s)).Kind);
    }

    [Fact]
    public void An_email_used_by_two_people_is_not_trusted_for_matching()
    {
        var a = Build.Person("E1") with { Email = "team@bank.example" };
        var b = Build.Person("E2") with { Email = "team@bank.example" };
        var s = Build.Snapshot([a, b], Build.Account("CoreBanking", "x", null, "Read") with { Email = "team@bank.example" });
        Assert.Equal(FindingKind.Orphan, Assert.Single(Build.Analyze(s)).Kind);
    }

    [Fact]
    public void Findings_are_sorted_worst_first()
    {
        var s = Build.Snapshot([Build.Person("E1"), Build.Person("E2", left: Build.Today.AddDays(-60))],
            Build.Account("Payments", "e1", "E1", "Create") with { LastLogin = Build.Today.AddDays(-200) },
            Build.Account("Payments", "e2", "E2", "Create"),
            Build.Account("Payments", "e3", "E1", "Approve"));
        var f = Build.Analyze(s);
        Assert.Equal(f.OrderByDescending(x => x.Severity).Select(x => x.Severity), f.Select(x => x.Severity));
        Assert.Equal(Severity.Critical, f[0].Severity);
    }
}
