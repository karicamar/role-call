using RoleCall.Domain.Analysis;
using RoleCall.Domain.Model;

namespace RoleCall.Domain.Reviews;

public enum Decision { Keep, Revoke }

/// <summary>One line a reviewer has to decide: may this person keep this entitlement?</summary>
public sealed record PlannedItem(
    string SubjectId,
    string SubjectName,
    string AccountKey,
    string App,
    string Entitlement,
    bool Privileged,
    string ReviewerId,
    string ReviewerReason,
    IReadOnlyList<FindingKind> Risks,
    Severity Severity,
    string RiskText);

/// <summary>Builds the review items of a campaign and picks a reviewer for each, never the person themselves.</summary>
public static class CampaignPlanner
{
    /// <summary>The account itself is reviewed when it holds no entitlements (an empty account is still a way in).</summary>
    public const string AccountItself = "(account)";

    public static IReadOnlyList<PlannedItem> Plan(Snapshot snapshot, Policy policy, IReadOnlyList<Finding> findings, DateOnly today)
    {
        var items = new List<PlannedItem>();
        foreach (var o in Analyzer.Match(snapshot).Where(o => o.Account.Enabled))
        {
            var a = o.Account;
            var subjectId = o.Owner?.EmployeeId ?? $"?{a.Key}";
            var subjectName = o.Owner?.Name ?? $"unknown owner ({a.AccountId})";
            var (reviewer, why) = Reviewer(snapshot, policy, o.Owner, a.App, today);
            var related = findings.Where(f =>
                    f.AccountKey == a.Key ||
                    (f.AccountKey is null && o.Owner is not null && string.Equals(f.EmployeeId, o.Owner.EmployeeId, StringComparison.OrdinalIgnoreCase)))
                .Where(f => f.Kind != FindingKind.MitigationExpiring || f.Entitlements.Count > 0)
                .ToList();

            var entitlements = a.Entitlements.Count == 0 ? [Entitlement.Qualified(a.App, AccountItself)] : a.Entitlements;
            foreach (var e in entitlements)
            {
                // Account-wide findings (leaver, orphan, dormant) apply to every line; the others only to the entitlements they name
                var risks = related.Where(f => f.Entitlements.Count == 0 || f.Entitlements.Contains(e, StringComparer.OrdinalIgnoreCase)
                                                || f.Kind is FindingKind.LeaverActive or FindingKind.Orphan or FindingKind.Dormant or FindingKind.UnownedPrivileged)
                    .Where(f => f.Kind != FindingKind.WeakMatch || e == entitlements[0])
                    .ToList();
                items.Add(new PlannedItem(subjectId, subjectName, a.Key, a.App, e, a.Privileged, reviewer, why,
                    risks.Select(r => r.Kind).Distinct().ToList(),
                    risks.Count == 0 ? (a.Privileged ? Severity.Medium : Severity.Low) : risks.Max(r => r.Severity),
                    string.Join(" ", risks.Select(r => r.Message).Distinct())));
            }
        }
        return items.OrderBy(i => i.ReviewerId).ThenBy(i => i.SubjectName).ThenBy(i => i.AccountKey).ThenBy(i => i.Entitlement).ToList();
    }

    /// <summary>
    /// The line manager reviews their people. Without an active manager, or for orphaned accounts, the application owner
    /// does. Nobody reviews their own access: then the fallback reviewer (for example the CISO) does.
    /// </summary>
    public static (string ReviewerId, string Reason) Reviewer(Snapshot snapshot, Policy policy, Person? subject, string app, DateOnly today)
    {
        var owner = policy.AppOwners.FirstOrDefault(o => string.Equals(o.Key, app, StringComparison.OrdinalIgnoreCase)).Value;
        (string, string)? candidate = null;
        if (subject is not null && snapshot.Find(subject.ManagerId) is { } manager && !manager.HasLeft(today))
            candidate = (manager.EmployeeId, "line manager");
        else if (owner is { Length: > 0 } && snapshot.Find(owner) is { } o && !o.HasLeft(today))
            candidate = (owner, subject is null ? "application owner (no owner for the account)" : "application owner (no active line manager)");

        if (candidate is { } c && (subject is null || !string.Equals(c.Item1, subject.EmployeeId, StringComparison.OrdinalIgnoreCase)))
            return c;
        return (policy.FallbackReviewer, "fallback reviewer (nobody else may review this)");
    }
}

