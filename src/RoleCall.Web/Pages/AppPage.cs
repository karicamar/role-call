using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RoleCall.Domain.Model;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

/// <summary>Base for every page: the signed-in actor, and post-redirect-get with a message for each action.</summary>
public abstract class AppPage : PageModel
{
    protected Actor Actor => Actor.From(User);

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Problem { get; set; }

    /// <summary>Runs an action; a rule violation becomes a message on the page, not an error page.</summary>
    protected async Task<IActionResult> Run(Func<Task<string>> action, object? routeValues = null)
    {
        try
        {
            Notice = await action();
        }
        catch (AccessException e) when (e.Error == AccessError.Forbidden)
        {
            return Forbid();
        }
        catch (AccessException e)
        {
            Problem = e.Message;
        }
        return RedirectToPage(routeValues);
    }

    public static string Css(Severity s) => s.ToString().ToLowerInvariant();

    /// <summary>Rounded to the nearest 10 %, for the progress bar classes (no inline styles under the CSP).</summary>
    public static string Bar(int percent) => $"p{Math.Clamp((int)Math.Round(percent / 10.0) * 10, 0, 100)}";
}
