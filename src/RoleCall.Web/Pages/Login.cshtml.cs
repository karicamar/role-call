using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using RoleCall.Web.Services;

namespace RoleCall.Web.Pages;

public sealed class LoginModel(IOptions<RoleCallOptions> options, IConfiguration config) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true, Name = "error")] public bool Error { get; set; }

    public bool DemoLogin => options.Value.DemoLogin;
    public bool Sso => config["Auth:Oidc:Authority"] is { Length: > 0 };
    public string Organisation => options.Value.Organisation;

    public void OnGet() { }
}
