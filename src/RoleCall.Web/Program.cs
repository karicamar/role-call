using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RoleCall.Web.Data;
using RoleCall.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RoleCallOptions>(builder.Configuration.GetSection("RoleCall"));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Clock>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddDbContextFactory<RoleCallDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("RoleCall") ?? "Data Source=role-call.db"));
builder.Services.AddHealthChecks().AddDbContextCheck<RoleCallDb>();

// Uploads: HR feeds and account exports are CSV files of a few MB at most
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 12 * 1024 * 1024);

// Sign-in: OpenID Connect against the bank's identity provider (for example Entra ID) when configured. The employee id
// claim links the user to the HR feed. Without OIDC, only the passwordless demo login exists, and only where enabled.
var oidc = builder.Configuration.GetSection("Auth:Oidc");
var auth = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        if (oidc["Authority"] is { Length: > 0 }) o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "role-call";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
if (oidc["Authority"] is { Length: > 0 } authority)
    auth.AddOpenIdConnect(o =>
    {
        o.Authority = authority;
        o.ClientId = oidc["ClientId"];
        o.ClientSecret = oidc["ClientSecret"];
        o.ResponseType = "code";
        o.UsePkce = true;
        o.MapInboundClaims = false;
        o.TokenValidationParameters.NameClaimType = "preferred_username";
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.Scope.Add("profile");
        var employeeClaim = oidc["EmployeeIdClaim"] is { Length: > 0 } c ? c : "employee_id";
        o.Events.OnTokenValidated = ctx =>
        {
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            foreach (var role in identity.FindAll("roles").Select(r => r.Value).Where(r => Roles.All.Contains(r)).ToList())
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            identity.AddClaim(new Claim(ClaimTypes.Name, identity.Name ?? ctx.Principal.FindFirstValue("sub") ?? "unknown"));
            if (ctx.Principal.FindFirstValue(employeeClaim) is { Length: > 0 } id && employeeClaim != Claims.EmployeeId)
                identity.AddClaim(new Claim(Claims.EmployeeId, id));
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Manage, p => p.RequireRole(Roles.Iam))
    .AddPolicy(Policies.Oversight, p => p.RequireRole(Roles.Iam, Roles.Auditor));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/NotFound");
    o.Conventions.AllowAnonymousToPage("/Denied");
    o.Conventions.AuthorizeFolder("/Admin", Policies.Manage);
    o.Conventions.AuthorizePage("/Campaign", Policies.Oversight);
    o.Conventions.AuthorizePage("/Audit", Policies.Oversight);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RoleCallDb>>();
    await using (var db = await factory.CreateDbContextAsync())
        await db.Database.EnsureCreatedAsync();
    if (app.Configuration.GetValue("RoleCall:SeedDemoData", app.Environment.IsDevelopment()))
        await DemoData.SeedAsync(factory, app.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            app.Services.GetRequiredService<IOptions<RoleCallOptions>>());
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    // Server-rendered pages without any script: nothing inline, nothing from elsewhere
    h.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self' data:; form-action 'self'; "
                              + "base-uri 'none'; frame-ancestors 'none'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h.CacheControl = "no-store";
    await next();
});

// Friendly "not found" page for browsers; only for GET, a rejected POST must keep its status code
app.UseWhen(ctx => HttpMethods.IsGet(ctx.Request.Method), b => b.UseStatusCodePagesWithReExecute("/not-found"));
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health").AllowAnonymous();

app.MapPost("/account/login", async ([FromForm] string user, [FromForm] string? returnUrl, HttpContext ctx, IOptions<RoleCallOptions> options) =>
{
    if (!options.Value.DemoLogin) return Results.NotFound();
    var demo = DemoUser.All.FirstOrDefault(u => u.Name == user);
    if (demo is null) return Results.Redirect("/login?error=1");
    var claims = new List<Claim> { new(ClaimTypes.Name, demo.Name), new(Claims.EmployeeId, demo.EmployeeId) };
    if (demo.Role is { } role) claims.Add(new Claim(ClaimTypes.Role, role));
    await ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.LocalRedirect(SafeReturn(returnUrl));
}).AllowAnonymous();

app.MapGet("/account/sso", (string? returnUrl) =>
    oidc["Authority"] is { Length: > 0 }
        ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme])
        : Results.NotFound()).AllowAnonymous();

// Minimal APIs check the antiforgery token only when they bind a form, so sign-out validates it itself
// (otherwise any site could sign people out with a hidden form)
app.MapPost("/account/logout", async (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(ctx)) return Results.BadRequest();
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
}).DisableAntiforgery();

// Exports: the files carry their SHA-256 in a header so the receiver can check them
app.MapGet("/campaigns/{id:int}/revocations.csv", (int id, ReviewService reviews, HttpContext ctx) =>
    Export(ctx, () => reviews.RevocationsCsvAsync(Actor.From(ctx.User), id), "text/csv; charset=utf-8")).RequireAuthorization(Policies.Oversight);
app.MapGet("/campaigns/{id:int}/evidence.json", (int id, ReviewService reviews, HttpContext ctx) =>
    Export(ctx, () => reviews.EvidenceAsync(Actor.From(ctx.User), id), "application/json")).RequireAuthorization(Policies.Oversight);

app.MapRazorPages();

app.Run();

static async Task<IResult> Export(HttpContext ctx, Func<Task<Evidence>> build, string type)
{
    try
    {
        var file = await build();
        ctx.Response.Headers["X-Content-SHA256"] = file.Sha256;
        return Results.File(file.Content, type, file.FileName);
    }
    catch (AccessException e)
    {
        return e.Error switch { AccessError.NotFound => Results.NotFound(), AccessError.Forbidden => Results.Forbid(), _ => Results.BadRequest(e.Message) };
    }
}

// Only local paths: an attacker-supplied returnUrl must not turn the login into an open redirect
static string SafeReturn(string? url) =>
    url is { Length: > 0 } r && r[0] == '/' && (r.Length == 1 || (r[1] != '/' && r[1] != (char)92)) && !r.Contains("://") ? r : "/";

public partial class Program;
