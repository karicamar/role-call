using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoleCall.Domain.Analysis;
using RoleCall.Domain.Reviews;
using RoleCall.Web.Data;
using RoleCall.Web.Services;
using static RoleCall.Tests.RoleCallApp;

namespace RoleCall.Tests;

/// <summary>The whole review cycle against the real service and a real SQLite database, with a controlled clock.</summary>
public class WorkflowTests : IAsyncLifetime
{
    readonly RoleCallApp app = new();

    public async Task InitializeAsync() => await app.ImportDemoAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    async Task<CampaignRow> Start(int days = 14) => await app.Reviews.StartCampaignAsync(Lena, "Q4 review", app.Today.AddDays(days));

    async Task<List<ReviewItemRow>> Items(Func<ReviewItemRow, bool>? where = null)
    {
        await using var db = app.Db();
        var all = await db.Items.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        return where is null ? all : all.Where(where).ToList();
    }

    static async Task<AccessException> Fails(AccessError error, Func<Task> action)
    {
        var e = await Assert.ThrowsAsync<AccessException>(action);
        Assert.Equal(error, e.Error);
        return e;
    }

    [Fact]
    public async Task Starting_a_campaign_assigns_every_entitlement_to_a_reviewer()
    {
        var c = await Start();
        var items = await Items();
        Assert.Equal(c.Items.Count, items.Count);
        Assert.True(items.Count > 60);
        Assert.DoesNotContain(items, i => i.ReviewerId == i.SubjectId);
        Assert.Equal("E1020", items.First(i => i.SubjectId == "E1021").ReviewerId);
        Assert.Equal("E1001", items.First(i => i.SubjectId == "E1020").ReviewerId);
        Assert.Equal("E1040", items.Single(i => i.AccountKey == "Directory/svc_backup").ReviewerId);
        Assert.Equal("E1002", items.First(i => i.SubjectId == "E1010").ReviewerId);
        Assert.DoesNotContain(items, i => i.AccountKey == "Directory/old-intern");
        Assert.Equal(64, c.BasisHash.Length);
        Assert.Contains((await app.Reviews.AuditTrailAsync(Lena)), a => a.Action == "campaign.start");
    }

    [Fact]
    public async Task Only_one_campaign_runs_at_a_time_and_only_the_IAM_team_starts_it()
    {
        await Fails(AccessError.Forbidden, () => app.Reviews.StartCampaignAsync(Tom, "Mine", app.Today.AddDays(5)));
        await Fails(AccessError.Invalid, () => app.Reviews.StartCampaignAsync(Lena, "Past", app.Today));
        await Start();
        await Fails(AccessError.Conflict, () => Start());
    }

    [Fact]
    public async Task A_campaign_needs_data_and_a_fallback_reviewer()
    {
        await using (var db = app.Db()) await db.Accounts.ExecuteDeleteAsync();
        var e = await Fails(AccessError.Invalid, () => Start());
        Assert.Contains("Import", e.Message);
    }

    [Fact]
    public async Task A_reviewer_decides_their_items_and_stale_pages_are_refused()
    {
        await Start();
        var item = (await Items(i => i.SubjectId == "E1022")).First();
        await app.Reviews.DecideAsync(Tom, item.Id, item.Version, Decision.Keep, null);
        var saved = (await Items(i => i.Id == item.Id)).Single();
        Assert.Equal((Decision.Keep, "E1020", item.Version + 1), (saved.Decision, saved.DecidedBy, saved.Version));

        await Fails(AccessError.Conflict, () => app.Reviews.DecideAsync(Tom, item.Id, item.Version, Decision.Revoke, null));
        await app.Reviews.DecideAsync(Tom, item.Id, saved.Version, Decision.Revoke, "Moved to the other team");
        var audit = await app.Reviews.AuditTrailAsync(Lena);
        Assert.Contains(audit, a => a.Detail.Contains("(was keep)"));
    }

