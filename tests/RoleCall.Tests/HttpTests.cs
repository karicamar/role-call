using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using RoleCall.Domain.Reviews;

namespace RoleCall.Tests;

/// <summary>The app over HTTP: sign-in, roles, CSRF, headers and the pages people actually use.</summary>
public partial class HttpTests : IAsyncLifetime
{
    readonly RoleCallApp app = new(seed: true);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await app.DisposeAsync();

    HttpClient Client() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    static string Token(string html) => TokenPattern().Match(html).Groups[1].Value;

    static async Task<HttpResponseMessage> Login(HttpClient client, string user, string? returnUrl = null)
    {
        var token = Token(await client.GetStringAsync("/login"));
        var form = new Dictionary<string, string> { ["user"] = user, ["__RequestVerificationToken"] = token };
        if (returnUrl is not null) form["returnUrl"] = returnUrl;
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(form));
    }

    async Task<HttpClient> As(string user)
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client, user)).StatusCode);
        return client;
    }

    [Fact]
    public async Task Everything_but_the_login_requires_sign_in()
    {
        var client = Client();
        foreach (var path in new[] { "/", "/reviews", "/findings", "/campaign", "/audit", "/admin/import", "/campaigns/1/evidence.json" })
        {
            var r = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.StartsWith("/login", r.Headers.Location!.PathAndQuery);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.css")).StatusCode);
    }

    [Fact]
    public async Task Pages_send_a_strict_content_security_policy_and_no_caching()
    {
        var r = await Client().GetAsync("/login");
        var csp = string.Join(";", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("script-src", csp);
        Assert.DoesNotContain("unsafe", csp);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(r.Headers.CacheControl?.NoStore);
        var html = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("style=", html);
    }

    [Fact]
    public async Task The_session_cookie_is_http_only_strict_and_secure()
    {
        var r = await Login(Client(), "lena.iam");
        var cookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("role-call="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("//evil.example/")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    public async Task The_return_url_cannot_send_people_to_another_site(string returnUrl)
    {
        var r = await Login(Client(), "lena.iam", returnUrl);
        Assert.Equal("/", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_local_return_url_is_kept()
    {
        var r = await Login(Client(), "tom.payments", "/reviews");
        Assert.Equal("/reviews", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Login_without_the_antiforgery_token_is_refused()
    {
        var r = await Client().PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "lena.iam" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Without_demo_login_there_are_no_passwordless_users()
    {
        await using var prod = new RoleCallApp(demoLogin: false);
        var client = prod.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var page = await client.GetStringAsync("/login");
        Assert.DoesNotContain("lena.iam", page);
        Assert.Contains("No sign-in method is configured", page);
        var r = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "lena.iam" }));
        Assert.NotEqual(HttpStatusCode.Redirect, r.StatusCode);
    }

    [Fact]
    public async Task Managers_cannot_open_the_campaign_audit_or_import_pages()
    {
        var tom = await As("tom.payments");
        foreach (var path in new[] { "/campaign", "/audit", "/admin/import", "/campaigns/1/revocations.csv" })
        {
            var r = await tom.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.StartsWith("/denied", r.Headers.Location!.PathAndQuery);
        }
        Assert.Equal(HttpStatusCode.OK, (await tom.GetAsync("/reviews")).StatusCode);
    }

    [Fact]
    public async Task The_auditor_reads_everything_but_cannot_import()
    {
        var anna = await As("anna.audit");
        Assert.Contains("Review quality", await anna.GetStringAsync("/campaign"));
        Assert.Contains("Hash chain intact", await anna.GetStringAsync("/audit"));
        var r = await anna.GetAsync("/campaigns/1/evidence.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(64, r.Headers.GetValues("X-Content-SHA256").Single().Length);
        Assert.Equal(HttpStatusCode.Redirect, (await anna.GetAsync("/admin/import")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync("/campaigns/99/evidence.json")).StatusCode);
    }

    [Fact]
    public async Task A_manager_sees_only_the_findings_of_their_own_team()
    {
        var tom = await As("tom.payments");
        var page = await tom.GetStringAsync("/findings");
        Assert.Contains("Mia Schulz", page);
        Assert.DoesNotContain("Lukas Braun", page);
        Assert.DoesNotContain("svc_backup", page);

        var lena = await As("lena.iam");
        page = await lena.GetStringAsync("/findings");
        Assert.Contains("Lukas Braun", page);
        Assert.Contains("svc_backup", page);
    }

    [Fact]
    public async Task A_reviewer_decides_in_the_browser_flow()
    {
        var tom = await As("tom.payments");
        var page = await tom.GetStringAsync("/reviews");
        Assert.Contains("Mia Schulz", page);
        Assert.Contains("Keep all", page);

        int id, version;
        await using (var db = app.Db())
        {
            var item = await db.Items.FirstAsync(i => i.SubjectId == "E1021" && i.Entitlement == "Payments:Approve");
            (id, version) = (item.Id, item.Version);
        }
        var form = new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["version"] = version.ToString(), ["decision"] = "Revoke", ["comment"] = "",
            ["__RequestVerificationToken"] = Token(page),
        };
        var r = await tom.PostAsync("/reviews?handler=Decide", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Marked for revocation", await tom.GetStringAsync(r.Headers.Location!.OriginalString));
        await using (var db = app.Db())
            Assert.Equal(Decision.Revoke, (await db.Items.SingleAsync(i => i.Id == id)).Decision);

        // Without the token the same post is refused
        form.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await tom.PostAsync("/reviews?handler=Decide", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task Deciding_someone_elses_item_is_forbidden()
    {
        var nina = await As("nina.treasury");
        var token = Token(await nina.GetStringAsync("/reviews"));
        int id;
        await using (var db = app.Db())
            id = (await db.Items.FirstAsync(i => i.ReviewerId == "E1020")).Id;
        var r = await nina.PostAsync("/reviews?handler=Decide", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["version"] = "0", ["decision"] = "Keep", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/denied", r.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task A_rule_violation_is_shown_on_the_page()
    {
        var tom = await As("tom.payments");
        var token = Token(await tom.GetStringAsync("/reviews"));
        int id;
        await using (var db = app.Db())
            id = (await db.Items.FirstAsync(i => i.SubjectId == "E1021" && i.Entitlement == "Payments:Approve")).Id;
        var r = await tom.PostAsync("/reviews?handler=Decide", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["version"] = "0", ["decision"] = "Keep", ["__RequestVerificationToken"] = token,
        }));
        Assert.Contains("compensating control", await tom.GetStringAsync(r.Headers.Location!.OriginalString));
    }

    [Fact]
    public async Task The_IAM_team_uploads_an_export_and_bad_files_are_explained()
    {
        var lena = await As("lena.iam");
        var token = Token(await lena.GetStringAsync("/admin/import"));

        async Task<string> Upload(string handler, string fileName, string content, string? app = null)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(token), "__RequestVerificationToken" },
                { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content)), "file", fileName },
            };
            if (app is not null) form.Add(new StringContent(app), "app");
            var r = await lena.PostAsync($"/admin/import?handler={handler}", form);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            return await lena.GetStringAsync(r.Headers.Location!.OriginalString);
        }

        Assert.Contains("2 accounts of Wiki imported", await Upload("Accounts", "wiki.csv", "account_id,employee_id,enabled,entitlements\na,E1021,yes,Edit\nb,,yes,Admin\n", "Wiki"));
        Assert.Contains("Line 2: expected 3 fields", await Upload("Accounts", "bad.csv", "account_id,enabled,entitlements\na,yes\n", "Wiki"));
        Assert.Contains("not valid JSON", await Upload("Policy", "p.json", "{ nope"));
        Assert.Contains("Wiki/b", await lena.GetStringAsync("/findings"));
    }

    [Fact]
    public async Task Sign_out_needs_the_token_and_ends_the_session()
    {
        var tom = await As("tom.payments");
        Assert.Equal(HttpStatusCode.BadRequest, (await tom.PostAsync("/account/logout", new FormUrlEncodedContent([]))).StatusCode);
        var token = Token(await tom.GetStringAsync("/reviews"));
        var r = await tom.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal("/login", r.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await tom.GetAsync("/reviews")).StatusCode);
    }

    [Fact]
    public async Task The_seeded_demo_tells_the_story()
    {
        var lena = await As("lena.iam");
        var overview = await lena.GetStringAsync("/");
        Assert.Contains("critical findings", overview);
        Assert.Contains("Q4 2026 access recertification", overview);
        var campaign = await lena.GetStringAsync("/campaign");
        Assert.Contains("Nina Koch", campaign);
        Assert.Contains("% keep", campaign);
        var nina = await As("nina.treasury");
        Assert.Contains("Left the bank: revoke only", await nina.GetStringAsync("/reviews"));
    }

    [Fact]
    public async Task Unknown_pages_get_a_friendly_404()
    {
        var lena = await As("lena.iam");
        var r = await lena.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("This page does not exist", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync("/campaign/42")).StatusCode);
    }
}
