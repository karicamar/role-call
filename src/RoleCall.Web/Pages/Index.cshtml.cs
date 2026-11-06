using RoleCall.Domain.Analysis;
using RoleCall.Domain.Model;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

public sealed class IndexModel(ReviewService reviews, Clock clock) : AppPage
{
    public Overview Data { get; private set; } = null!;
    public IReadOnlyList<Finding> Visible { get; private set; } = [];
    public int MyOpen { get; private set; }
    public int MyRisky { get; private set; }
    public bool Oversight => Actor.IsIam || Actor.IsAuditor;
    public Clock Clock => clock;

    public async Task OnGetAsync()
    {
        Data = await reviews.OverviewAsync(Actor);
        var snapshot = await reviews.SnapshotAsync();
        Visible = FindingsModel.VisibleTo(Actor, Data.Findings, snapshot);
        var (_, mine) = await reviews.MyItemsAsync(Actor);
        MyOpen = mine.Count(i => i.Decision is null);
        MyRisky = mine.Count(i => i.Decision is null && i.Risks.Count > 0);
    }

    public int Count(Severity s) => Visible.Count(f => f.Severity == s);
}
