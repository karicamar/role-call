using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace RoleCall.Web.Services;

public static class Roles
{
    /// <summary>The IAM team: imports the feeds, maintains the policy, runs campaigns.</summary>
    public const string Iam = "iam";

    /// <summary>Internal audit: reads everything, changes nothing.</summary>
    public const string Auditor = "auditor";

    public static readonly string[] All = [Iam, Auditor];
}

public static class Policies
{
    public const string Manage = "manage";
    public const string Oversight = "oversight";
}

public static class Claims
{
    /// <summary>Links the signed-in user to the HR feed. Reviews are assigned by employee id.</summary>
    public const string EmployeeId = "employee_id";
}

/// <summary>Who is acting. Taken from the signed-in user, never from form input.</summary>
public sealed record Actor(string Name, string? EmployeeId, IReadOnlySet<string> Roles)
{
    public bool IsIam => Roles.Contains(Services.Roles.Iam);
    public bool IsAuditor => Roles.Contains(Services.Roles.Auditor);

    public static Actor From(ClaimsPrincipal user) =>
        new(user.Identity?.Name ?? throw new AccessException(AccessError.Forbidden, "Not signed in."),
            user.FindFirstValue(Claims.EmployeeId),
            user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal));
}

public enum AccessError { NotFound, Forbidden, Conflict, Invalid }

public sealed class AccessException(AccessError error, string message) : Exception(message)
{
    public AccessError Error { get; } = error;
}

public sealed class RoleCallOptions
{
    public string Organisation { get; set; } = "Mainufer Bank AG (demo)";

    /// <summary>Time zone for "today" (leaver dates, due dates) and display. Stored times are UTC.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>Sign in as a demo user without a password. Development only, or explicitly enabled for a demo.</summary>
    public bool DemoLogin { get; set; }
}

public sealed record DemoUser(string Name, string DisplayName, string EmployeeId, string? Role, string Description)
{
    public static readonly DemoUser[] All =
    [
        new("lena.iam", "Lena Hoffmann (IAM team)", "E1010", Roles.Iam, "imports the feeds, starts and closes the campaign"),
        new("tom.payments", "Tom Becker (Head of Payments)", "E1020", null, "reviews the payments team, owns the payments application"),
        new("nina.treasury", "Nina Koch (Head of Treasury)", "E1030", null, "reviews the treasury team, owns the treasury system"),
        new("daniel.it", "Daniel Klein (Head of IT Operations)", "E1040", null, "reviews IT, owns core banking and the directory"),
        new("clara.ceo", "Dr. Clara Wendt (CEO)", "E1001", null, "reviews the heads of department, receives escalations"),
        new("jonas.ciso", "Jonas Weber (CISO)", "E1002", null, "fallback reviewer for items nobody else may decide"),
        new("anna.audit", "Anna Vogel (internal audit)", "E1050", Roles.Auditor, "read-only, sees rubber-stamping signals"),
    ];
}

public sealed class Clock(TimeProvider time, IOptions<RoleCallOptions> options)
{
    readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, zone).DateTime);

    public string Format(DateTimeOffset? at) =>
        at is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";
}
