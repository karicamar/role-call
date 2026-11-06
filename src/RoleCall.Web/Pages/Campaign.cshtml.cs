using Microsoft.AspNetCore.Mvc;
using RoleCall.Domain.Model;
using RoleCall.Domain.Reviews;
using RoleCall.Web.Data;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

public sealed class CampaignModel(ReviewService reviews, Clock clock) : AppPage
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string Show { get; set; } = "risky";

    public CampaignRow? Current { get; private set; }
    public IReadOnlyList<ReviewItemRow> Items { get; private set; } = [];
    public IReadOnlyList<ReviewItemRow> Shown { get; private set; } = [];
    public CampaignStats? Stats { get; private set; }
    public IReadOnlyList<CampaignRow> History { get; private set; } = [];
    public IReadOnlyList<RubberStampSignal> Signals { get; private set; } = [];
    public Snapshot Snapshot { get; private set; } = new([], []);
    public bool CanManage => Actor.IsIam;
    public Clock Clock => clock;
    public DateOnly SuggestedDue => clock.Today.AddDays(14);

    public async Task<IActionResult> OnGetAsync()
    {
        History = await reviews.CampaignsAsync();
        var id = Id ?? History.FirstOrDefault()?.Id;
        if (id is null) return Page();
        if (History.All(c => c.Id != id)) return NotFound();
        (Current, Items, Stats) = await reviews.CampaignAsync(Actor, id.Value);
        Signals = await reviews.RubberStampAsync(Actor, id.Value);
        Snapshot = await reviews.SnapshotAsync();
        Shown = Show switch
        {
            "open" => Items.Where(i => i.Decision is null).ToList(),
            "revoked" => Items.Where(i => i.Decision == Decision.Revoke).ToList(),
            "all" => Items,
            _ => Items.Where(i => i.Risks.Count > 0).ToList(),
        };
        return Page();
    }

    public Task<IActionResult> OnPostStartAsync(string name, DateOnly dueOn) =>
        Run(async () =>
        {
            var c = await reviews.StartCampaignAsync(Actor, name, dueOn);
            Id = c.Id;
            return $"Campaign started with {c.Items.Count} items. Reviewers see them under \"My reviews\".";
        }, new { Id });

    public Task<IActionResult> OnPostEscalateAsync() =>
        Run(async () => $"{await reviews.EscalateOverdueAsync(Actor)} overdue items escalated to the next manager.", new { Id });

    public Task<IActionResult> OnPostCloseAsync() =>
        Run(async () =>
        {
            await reviews.CloseCampaignAsync(Actor, Id ?? 0);
            return "Campaign closed. Download the revocation list for the application administrators.";
        }, new { Id });

    public string Name(string? employeeId) => Snapshot.Find(employeeId)?.Name ?? employeeId ?? "";
}