    [Fact]
    public async Task Nobody_can_decide_about_their_own_access_and_the_attempt_is_logged()
    {
        await Start();
        var own = (await Items(i => i.SubjectId == "E1021" && i.Entitlement == "Payments:Approve")).Single();
        var e = await Fails(AccessError.Invalid, () => app.Reviews.DecideAsync(Mia, own.Id, own.Version, Decision.Keep, "I really need this one"));
        Assert.Contains("own access", e.Message);
        Assert.Contains(await app.Reviews.AuditTrailAsync(Anna), a => a.Action == "review.self-review-blocked");
        Assert.Null((await Items(i => i.Id == own.Id)).Single().Decision);
    }

    [Fact]
    public async Task Other_managers_cannot_decide_on_items_they_are_not_assigned()
    {
        await Start();
        var item = (await Items(i => i.ReviewerId == "E1020")).First();
        await Fails(AccessError.Forbidden, () => app.Reviews.DecideAsync(Nina, item.Id, item.Version, Decision.Revoke, null));
        await Fails(AccessError.Forbidden, () => app.Reviews.DecideAsync(Lena, item.Id, item.Version, Decision.Revoke, null));
    }

    [Fact]
    public async Task Keeping_a_segregation_of_duties_conflict_needs_the_compensating_control()
    {
        await Start();
        var sod = (await Items(i => i.SubjectId == "E1021" && i.Entitlement == "Payments:Approve")).Single();
        Assert.Contains(FindingKind.SodConflict, sod.Risks);
        Assert.Equal(RoleCall.Domain.Model.Severity.Critical, sod.Severity);
        await Fails(AccessError.Invalid, () => app.Reviews.DecideAsync(Tom, sod.Id, sod.Version, Decision.Keep, "ok"));
        await app.Reviews.DecideAsync(Tom, sod.Id, sod.Version, Decision.Keep, "Every payment Mia approves is re-checked by Julia the same day");
        Assert.Equal(Decision.Keep, (await Items(i => i.Id == sod.Id)).Single().Decision);
    }

    [Fact]
    public async Task Leaver_access_can_only_be_revoked()
    {
        await Start();
        var leaver = await Items(i => i.SubjectId == "E1032");
        Assert.All(leaver, i => Assert.Contains(FindingKind.LeaverActive, i.Risks));
        Assert.Equal(RoleCall.Domain.Model.Severity.Critical, leaver.Single(i => i.Entitlement == "Directory:VPN").Severity);
        await Fails(AccessError.Invalid, () => app.Reviews.DecideAsync(Nina, leaver[0].Id, leaver[0].Version, Decision.Keep, "Still needed for the handover period"));
        await app.Reviews.DecideAsync(Nina, leaver[0].Id, leaver[0].Version, Decision.Revoke, null);
    }

    [Fact]
    public async Task Bulk_keep_only_touches_items_without_risk()
    {
        await Start();
        var mine = await Items(i => i.ReviewerId == "E1020");
        var n = await app.Reviews.BulkKeepAsync(Tom, mine.Select(i => i.Id).ToList());
        var after = await Items(i => i.ReviewerId == "E1020");
        Assert.Equal(mine.Count(i => i.Risks.Count == 0), n);
        Assert.All(after.Where(i => i.Risks.Count > 0), i => Assert.Null(i.Decision));
        Assert.All(after.Where(i => i.Risks.Count == 0), i => Assert.True(i.Bulk));

        // Someone else's items in the list are skipped, not decided
        var ninas = await Items(i => i.ReviewerId == "E1030" && i.Risks.Count == 0);
        Assert.Equal(0, await app.Reviews.BulkKeepAsync(Tom, ninas.Select(i => i.Id).ToList()));
    }

