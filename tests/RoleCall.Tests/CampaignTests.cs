using RoleCall.Domain.Analysis;
using RoleCall.Domain.Model;
using RoleCall.Domain.Reviews;

namespace RoleCall.Tests;

public class ReviewerTests
{
    static (string Id, string Reason) Reviewer(Snapshot s, Person? subject, string app = "Payments") =>
        CampaignPlanner.Reviewer(s, Build.Policy(), subject, app, Build.Today);

    [Fact]
    public void The_line_manager_reviews_their_people()
    {
        var e1 = Build.Person("E1");
        Assert.Equal(("M1", "line manager"), Reviewer(Build.Snapshot([e1]), e1));
    }

    [Fact]
    public void Without_a_manager_or_with_a_manager_who_left_the_application_owner_reviews()
    {
        var top = Build.Person("E1", manager: null);
        Assert.Equal("OWN", Reviewer(Build.Snapshot([top]), top).Id);

        var gone = Build.Person("M2", manager: null, left: Build.Today.AddDays(-3));
        var e2 = Build.Person("E2", manager: "M2");
        var r = Reviewer(Build.Snapshot([gone, e2]), e2);
        Assert.Equal("OWN", r.Id);
        Assert.Contains("no active line manager", r.Reason);
    }

    [Fact]
    public void Orphaned_accounts_go_to_the_application_owner()
    {
        var r = Reviewer(Build.Snapshot([]), null, "Treasury");
        Assert.Equal("OWN2", r.Id);
        Assert.Contains("no owner for the account", r.Reason);
    }

    [Fact]
    public void Nobody_reviews_their_own_access_not_even_the_application_owner()
    {
        var r = Reviewer(Build.Snapshot([]), Build.Owner, "Payments");
        Assert.Equal("CISO", r.Id);
        Assert.Contains("fallback", r.Reason);
    }

    [Fact]
    public void An_application_without_an_owner_falls_back()
    {
        var top = Build.Person("E1", manager: null);
        Assert.Equal("CISO", Reviewer(Build.Snapshot([top]), top, "Unknown").Id);
    }
}

public class PlannerTests
{
    [Fact]
    public void One_item_per_entitlement_of_every_enabled_account()
    {
        var s = Build.Snapshot([Build.Person("E1")],
            Build.Account("Payments", "e1", "E1", "Create", "ViewReports"),
            Build.Account("Treasury", "e1", "E1") ,
            Build.Account("Directory", "e1", "E1", "User") with { Enabled = false });
        var items = CampaignPlanner.Plan(s, Build.Policy(), Build.Analyze(s), Build.Today);
        Assert.Equal(["Payments:Create", "Payments:ViewReports", "Treasury:(account)"], items.Select(i => i.Entitlement).Order());
        Assert.All(items, i => Assert.Equal("M1", i.ReviewerId));
        Assert.All(items, i => Assert.Equal(Severity.Low, i.Severity));
    }

    [Fact]
    public void Risks_are_attached_to_the_entitlements_they_concern()
    {
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "e1", "E1", "Create", "Approve", "ViewReports"));
        var items = CampaignPlanner.Plan(s, Build.Policy(), Build.Analyze(s), Build.Today).ToDictionary(i => i.Entitlement);
        Assert.Equal([FindingKind.SodConflict], items["Payments:Create"].Risks);
        Assert.Equal([FindingKind.SodConflict], items["Payments:Approve"].Risks);
        Assert.Equal(Severity.Critical, items["Payments:Approve"].Severity);
        Assert.Empty(items["Payments:ViewReports"].Risks);
        Assert.Contains("four", items["Payments:Create"].RiskText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Account_wide_findings_apply_to_every_line()
    {
        var s = Build.Snapshot([Build.Person("E1", left: Build.Today.AddDays(-10))], Build.Account("Payments", "e1", "E1", "Create", "ViewReports"));
        var items = CampaignPlanner.Plan(s, Build.Policy(), Build.Analyze(s), Build.Today);
        Assert.All(items, i => Assert.Contains(FindingKind.LeaverActive, i.Risks));
    }

    [Fact]
    public void Orphaned_accounts_are_reviewed_under_their_account_name()
    {
        var s = Build.Snapshot([], Build.Account("Payments", "ghost", "E999", "Create"));
        var item = Assert.Single(CampaignPlanner.Plan(s, Build.Policy(), Build.Analyze(s), Build.Today));
        Assert.Equal("?Payments/ghost", item.SubjectId);
        Assert.Equal("OWN", item.ReviewerId);
        Assert.Equal([FindingKind.Orphan], item.Risks);
    }

    [Fact]
    public void A_privileged_entitlement_without_findings_is_still_medium()
    {
        var s = Build.Snapshot([Build.Person("E1")], Build.Account("Payments", "adm", "E1", "AdminUsers") with { Privileged = true });
        Assert.Equal(Severity.Medium, Assert.Single(CampaignPlanner.Plan(s, Build.Policy(), Build.Analyze(s), Build.Today)).Severity);
    }
}

