using Microsoft.AspNetCore.Mvc;
using RoleCall.Domain.Reviews;
using RoleCall.Web.Data;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

public sealed class ReviewsModel(ReviewService reviews, Clock clock) : AppPage
{
    [BindProperty(SupportsGet = true)] public bool All { get; set; }

    public CampaignRow? Campaign { get; private set; }
    public IReadOnlyList<ReviewItemRow> Items { get; private set; } = [];
    public IReadOnlyList<IGrouping<string, ReviewItemRow>> People { get; private set; } = [];
    public bool Linked => Actor.EmployeeId is not null;
    public Clock Clock => clock;

    public int Open => Items.Count(i => i.Decision is null);
    public int LowRiskOpen => Items.Count(i => i.Decision is null && ReviewRules.BulkKeepAllowed(i.Risks));

    public async Task OnGetAsync()
    {
        (Campaign, Items) = await reviews.MyItemsAsync(Actor);
        People = Items.Where(i => All || i.Decision is null)
            .GroupBy(i => i.SubjectId)
            .OrderByDescending(g => g.Max(i => i.Severity)).ThenBy(g => g.First().SubjectName)
            .ToList();
    }

    public Task<IActionResult> OnPostDecideAsync(int id, int version, Decision decision, string? comment) =>
        Run(async () =>
        {
            await reviews.DecideAsync(Actor, id, version, decision, comment);
            return decision == Decision.Keep ? "Access confirmed." : "Marked for revocation. The IAM team removes it after the campaign.";
        }, new { All });

    public Task<IActionResult> OnPostKeepLowRiskAsync() =>
        Run(async () =>
        {
            var (_, items) = await reviews.MyItemsAsync(Actor);
            var n = await reviews.BulkKeepAsync(Actor, items.Where(i => i.Decision is null && ReviewRules.BulkKeepAllowed(i.Risks)).Select(i => i.Id).ToList());
            return $"{n} low-risk entitlements confirmed. Items with a risk still need a decision one by one.";
        }, new { All });
}