    [Fact]
    public async Task Overdue_items_are_escalated_to_the_reviewers_manager_never_to_the_subject()
    {
        await Start(days: 7);
        await Fails(AccessError.Invalid, () => app.Reviews.EscalateOverdueAsync(Lena));
        app.Time.Advance(TimeSpan.FromDays(8));
        await Fails(AccessError.Forbidden, () => app.Reviews.EscalateOverdueAsync(Anna));

        var moved = await app.Reviews.EscalateOverdueAsync(Lena);
        var items = await Items();
        Assert.Equal(items.Count(i => i.OriginalReviewerId is not null), moved);
        Assert.All(items.Where(i => i.OriginalReviewerId == "E1030"), i => Assert.Equal("E1001", i.ReviewerId));
        Assert.Equal("E1002", items.Single(i => i.AccountKey == "Directory/cwendt").ReviewerId);
        Assert.DoesNotContain(items, i => i.ReviewerId == i.SubjectId);

        Assert.Equal(0, await app.Reviews.EscalateOverdueAsync(Lena));
    }

    [Fact]
    public async Task A_campaign_closes_only_when_everything_is_decided_and_then_stays_closed()
    {
        var c = await Start();
        await Fails(AccessError.Invalid, () => app.Reviews.CloseCampaignAsync(Lena, c.Id));

        var people = new Dictionary<string, Actor> { ["E1020"] = Tom, ["E1030"] = Nina, ["E1001"] = Clara, ["E1002"] = Jonas, ["E1040"] = new("daniel.it", "E1040", new HashSet<string>()) };
        foreach (var i in await Items())
            await app.Reviews.DecideAsync(people[i.ReviewerId], i.Id, i.Version, Decision.Revoke, null);

        await Fails(AccessError.Forbidden, () => app.Reviews.CloseCampaignAsync(Tom, c.Id));
        await app.Reviews.CloseCampaignAsync(Lena, c.Id);
        await Fails(AccessError.Conflict, () => app.Reviews.CloseCampaignAsync(Lena, c.Id));
        var first = (await Items()).First();
        await Fails(AccessError.Invalid, () => app.Reviews.DecideAsync(people[first.ReviewerId], first.Id, first.Version, Decision.Keep, null));

        // A new campaign can start after the old one is closed
        app.Time.Advance(TimeSpan.FromDays(1));
        await app.Reviews.StartCampaignAsync(Lena, "Next", app.Today.AddDays(20));
    }

