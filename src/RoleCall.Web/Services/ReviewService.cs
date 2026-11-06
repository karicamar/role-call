using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoleCall.Domain.Analysis;
using RoleCall.Domain.Import;
using RoleCall.Domain.Model;
using RoleCall.Domain.Reviews;
using RoleCall.Web.Data;

namespace RoleCall.Web.Services;

public sealed record CampaignStats(int Total, int Decided, int Kept, int Revoked, int Overdue, int Risky, int RiskyOpen)
{
    public int Open => Total - Decided;
    public int Percent => Total == 0 ? 100 : Decided * 100 / Total;
}

public sealed record ReviewerProgress(string ReviewerId, string ReviewerName, int Total, int Decided, int Escalated);

public sealed record Overview(
    IReadOnlyList<Finding> Findings,
    int People,
    int Accounts,
    IReadOnlyList<string> Apps,
    CampaignRow? Campaign,
    CampaignStats? Stats,
    IReadOnlyList<ReviewerProgress> Progress,
    IReadOnlyList<ImportRow> LastImports);

public sealed record Evidence(string FileName, byte[] Content, string Sha256);

/// <summary>
/// Everything that changes data goes through here: imports, campaigns, decisions. Permissions are checked here as well
/// as on the pages, so a hand-crafted request gets no further than a click in the UI.
/// </summary>
public sealed class ReviewService(IDbContextFactory<RoleCallDb> dbs, Clock clock)
{
    // ---------- reading ----------

