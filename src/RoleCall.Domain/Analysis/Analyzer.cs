using RoleCall.Domain.Model;

namespace RoleCall.Domain.Analysis;

public enum FindingKind
{
    /// <summary>The account belongs to nobody in the HR feed.</summary>
    Orphan,
    /// <summary>The owner has left, the account is still enabled.</summary>
    LeaverActive,
    /// <summary>Enabled, but not used for longer than the policy allows.</summary>
    Dormant,
    /// <summary>One person holds both sides of a segregation-of-duties rule.</summary>
    SodConflict,
    /// <summary>A mover still holds the access of the old department after the handover period.</summary>
    MoverRetained,
    /// <summary>Access outside the baseline of the department (needs a reason in the review).</summary>
    OutsideBaseline,
    /// <summary>A privileged account no person is accountable for (shared or generic admin).</summary>
    UnownedPrivileged,
    /// <summary>A mitigation for an SoD conflict has run out or runs out soon.</summary>
    MitigationExpiring,
    /// <summary>The account was matched to the person by email only, the employee id is missing or wrong.</summary>
    WeakMatch,
}

public sealed record Finding(
    FindingKind Kind,
    Severity Severity,
    string? EmployeeId,
    string? AccountKey,
    string Message,
    IReadOnlyList<string> Entitlements)
{
    public string? RuleId { get; init; }
}

/// <summary>Where an account belongs: the matched person (if any) and how sure the match is.</summary>
public sealed record Ownership(Account Account, Person? Owner, bool ByEmail);