    [Fact]
    public async Task The_revocation_list_is_safe_to_open_in_Excel()
    {
        var c = await Start();
        var item = (await Items(i => i.SubjectId == "E1022")).First();
        await app.Reviews.DecideAsync(Tom, item.Id, item.Version, Decision.Revoke, "=HYPERLINK(\"http://evil.example\",\"click\")");
        var file = await app.Reviews.RevocationsCsvAsync(Anna, c.Id);
        var text = Encoding.UTF8.GetString(file.Content);
        Assert.StartsWith("﻿campaign,application,account", text);
        Assert.Contains("\"'=HYPERLINK(", text);
        Assert.DoesNotContain(",=HYPERLINK", text);
        Assert.Equal(2, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        await Fails(AccessError.Forbidden, () => app.Reviews.RevocationsCsvAsync(Tom, c.Id));
    }

    [Fact]
    public async Task The_evidence_file_carries_every_decision_and_its_hash()
    {
        var c = await Start();
        var item = (await Items(i => i.SubjectId == "E1022")).First();
        await app.Reviews.DecideAsync(Tom, item.Id, item.Version, Decision.Keep, null);
        var file = await app.Reviews.EvidenceAsync(Anna, c.Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file.Content)), file.Sha256);
        using var doc = JsonDocument.Parse(file.Content);
        Assert.Equal("rolecall-evidence/1", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(c.BasisHash, doc.RootElement.GetProperty("campaign").GetProperty("basisHash").GetString());
        var decided = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == item.Id);
        Assert.Equal("Keep", decided.GetProperty("decision").GetString());
        Assert.Equal("E1020", decided.GetProperty("decidedBy").GetString());
    }

    [Fact]
    public async Task Clicking_through_a_review_shows_up_as_a_quality_signal()
    {
        var c = await Start();
        foreach (var i in await Items(i => i.ReviewerId == "E1030" && i.Risks.Count == 0))
        {
            app.Time.Advance(TimeSpan.FromSeconds(2));
            await app.Reviews.DecideAsync(Nina, i.Id, i.Version, Decision.Keep, null);
        }
        var signal = Assert.Single(await app.Reviews.RubberStampAsync(Anna, c.Id));
        Assert.Equal("E1030", signal.ReviewerId);
        await Fails(AccessError.Forbidden, () => app.Reviews.RubberStampAsync(Nina, c.Id));
    }

    [Fact]
    public async Task The_audit_trail_detects_tampering()
    {
        await Start();
        Assert.Null(await app.Reviews.VerifyAuditChainAsync());
        await using var db = app.Db();
        var second = await db.Audit.OrderBy(a => a.Id).Skip(1).FirstAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Audit SET Detail = 'nothing to see' WHERE Id = {second.Id}");
        Assert.Equal(second.Id, await app.Reviews.VerifyAuditChainAsync());
    }

    [Fact]
    public async Task Deleting_an_audit_entry_breaks_the_chain_too()
    {
        await using var db = app.Db();
        var second = await db.Audit.OrderBy(a => a.Id).Skip(1).FirstAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Audit WHERE Id = {second.Id}");
        Assert.NotNull(await app.Reviews.VerifyAuditChainAsync());
    }

    [Fact]
    public async Task The_HR_feed_replaces_everyone_and_an_account_export_only_its_application()
    {
        var lines = DemoData.Hr(app.Today).Split('\n').Where(l => !l.Contains("E1036")).ToArray();
        Assert.Equal(19, await app.Reviews.ImportPeopleAsync(Lena, "hr.csv", string.Join('\n', lines)));
        var findings = await app.Reviews.FindingsAsync();
        Assert.Contains(findings, f => f.Kind == FindingKind.Orphan && f.AccountKey == "Treasury/tberger");

        await app.Reviews.ImportAccountsAsync(Lena, "Treasury", "t.csv", "account_id,employee_id,enabled,entitlements\nnkoch,E1030,yes,View\n");
        var snapshot = await app.Reviews.SnapshotAsync();
        Assert.Single(snapshot.Accounts, a => a.App == "Treasury");
        Assert.Equal(10, snapshot.Accounts.Count(a => a.App == "Payments"));
    }

    [Fact]
    public async Task Broken_files_change_nothing_and_say_what_is_wrong()
    {
        var e = await Fails(AccessError.Invalid, () => app.Reviews.ImportPeopleAsync(Lena, "hr.csv", "employee_id,name\nE1,A\n"));
        Assert.Contains("Missing column", e.Message);
        Assert.Equal(20, (await app.Reviews.SnapshotAsync()).People.Count);
        await Fails(AccessError.Invalid, () => app.Reviews.ImportPolicyAsync(Lena, "p.json", "{\"dormantDays\": -1}"));
        await Fails(AccessError.Forbidden, () => app.Reviews.ImportPeopleAsync(Tom, "hr.csv", DemoData.Hr(app.Today)));
        await Fails(AccessError.Forbidden, () => app.Reviews.ImportAccountsAsync(Anna, "X", "x.csv", "account_id,enabled,entitlements\na,yes,b\n"));
    }

    [Fact]
    public async Task Imports_record_the_file_hash_and_strip_path_and_control_characters_from_names()
    {
        await app.Reviews.ImportAccountsAsync(Lena, "Treasury", "..\\..\\evil\u0007.csv", "account_id,enabled,entitlements\na,yes,View\n");
        await using var db = app.Db();
        var last = await db.Imports.OrderByDescending(i => i.Id).FirstAsync();
        Assert.Equal("evil.csv", last.FileName);
        Assert.Equal(64, last.Sha256.Length);
    }

    [Fact]
    public async Task The_overview_counts_progress_per_reviewer_for_oversight_only()
    {
        await Start();
        var forLena = await app.Reviews.OverviewAsync(Lena);
        Assert.NotEmpty(forLena.Progress);
        Assert.Equal(forLena.Stats!.Total, forLena.Progress.Sum(p => p.Total));
        Assert.Empty((await app.Reviews.OverviewAsync(Tom)).Progress);
    }
}