    public async Task<Policy> PolicyAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Policies.AsNoTracking().OrderByDescending(p => p.Id).FirstOrDefaultAsync();
        return row is null ? new Policy() : PolicyFile.Parse(row.Json);
    }

    public async Task<Snapshot> SnapshotAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await SnapshotAsync(db);
    }

    static async Task<Snapshot> SnapshotAsync(RoleCallDb db)
    {
        var people = await db.People.AsNoTracking().OrderBy(p => p.EmployeeId).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.App).ThenBy(a => a.AccountId).ToListAsync();
        return new Snapshot(people.Select(p => p.ToModel()).ToList(), accounts.Select(a => a.ToModel()).ToList());
    }

    public async Task<IReadOnlyList<Finding>> FindingsAsync() =>
        Analyzer.Analyze(await SnapshotAsync(), await PolicyAsync(), clock.Today);

    public async Task<Overview> OverviewAsync(Actor actor)
    {
        var snapshot = await SnapshotAsync();
        var findings = Analyzer.Analyze(snapshot, await PolicyAsync(), clock.Today);
        await using var db = await dbs.CreateDbContextAsync();
        var campaign = await db.Campaigns.AsNoTracking().OrderByDescending(c => c.Id).FirstOrDefaultAsync();
        CampaignStats? stats = null;
        IReadOnlyList<ReviewerProgress> progress = [];
        if (campaign is not null)
        {
            var items = await db.Items.AsNoTracking().Where(i => i.CampaignId == campaign.Id).ToListAsync();
            stats = Stats(items, campaign);
            if (actor.IsIam || actor.IsAuditor)
                progress = items.GroupBy(i => i.ReviewerId)
                    .Select(g => new ReviewerProgress(g.Key, snapshot.Find(g.Key)?.Name ?? g.Key, g.Count(), g.Count(i => i.Decision is not null), g.Count(i => i.OriginalReviewerId is not null)))
                    .OrderByDescending(p => p.Total - p.Decided).ThenBy(p => p.ReviewerName).ToList();
        }
        var imports = await db.Imports.AsNoTracking().OrderByDescending(i => i.Id).Take(8).ToListAsync();
        return new Overview(findings, snapshot.People.Count, snapshot.Accounts.Count,
            snapshot.Accounts.Select(a => a.App).Distinct().Order().ToList(), campaign, stats, progress, imports);
    }

    CampaignStats Stats(IReadOnlyCollection<ReviewItemRow> items, CampaignRow campaign) => new(
        items.Count,
        items.Count(i => i.Decision is not null),
        items.Count(i => i.Decision == Decision.Keep),
        items.Count(i => i.Decision == Decision.Revoke),
        campaign.IsOpen && clock.Today > campaign.DueOn ? items.Count(i => i.Decision is null) : 0,
        items.Count(i => i.Risks.Count > 0),
        items.Count(i => i.Risks.Count > 0 && i.Decision is null));

    public async Task<CampaignRow?> OpenCampaignAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Campaigns.AsNoTracking().Where(c => c.ClosedAt == null).OrderByDescending(c => c.Id).FirstOrDefaultAsync();
    }

    public async Task<(CampaignRow Campaign, IReadOnlyList<ReviewItemRow> Items, CampaignStats Stats)> CampaignAsync(Actor actor, int id)
    {
        if (!actor.IsIam && !actor.IsAuditor) throw new AccessException(AccessError.Forbidden, "Only the IAM team and internal audit see the whole campaign.");
        await using var db = await dbs.CreateDbContextAsync();
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id)
                       ?? throw new AccessException(AccessError.NotFound, $"Campaign {id} does not exist.");
        var items = await db.Items.AsNoTracking().Where(i => i.CampaignId == id)
            .OrderByDescending(i => i.Severity).ThenBy(i => i.SubjectName).ThenBy(i => i.Entitlement).ToListAsync();
        return (campaign, items, Stats(items, campaign));
    }

    public async Task<IReadOnlyList<CampaignRow>> CampaignsAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Campaigns.AsNoTracking().OrderByDescending(c => c.Id).ToListAsync();
    }

    /// <summary>The items of the open campaign the signed-in person has to decide (or has decided).</summary>
    public async Task<(CampaignRow? Campaign, IReadOnlyList<ReviewItemRow> Items)> MyItemsAsync(Actor actor)
    {
        if (actor.EmployeeId is null) return (null, []);
        await using var db = await dbs.CreateDbContextAsync();
        var campaign = await db.Campaigns.AsNoTracking().Where(c => c.ClosedAt == null).OrderByDescending(c => c.Id).FirstOrDefaultAsync();
        if (campaign is null) return (null, []);
        var items = await db.Items.AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.ReviewerId == actor.EmployeeId)
            .OrderBy(i => i.Decision != null).ThenByDescending(i => i.Severity).ThenBy(i => i.SubjectName).ThenBy(i => i.Entitlement)
            .ToListAsync();
        return (campaign, items);
    }

    public async Task<IReadOnlyList<RubberStampSignal>> RubberStampAsync(Actor actor, int campaignId)
    {
        if (!actor.IsIam && !actor.IsAuditor) throw new AccessException(AccessError.Forbidden, "Only the IAM team and internal audit see review quality signals.");
        await using var db = await dbs.CreateDbContextAsync();
        var decided = await db.Items.AsNoTracking().Where(i => i.CampaignId == campaignId && i.Decision != null).ToListAsync();
        return RubberStamp.Detect(decided.Select(i => new DecisionRecord(i.DecidedBy!, i.Decision!.Value, i.DecidedAt!.Value, i.Bulk)));
    }

    // ---------- imports ----------

    public async Task<int> ImportPeopleAsync(Actor actor, string fileName, string csv)
    {
        RequireIam(actor);
        var people = Wrap(() => Importers.People(csv));
        await using var db = await dbs.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        // The HR feed is always complete: whoever is missing from it is no longer employed
        await db.People.ExecuteDeleteAsync();
        db.People.AddRange(people.Select(PersonRow.From));
        db.Imports.Add(new ImportRow { Source = "HR", FileName = Clean(fileName), Sha256 = Sha256(csv), Rows = people.Count, ImportedBy = actor.Name, At = clock.Now });
        await db.SaveChangesAsync();
        await AuditAsync(db, actor, "import.hr", $"{people.Count} people from {Clean(fileName)} (sha256 {Sha256(csv)[..12]})");
        await tx.CommitAsync();
        return people.Count;
    }

    public async Task<int> ImportAccountsAsync(Actor actor, string app, string fileName, string csv)
    {
        RequireIam(actor);
        var accounts = Wrap(() => Importers.Accounts(app, csv));
        app = app.Trim();
        await using var db = await dbs.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Accounts.Where(a => a.App == app).ExecuteDeleteAsync();
        db.Accounts.AddRange(accounts.Select(AccountRow.From));
        db.Imports.Add(new ImportRow { Source = app, FileName = Clean(fileName), Sha256 = Sha256(csv), Rows = accounts.Count, ImportedBy = actor.Name, At = clock.Now });
        await db.SaveChangesAsync();
        await AuditAsync(db, actor, "import.accounts", $"{accounts.Count} accounts of {app} from {Clean(fileName)} (sha256 {Sha256(csv)[..12]})");
        await tx.CommitAsync();
        return accounts.Count;
    }

    public async Task ImportPolicyAsync(Actor actor, string fileName, string json)
    {
        RequireIam(actor);
        var policy = Wrap(() => PolicyFile.Parse(json));
        await using var db = await dbs.CreateDbContextAsync();
        db.Policies.Add(new PolicyRow { Json = PolicyFile.Write(policy), UploadedBy = actor.Name, At = clock.Now });
        await db.SaveChangesAsync();
        await AuditAsync(db, actor, "import.policy", $"{policy.SodRules.Count} SoD rules, {policy.Mitigations.Count} mitigations, {policy.Baselines.Count} baselines from {Clean(fileName)}");
    }

    // ---------- campaign ----------

    public async Task<CampaignRow> StartCampaignAsync(Actor actor, string name, DateOnly dueOn)
    {
        RequireIam(actor);
        name = name.Trim();
        if (name.Length is < 3 or > 100) throw new AccessException(AccessError.Invalid, "The name must be 3 to 100 characters.");
        if (dueOn <= clock.Today) throw new AccessException(AccessError.Invalid, "The due date must be in the future.");
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Campaigns.AnyAsync(c => c.ClosedAt == null))
            throw new AccessException(AccessError.Conflict, "A campaign is already open. Close it first.");

        var snapshot = await SnapshotAsync(db);
        if (snapshot.People.Count == 0 || snapshot.Accounts.Count == 0)
            throw new AccessException(AccessError.Invalid, "Import the HR feed and at least one application first.");
        var policyRow = await db.Policies.AsNoTracking().OrderByDescending(p => p.Id).FirstOrDefaultAsync();
        var policy = policyRow is null ? new Policy() : PolicyFile.Parse(policyRow.Json);
        if (string.IsNullOrEmpty(policy.FallbackReviewer) || snapshot.Find(policy.FallbackReviewer) is null)
            throw new AccessException(AccessError.Invalid, "The policy needs a fallback reviewer who is in the HR feed.");

        var findings = Analyzer.Analyze(snapshot, policy, clock.Today);
        var plan = CampaignPlanner.Plan(snapshot, policy, findings, clock.Today);
        var policyJson = PolicyFile.Write(policy);
        var campaign = new CampaignRow
        {
            Name = name, StartedAt = clock.Now, StartedBy = actor.Name, DueOn = dueOn, PolicyJson = policyJson,
            BasisHash = Sha256(JsonSerializer.Serialize(new { snapshot.People, snapshot.Accounts }, PolicyFile.Json) + policyJson),
            Items = plan.Select(p => new ReviewItemRow
            {
                SubjectId = p.SubjectId, SubjectName = p.SubjectName, AccountKey = p.AccountKey, App = p.App, Entitlement = p.Entitlement,
                Privileged = p.Privileged, ReviewerId = p.ReviewerId, ReviewerReason = p.ReviewerReason, Risks = [.. p.Risks],
                Severity = p.Severity, RiskText = p.RiskText,
            }).ToList(),
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();
        await AuditAsync(db, actor, "campaign.start", $"#{campaign.Id} \"{name}\": {plan.Count} items, {plan.Select(p => p.ReviewerId).Distinct().Count()} reviewers, due {dueOn:yyyy-MM-dd}");
        return campaign;
    }

    public async Task DecideAsync(Actor actor, int itemId, int version, Decision decision, string? comment)
    {
        if (actor.EmployeeId is null) throw new AccessException(AccessError.Forbidden, "Your sign-in is not linked to an employee id.");
        comment = comment?.Trim();
        if (comment is { Length: > 1000 }) throw new AccessException(AccessError.Invalid, "The comment is longer than 1000 characters.");
        await using var db = await dbs.CreateDbContextAsync();
        var item = await db.Items.Include(i => i.Campaign).SingleOrDefaultAsync(i => i.Id == itemId)
                   ?? throw new AccessException(AccessError.NotFound, $"Item {itemId} does not exist.");
        var error = ReviewRules.Check(actor.EmployeeId, item.ReviewerId, item.SubjectId, item.Risks, decision, comment, item.Campaign!.IsOpen);
        if (error is not null)
        {
            if (string.Equals(actor.EmployeeId, item.SubjectId, StringComparison.OrdinalIgnoreCase))
                await AuditAsync(db, actor, "review.self-review-blocked", $"item {item.Id}: {item.SubjectName} tried to decide on their own {item.Entitlement}");
            throw new AccessException(error.Contains("assigned to another") ? AccessError.Forbidden : AccessError.Invalid, error);
        }
        if (item.Version != version) throw new AccessException(AccessError.Conflict, "Someone changed this item in the meantime. Reload the page.");

        var previous = item.Decision;
        Apply(item, actor, decision, comment, bulk: false);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { throw new AccessException(AccessError.Conflict, "Someone changed this item in the meantime. Reload the page."); }
        await AuditAsync(db, actor, "review.decide",
            $"item {item.Id}: {decision.ToString().ToLowerInvariant()} {item.SubjectName} {item.Entitlement}" +
            (previous is { } p && p != decision ? $" (was {p.ToString().ToLowerInvariant()})" : "") +
            (comment is { Length: > 0 } ? $": {comment}" : ""));
    }

    /// <summary>Keeps all given items in one go, but only those without any risk: risky access is decided one by one.</summary>
    public async Task<int> BulkKeepAsync(Actor actor, IReadOnlyCollection<int> itemIds)
    {
        if (actor.EmployeeId is null) throw new AccessException(AccessError.Forbidden, "Your sign-in is not linked to an employee id.");
        await using var db = await dbs.CreateDbContextAsync();
        var items = await db.Items.Include(i => i.Campaign).Where(i => itemIds.Contains(i.Id) && i.Decision == null).ToListAsync();
        var ok = items.Where(i => ReviewRules.BulkKeepAllowed(i.Risks) &&
                                  ReviewRules.Check(actor.EmployeeId, i.ReviewerId, i.SubjectId, i.Risks, Decision.Keep, null, i.Campaign!.IsOpen) is null).ToList();
        foreach (var item in ok) Apply(item, actor, Decision.Keep, null, bulk: true);
        await db.SaveChangesAsync();
        if (ok.Count > 0) await AuditAsync(db, actor, "review.bulk-keep", $"{ok.Count} low-risk items: {string.Join(", ", ok.Select(i => i.Id))}");
        return ok.Count;
    }

    void Apply(ReviewItemRow item, Actor actor, Decision decision, string? comment, bool bulk)
    {
        item.Decision = decision;
        item.DecidedBy = actor.EmployeeId;
        item.DecidedAt = clock.Now;
        item.Comment = comment is { Length: > 0 } ? comment : null;
        item.Bulk = bulk;
        item.Version++;
    }

    /// <summary>After the due date, open items move to the reviewer's own manager (or the fallback reviewer).</summary>
    public async Task<int> EscalateOverdueAsync(Actor actor)
    {
        RequireIam(actor);
        await using var db = await dbs.CreateDbContextAsync();
        var campaign = await db.Campaigns.Where(c => c.ClosedAt == null).OrderByDescending(c => c.Id).FirstOrDefaultAsync()
                       ?? throw new AccessException(AccessError.Invalid, "There is no open campaign.");
        if (clock.Today <= campaign.DueOn) throw new AccessException(AccessError.Invalid, $"The campaign is due on {campaign.DueOn:dd.MM.yyyy}; nothing is overdue yet.");
        var policy = PolicyFile.Parse(campaign.PolicyJson);
        var snapshot = await SnapshotAsync(db);
        var open = await db.Items.Where(i => i.CampaignId == campaign.Id && i.Decision == null && i.OriginalReviewerId == null).ToListAsync();
        var moved = 0;
        foreach (var item in open)
        {
            var reviewer = snapshot.Find(item.ReviewerId);
            var target = reviewer is not null && snapshot.Find(reviewer.ManagerId) is { } boss && !boss.HasLeft(clock.Today) ? boss.EmployeeId : policy.FallbackReviewer;
            if (string.Equals(target, item.SubjectId, StringComparison.OrdinalIgnoreCase) || string.Equals(target, item.ReviewerId, StringComparison.OrdinalIgnoreCase))
                target = policy.FallbackReviewer;
            if (string.Equals(target, item.ReviewerId, StringComparison.OrdinalIgnoreCase) || string.Equals(target, item.SubjectId, StringComparison.OrdinalIgnoreCase)) continue;
            item.OriginalReviewerId = item.ReviewerId;
            item.ReviewerId = target;
            item.ReviewerReason = $"escalated: overdue with {reviewer?.Name ?? item.OriginalReviewerId}";
            item.Version++;
            moved++;
        }
        await db.SaveChangesAsync();
        if (moved > 0) await AuditAsync(db, actor, "campaign.escalate", $"#{campaign.Id}: {moved} overdue items escalated");
        return moved;
    }

    public async Task CloseCampaignAsync(Actor actor, int id)
    {
        RequireIam(actor);
        await using var db = await dbs.CreateDbContextAsync();
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id) ?? throw new AccessException(AccessError.NotFound, $"Campaign {id} does not exist.");
        if (!campaign.IsOpen) throw new AccessException(AccessError.Conflict, "The campaign is already closed.");
        var open = await db.Items.CountAsync(i => i.CampaignId == id && i.Decision == null);
        if (open > 0) throw new AccessException(AccessError.Invalid, $"{open} items are still open. Escalate them or wait for the reviewers.");
        campaign.ClosedAt = clock.Now;
        campaign.ClosedBy = actor.Name;
        await db.SaveChangesAsync();
        var revoked = await db.Items.CountAsync(i => i.CampaignId == id && i.Decision == Decision.Revoke);
        await AuditAsync(db, actor, "campaign.close", $"#{id}: closed, {revoked} entitlements to revoke");
    }

    // ---------- exports ----------

    /// <summary>The work list for the application administrators: every entitlement to remove, safe to open in Excel.</summary>
    public async Task<Evidence> RevocationsCsvAsync(Actor actor, int id)
    {
        var (campaign, items, _) = await CampaignAsync(actor, id);
        var sb = new StringBuilder();
        sb.Append(Csv.Line("campaign", "application", "account", "entitlement", "person", "employee_id", "decided_by", "decided_at", "reason")).Append("\r\n");
        foreach (var i in items.Where(i => i.Decision == Decision.Revoke).OrderBy(i => i.App).ThenBy(i => i.AccountKey).ThenBy(i => i.Entitlement))
            sb.Append(Csv.Line(campaign.Id.ToString(), i.App, i.AccountKey, Entitlement.Split(i.Entitlement).Name, i.SubjectName, i.SubjectId,
                i.DecidedBy, i.DecidedAt?.ToString("O"), i.Comment ?? i.RiskText)).Append("\r\n");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        await using var db = await dbs.CreateDbContextAsync();
        await AuditAsync(db, actor, "export.revocations", $"#{id}: {items.Count(i => i.Decision == Decision.Revoke)} lines");
        return new Evidence($"rolecall-{id}-revocations.csv", bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>The evidence for the auditor: basis, every decision and who made it, with a SHA-256 over the content.</summary>
    public async Task<Evidence> EvidenceAsync(Actor actor, int id)
    {
        var (campaign, items, stats) = await CampaignAsync(actor, id);
        var rubber = await RubberStampAsync(actor, id);
        var body = new
        {
            format = "rolecall-evidence/1",
            campaign = new { campaign.Id, campaign.Name, campaign.StartedAt, campaign.StartedBy, campaign.DueOn, campaign.ClosedAt, campaign.ClosedBy, campaign.BasisHash },
            stats,
            reviewQualitySignals = rubber,
            items = items.Select(i => new
            {
                i.Id, i.SubjectId, i.SubjectName, i.AccountKey, i.Entitlement, i.Privileged, i.ReviewerId, i.ReviewerReason, i.OriginalReviewerId,
                risks = i.Risks.Select(r => r.ToString()), severity = i.Severity.ToString(), i.RiskText,
                decision = i.Decision?.ToString(), i.DecidedBy, i.DecidedAt, i.Comment, i.Bulk,
            }),
        };
        var json = JsonSerializer.Serialize(body, PolicyFile.Json);
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var db = await dbs.CreateDbContextAsync();
        await AuditAsync(db, actor, "export.evidence", $"#{id}");
        return new Evidence($"rolecall-{id}-evidence.json", bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    // ---------- audit ----------

    public async Task<IReadOnlyList<AuditRow>> AuditTrailAsync(Actor actor, int take = 300)
    {
        if (!actor.IsIam && !actor.IsAuditor) throw new AccessException(AccessError.Forbidden, "Only the IAM team and internal audit read the audit trail.");
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync();
    }

    /// <summary>Recomputes the hash chain. Returns the id of the first entry that does not match, or null if all are intact.</summary>
    public async Task<int?> VerifyAuditChainAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var previous = "";
        foreach (var row in await db.Audit.AsNoTracking().OrderBy(a => a.Id).ToListAsync())
        {
            if (row.PreviousHash != previous || row.Hash != ChainHash(previous, row.At, row.Actor, row.Action, row.Detail)) return row.Id;
            previous = row.Hash;
        }
        return null;
    }

    async Task AuditAsync(RoleCallDb db, Actor actor, string action, string detail)
    {
        var previous = await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Select(a => a.Hash).FirstOrDefaultAsync() ?? "";
        var at = clock.Now;
        db.Audit.Add(new AuditRow
        {
            At = at, Actor = actor.Name, Action = action, Detail = detail,
            PreviousHash = previous, Hash = ChainHash(previous, at, actor.Name, action, detail),
        });
        await db.SaveChangesAsync();
    }

    static string ChainHash(string previous, DateTimeOffset at, string actor, string action, string detail) =>
        Sha256(string.Join("\u001f", previous, at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), actor, action, detail));

    // ---------- helpers ----------

    static void RequireIam(Actor actor)
    {
        if (!actor.IsIam) throw new AccessException(AccessError.Forbidden, "Only the IAM team may do this.");
    }

    static T Wrap<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (ImportException e) { throw new AccessException(AccessError.Invalid, e.Message); }
    }

    static string Clean(string fileName)
    {
        // Both separators: a file name from a Windows browser keeps its backslashes on a Linux server
        var name = fileName ?? "";
        name = name[(name.LastIndexOfAny([(char)47, (char)92]) + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).Take(120).ToArray());
        return name.Length == 0 ? "upload" : name;
    }

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