public class ReviewRuleTests
{
    static string? Check(string actor, IReadOnlyList<FindingKind> risks, Decision d, string? comment = null, string reviewer = "M1", string subject = "E1", bool open = true) =>
        ReviewRules.Check(actor, reviewer, subject, risks, d, comment, open);

    [Fact]
    public void The_assigned_reviewer_may_keep_or_revoke_plain_access() =>
        Assert.All(new[] { Decision.Keep, Decision.Revoke }, d => Assert.Null(Check("M1", [], d)));

    [Fact]
    public void Self_review_is_refused_even_when_assigned() =>
        Assert.Equal("Nobody may review their own access.", Check("e1", [], Decision.Keep, reviewer: "E1"));

    [Fact]
    public void Only_the_assigned_reviewer_decides() =>
        Assert.Contains("another reviewer", Check("M2", [], Decision.Revoke));

    [Fact]
    public void A_closed_campaign_cannot_change() =>
        Assert.Contains("closed", Check("M1", [], Decision.Revoke, open: false));

    [Fact]
    public void Leaver_access_can_only_be_revoked()
    {
        Assert.Contains("only be revoked", Check("M1", [FindingKind.LeaverActive], Decision.Keep, "I am sure this is needed"));
        Assert.Null(Check("M1", [FindingKind.LeaverActive], Decision.Revoke));
    }

    [Theory]
    [InlineData(FindingKind.SodConflict)]
    [InlineData(FindingKind.Orphan)]
    [InlineData(FindingKind.UnownedPrivileged)]
    [InlineData(FindingKind.MoverRetained)]
    public void Keeping_risky_access_needs_a_real_justification(FindingKind risk)
    {
        Assert.NotNull(Check("M1", [risk], Decision.Keep));
        Assert.NotNull(Check("M1", [risk], Decision.Keep, "   ok, needed   "));
        Assert.Null(Check("M1", [risk], Decision.Keep, "Head of Payments checks every release daily"));
    }

    [Fact]
    public void Low_risks_need_no_comment()
    {
        Assert.Null(Check("M1", [FindingKind.Dormant, FindingKind.OutsideBaseline], Decision.Keep));
        Assert.True(ReviewRules.BulkKeepAllowed([]));
        Assert.False(ReviewRules.BulkKeepAllowed([FindingKind.OutsideBaseline]));
    }
}

public class RubberStampTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    static IEnumerable<DecisionRecord> Clicks(string reviewer, int n, double seconds, Decision d = Decision.Keep, bool bulk = false) =>
        Enumerable.Range(0, n).Select(i => new DecisionRecord(reviewer, d, Start.AddSeconds(i * seconds), bulk));

    [Fact]
    public void Many_keeps_seconds_apart_are_flagged()
    {
        var s = Assert.Single(RubberStamp.Detect(Clicks("M1", 20, 2)));
        Assert.Equal("M1", s.ReviewerId);
        Assert.Equal(20, s.Decisions);
        Assert.Equal(TimeSpan.FromSeconds(2), s.MedianGap);
        Assert.Contains("100 % keep", s.Message);
    }

    [Fact]
    public void Careful_reviews_revocations_and_small_reviews_are_not()
    {
        Assert.Empty(RubberStamp.Detect(Clicks("M1", 20, 30)));
        Assert.Empty(RubberStamp.Detect(Clicks("M1", 10, 2).Concat(Clicks("M1", 10, 2, Decision.Revoke).Select(d => d with { At = d.At.AddMinutes(1) }))));
        Assert.Empty(RubberStamp.Detect(Clicks("M1", 9, 1)));
    }

    [Fact]
    public void Bulk_keeps_of_low_risk_items_do_not_count()
    {
        Assert.Empty(RubberStamp.Detect(Clicks("M1", 50, 0, bulk: true)));
    }

    [Fact]
    public void Each_reviewer_is_judged_alone()
    {
        var signals = RubberStamp.Detect(Clicks("M1", 12, 1).Concat(Clicks("M2", 12, 60)));
        Assert.Equal("M1", Assert.Single(signals).ReviewerId);
    }
}
