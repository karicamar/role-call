using RoleCall.Web.Data;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

public sealed class AuditModel(ReviewService reviews, Clock clock) : AppPage
{
    public IReadOnlyList<AuditRow> Rows { get; private set; } = [];
    public int? BrokenAt { get; private set; }
    public Clock Clock => clock;

    public async Task OnGetAsync()
    {
        Rows = await reviews.AuditTrailAsync(Actor);
        BrokenAt = await reviews.VerifyAuditChainAsync();
    }
}