/// <summary>
/// Compares what people may have (HR feed, policy) with what they actually have (account exports). Each finding says
/// what is wrong and why, in words a manager understands.
/// </summary>
public static class Analyzer
{
    public static IReadOnlyList<Ownership> Match(Snapshot snapshot)
    {
        var byId = snapshot.People.ToDictionary(p => p.EmployeeId, StringComparer.OrdinalIgnoreCase);
        var byEmail = snapshot.People.GroupBy(p => p.Email, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        return snapshot.Accounts.Select(a =>
        {
            if (a.EmployeeId is { } id && byId.TryGetValue(id, out var p)) return new Ownership(a, p, false);
            if (a.Email is { } mail && byEmail.TryGetValue(mail, out var q)) return new Ownership(a, q, true);
            return new Ownership(a, null, false);
        }).ToList();
    }

    public static IReadOnlyList<Finding> Analyze(Snapshot snapshot, Policy policy, DateOnly today)
    {
        var findings = new List<Finding>();
        var owned = Match(snapshot);

        foreach (var o in owned)
        {
            var a = o.Account;
            if (o.Owner is null)
            {
                if (!a.Enabled) continue;
                findings.Add(a.Privileged
                    ? new Finding(FindingKind.UnownedPrivileged, Severity.Critical, null, a.Key,
                        $"Privileged account {a.Key} has no owner in the HR feed. Shared admin accounts must be assigned to a person or replaced by personal ones.", a.Entitlements)
                    : new Finding(FindingKind.Orphan, Severity.High, null, a.Key,
                        $"Account {a.Key} matches nobody in the HR feed" + (a.EmployeeId is { } id ? $" (employee id {id} is unknown)." : "."), a.Entitlements));
                continue;
            }

            var p = o.Owner;
            if (o.ByEmail)
                findings.Add(new Finding(FindingKind.WeakMatch, Severity.Low, p.EmployeeId, a.Key,
                    $"Account {a.Key} was matched to {p.Name} by email only. Store the employee id in {a.App} so leavers are found reliably.", []));

            if (!a.Enabled) continue;

            if (p.LeaveDate is { } left && left < today)
            {
                var days = today.DayNumber - left.DayNumber;
                var grace = a.Privileged ? 0 : policy.LeaverGraceDays;
                var usedAfter = a.LastLogin is { } used && used > left;
                if (days > grace)
                    findings.Add(new Finding(FindingKind.LeaverActive, a.Privileged || usedAfter || days > 30 ? Severity.Critical : Severity.High, p.EmployeeId, a.Key,
                        $"{p.Name} left on {left:yyyy-MM-dd} ({days} days ago), account {a.Key} is still enabled" +
                        (a.LastLogin is { } l && l > left ? $" and was used on {l:yyyy-MM-dd}, after the last working day." : "."), a.Entitlements));
                continue;
            }

            var lastUse = a.LastLogin ?? a.Created;
            if (lastUse != DateOnly.MinValue && today.DayNumber - lastUse.DayNumber > policy.DormantDays)
                findings.Add(new Finding(FindingKind.Dormant, a.Privileged ? Severity.High : Severity.Medium, p.EmployeeId, a.Key,
                    a.LastLogin is null
                        ? $"Account {a.Key} was created on {a.Created:yyyy-MM-dd} and has never been used."
                        : $"Account {a.Key} was last used on {a.LastLogin:yyyy-MM-dd}, {today.DayNumber - lastUse.DayNumber} days ago (limit {policy.DormantDays}).",
                    a.Entitlements));
        }

        // Per person: SoD across all applications, mover leftovers and access outside the baseline
        foreach (var group in owned.Where(o => o.Owner is not null && o.Account.Enabled).GroupBy(o => o.Owner!.EmployeeId, StringComparer.OrdinalIgnoreCase))
        {
            var person = group.First().Owner!;
            if (person.HasLeft(today)) continue;
            var held = group.SelectMany(o => o.Account.Entitlements.Select(e => (Entitlement: e, o.Account))).ToList();
            var set = new HashSet<string>(held.Select(h => h.Entitlement), StringComparer.OrdinalIgnoreCase);

            foreach (var rule in policy.SodRules)
            {
                var a = rule.SideA.Where(set.Contains).ToList();
                var b = rule.SideB.Where(set.Contains).ToList();
                if (a.Count == 0 || b.Count == 0) continue;
                var mitigation = policy.Mitigations.FirstOrDefault(m =>
                    string.Equals(m.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.EmployeeId, person.EmployeeId, StringComparison.OrdinalIgnoreCase));
                if (mitigation is not null && mitigation.Expires >= today)
                {
                    if (mitigation.Expires.DayNumber - today.DayNumber <= policy.MitigationWarningDays)
                        findings.Add(new Finding(FindingKind.MitigationExpiring, Severity.Medium, person.EmployeeId, null,
                            $"The mitigation for {rule.Name} ({mitigation.Control}) for {person.Name} runs out on {mitigation.Expires:yyyy-MM-dd}.", [.. a, .. b]) { RuleId = rule.Id });
                    continue;
                }
                var accounts = held.Where(h => a.Contains(h.Entitlement) || b.Contains(h.Entitlement)).Select(h => h.Account.Key).Distinct().ToList();
                findings.Add(new Finding(FindingKind.SodConflict, rule.Severity, person.EmployeeId, accounts.Count == 1 ? accounts[0] : null,
                    $"{person.Name} holds {string.Join(", ", a)} and {string.Join(", ", b)}: {rule.Name}. {rule.Why}" +
                    (mitigation is not null ? $" The mitigation ran out on {mitigation.Expires:yyyy-MM-dd}." : "") +
                    (accounts.Count > 1 ? $" Across {string.Join(" and ", accounts)}." : ""), [.. a, .. b]) { RuleId = rule.Id });
            }

            var baseline = Baseline(policy, person.Department);
            var previous = person.IsMover ? Baseline(policy, person.PreviousDepartment!) : [];
            var moverOverdue = person.IsMover && today.DayNumber - person.MovedOn!.Value.DayNumber > policy.MoverGraceDays;
            foreach (var account in group.Select(o => o.Account))
            {
                var outside = account.Entitlements.Where(e => !baseline.Contains(e)).ToList();
                if (outside.Count == 0) continue;
                var retained = outside.Where(previous.Contains).ToList();
                if (retained.Count > 0 && moverOverdue)
                    findings.Add(new Finding(FindingKind.MoverRetained, Severity.High, person.EmployeeId, account.Key,
                        $"{person.Name} moved from {person.PreviousDepartment} to {person.Department} on {person.MovedOn:yyyy-MM-dd} and still holds {string.Join(", ", retained)} from the old department.",
                        retained));
                var other = outside.Except(moverOverdue ? retained : [], StringComparer.OrdinalIgnoreCase).ToList();
                if (other.Count > 0 && policy.Baselines.Count > 0)
                    findings.Add(new Finding(FindingKind.OutsideBaseline, Severity.Low, person.EmployeeId, account.Key,
                        $"{string.Join(", ", other)} {(other.Count == 1 ? "is" : "are")} not part of the {person.Department} baseline: the reviewer must confirm it is still needed.", other));
            }
        }

        foreach (var m in policy.Mitigations.Where(m => m.Expires < today))
        {
            if (findings.Any(f => f.Kind == FindingKind.SodConflict && f.RuleId == m.RuleId && string.Equals(f.EmployeeId, m.EmployeeId, StringComparison.OrdinalIgnoreCase))) continue;
            // Expired, but the conflict is gone: only a housekeeping note
            findings.Add(new Finding(FindingKind.MitigationExpiring, Severity.Low, m.EmployeeId, null,
                $"The mitigation for rule {m.RuleId} expired on {m.Expires:yyyy-MM-dd} and is no longer needed: remove it from the policy.", []) { RuleId = m.RuleId });
        }

        return findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Kind).ThenBy(f => f.EmployeeId).ThenBy(f => f.AccountKey).ToList();
    }

    static HashSet<string> Baseline(Policy policy, string department)
    {
        var entry = policy.Baselines.FirstOrDefault(b => string.Equals(b.Key, department, StringComparison.OrdinalIgnoreCase));
        return new HashSet<string>(entry.Value ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static string Describe(FindingKind kind) => kind switch
    {
        FindingKind.Orphan => "Orphaned account",
        FindingKind.LeaverActive => "Leaver still active",
        FindingKind.Dormant => "Dormant account",
        FindingKind.SodConflict => "SoD conflict",
        FindingKind.MoverRetained => "Mover kept old access",
        FindingKind.OutsideBaseline => "Outside role baseline",
        FindingKind.UnownedPrivileged => "Unowned privileged account",
        FindingKind.MitigationExpiring => "Mitigation expiring",
        FindingKind.WeakMatch => "Matched by email only",
        _ => kind.ToString(),
    };
}
