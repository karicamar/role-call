namespace RoleCall.Domain.Model;

public enum Severity { Low, Medium, High, Critical }

/// <summary>
/// Two sets of entitlements one person must not hold together, for example creating and approving a payment.
/// Holding any entitlement of <see cref="SideA"/> and any of <see cref="SideB"/> is a conflict, across applications.
/// </summary>
public sealed record SodRule(string Id, string Name, IReadOnlyList<string> SideA, IReadOnlyList<string> SideB, Severity Severity, string Why);

/// <summary>An accepted SoD conflict: the risk is covered by another control until <see cref="Expires"/>.</summary>
public sealed record Mitigation(string RuleId, string EmployeeId, string Control, string ApprovedBy, DateOnly Expires);

/// <summary>The bank's rules for access: SoD matrix, role baselines per department, owners and time limits.</summary>
public sealed record Policy
{
    public IReadOnlyList<SodRule> SodRules { get; init; } = [];
    public IReadOnlyList<Mitigation> Mitigations { get; init; } = [];

    /// <summary>Department → entitlements everyone there may hold without a special reason ("birthright" access).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Baselines { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Application → employee id of the application owner (reviews orphaned accounts, steps in for missing managers).</summary>
    public IReadOnlyDictionary<string, string> AppOwners { get; init; } = new Dictionary<string, string>();

    /// <summary>Reviews items nobody else may review, e.g. the access of an application owner to their own application.</summary>
    public string FallbackReviewer { get; init; } = "";

    /// <summary>An enabled account without a login for this many days is dormant.</summary>
    public int DormantDays { get; init; } = 90;

    /// <summary>Days after the last working day until a normal account must be disabled. Privileged accounts: no grace.</summary>
    public int LeaverGraceDays { get; init; } = 1;

    /// <summary>Days a mover may keep the access of the old department (handover).</summary>
    public int MoverGraceDays { get; init; } = 30;

    /// <summary>Mitigations that run out within this many days are reported, so they are renewed or the conflict removed in time.</summary>
    public int MitigationWarningDays { get; init; } = 30;
}
