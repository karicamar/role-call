using Microsoft.AspNetCore.Mvc;
using RoleCall.Domain.Analysis;
using RoleCall.Domain.Model;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

public sealed class FindingsModel(ReviewService reviews) : AppPage
{
    [BindProperty(SupportsGet = true)] public FindingKind? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public Severity? Severity { get; set; }

    public IReadOnlyList<Finding> All { get; private set; } = [];
    public IReadOnlyList<Finding> Shown { get; private set; } = [];
    public Snapshot Snapshot { get; private set; } = new([], []);

    public async Task OnGetAsync()
    {
        Snapshot = await reviews.SnapshotAsync();
        All = VisibleTo(Actor, await reviews.FindingsAsync(), Snapshot);
        Shown = All.Where(f => (Kind is null || f.Kind == Kind) && (Severity is null || f.Severity == Severity)).ToList();
    }

    /// <summary>
    /// The IAM team and internal audit see every finding. A manager sees the findings of their direct reports only:
    /// who has which access is itself sensitive information.
    /// </summary>
    public static IReadOnlyList<Finding> VisibleTo(Actor actor, IReadOnlyList<Finding> findings, Snapshot snapshot)
    {
        if (actor.IsIam || actor.IsAuditor) return findings;
        if (actor.EmployeeId is null) return [];
        var team = snapshot.People.Where(p => string.Equals(p.ManagerId, actor.EmployeeId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.EmployeeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return findings.Where(f => f.EmployeeId is not null && team.Contains(f.EmployeeId)).ToList();
    }

    public string Name(string? employeeId) => Snapshot.Find(employeeId)?.Name ?? "";
}