/// <summary>The rules every decision must pass. Returned as text so the UI can show it as is.</summary>
public static class ReviewRules
{
    public const int MinJustification = 15;

    public static string? Check(string actorId, string reviewerId, string subjectId, IReadOnlyList<FindingKind> risks, Decision decision, string? comment, bool campaignOpen)
    {
        if (!campaignOpen) return "The campaign is closed; decisions can no longer change.";
        if (string.Equals(actorId, subjectId, StringComparison.OrdinalIgnoreCase)) return "Nobody may review their own access.";
        if (!string.Equals(actorId, reviewerId, StringComparison.OrdinalIgnoreCase)) return "This item is assigned to another reviewer.";
        if (decision == Decision.Revoke) return null;

        if (risks.Contains(FindingKind.LeaverActive)) return "The person has left: this access can only be revoked.";
        var text = comment?.Trim() ?? "";
        if (risks.Contains(FindingKind.SodConflict) && text.Length < MinJustification)
            return $"Keeping access that breaks segregation of duties needs the compensating control in the comment (at least {MinJustification} characters).";
        if (risks.Contains(FindingKind.Orphan) || risks.Contains(FindingKind.UnownedPrivileged))
            return text.Length < MinJustification ? $"Keeping an account without an owner needs the accountable person and the reason (at least {MinJustification} characters)." : null;
        if (risks.Contains(FindingKind.MoverRetained) && text.Length < MinJustification)
            return $"Keeping access from the old department needs a reason (at least {MinJustification} characters).";
        return null;
    }

    /// <summary>"Keep all" is only offered for items without any risk: risky access must be looked at one by one.</summary>
    public static bool BulkKeepAllowed(IReadOnlyList<FindingKind> risks) => risks.Count == 0;
}

/// <summary>A decision as recorded, for the rubber-stamping check.</summary>
public sealed record DecisionRecord(string ReviewerId, Decision Decision, DateTimeOffset At, bool Bulk);

public sealed record RubberStampSignal(string ReviewerId, int Decisions, double KeepRate, TimeSpan MedianGap, string Message);

/// <summary>
/// Spots reviews that were clicked through instead of done: many decisions, nearly all "keep", seconds apart.
/// The auditor sees these signals; the campaign is not blocked by them.
/// </summary>
public static class RubberStamp
{
    public static IReadOnlyList<RubberStampSignal> Detect(IEnumerable<DecisionRecord> decisions, int minDecisions = 10, double keepRate = 0.9, double maxMedianSeconds = 4)
    {
        var signals = new List<RubberStampSignal>();
        foreach (var g in decisions.Where(d => !d.Bulk).GroupBy(d => d.ReviewerId, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.OrderBy(d => d.At).ToList();
            if (list.Count < minDecisions) continue;
            var keep = list.Count(d => d.Decision == Decision.Keep) / (double)list.Count;
            var gaps = list.Zip(list.Skip(1), (x, y) => (y.At - x.At).TotalSeconds).Order().ToList();
            var median = gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2;
            if (keep >= keepRate && median <= maxMedianSeconds)
                signals.Add(new RubberStampSignal(g.Key, list.Count, keep, TimeSpan.FromSeconds(median),
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{list.Count} decisions, {keep * 100:0} % keep, median {median:0.#} s between decisions: the review may not have been done properly.")));
        }
        return signals;
    }
}
