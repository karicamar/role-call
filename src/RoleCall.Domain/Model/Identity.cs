namespace RoleCall.Domain.Model;

/// <summary>One person from the HR feed (the source of truth for who works here and in which role).</summary>
public sealed record Person(
    string EmployeeId,
    string Name,
    string Email,
    string Department,
    string Position,
    string? ManagerId,
    DateOnly? LeaveDate = null,
    string? PreviousDepartment = null,
    DateOnly? MovedOn = null)
{
    /// <summary>A leaver is anyone whose last working day is before <paramref name="today"/>.</summary>
    public bool HasLeft(DateOnly today) => LeaveDate is { } d && d < today;

    public bool IsMover => PreviousDepartment is { Length: > 0 } && MovedOn is not null;
}

/// <summary>An account in one application, with the entitlements (roles, groups, permissions) it holds there.</summary>
public sealed record Account(
    string App,
    string AccountId,
    string? EmployeeId,
    string? Email,
    bool Enabled,
    bool Privileged,
    DateOnly Created,
    DateOnly? LastLogin,
    IReadOnlyList<string> Entitlements)
{
    public string Key => $"{App}/{AccountId}";
}

/// <summary>An entitlement across all applications is written as "App:Name", for example "Payments:Approve".</summary>
public static class Entitlement
{
    public static string Qualified(string app, string entitlement) => $"{app}:{entitlement}";

    public static (string App, string Name) Split(string qualified)
    {
        var i = qualified.IndexOf(':');
        return i <= 0 ? ("", qualified) : (qualified[..i], qualified[(i + 1)..]);
    }
}

/// <summary>Everything one review is based on: the HR feed and the account exports of every application.</summary>
public sealed record Snapshot(IReadOnlyList<Person> People, IReadOnlyList<Account> Accounts)
{
    public Person? Find(string? employeeId) =>
        employeeId is null ? null : People.FirstOrDefault(p => string.Equals(p.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase));
}
