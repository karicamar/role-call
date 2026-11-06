using System.Globalization;
using RoleCall.Domain.Model;

namespace RoleCall.Domain.Import;

/// <summary>Reads the HR feed and the per-application account exports into the model, with line numbers in every error.</summary>
public static class Importers
{
    public static readonly string[] HrColumns = ["employee_id", "name", "email", "department", "position", "manager_id"];
    public static readonly string[] AccountColumns = ["account_id", "enabled", "entitlements"];

    public static IReadOnlyList<Person> People(string csv)
    {
        var table = Csv.Parse(csv, HrColumns);
        var people = new List<Person>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            var id = Required(row, "employee_id");
            if (!seen.Add(id)) throw new ImportException($"Line {row.Line}: employee {id} appears twice.");
            var email = Required(row, "email");
            if (!email.Contains('@')) throw new ImportException($"Line {row.Line}: \"{email}\" is not an email address.");
            people.Add(new Person(id, Required(row, "name"), email, Required(row, "department"), row.Get("position"),
                row.Optional("manager_id"), Date(row, "leave_date"), row.Optional("previous_department"), Date(row, "moved_on")));
        }
        foreach (var p in people.Where(p => p.ManagerId is not null && !seen.Contains(p.ManagerId)))
            throw new ImportException($"Employee {p.EmployeeId}: manager {p.ManagerId} is not in the HR feed.");
        foreach (var p in people.Where(p => string.Equals(p.ManagerId, p.EmployeeId, StringComparison.OrdinalIgnoreCase)))
            throw new ImportException($"Employee {p.EmployeeId} is their own manager.");
        return people;
    }

    public static IReadOnlyList<Account> Accounts(string app, string csv)
    {
        app = app.Trim();
        if (app.Length is 0 or > 50 || app.Contains(':') || app.Contains('/'))
            throw new ImportException("The application name must be 1 to 50 characters, without \":\" or \"/\".");
        var table = Csv.Parse(csv, AccountColumns);
        var accounts = new List<Account>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            var id = Required(row, "account_id");
            if (!seen.Add(id)) throw new ImportException($"Line {row.Line}: account {id} appears twice.");
            var entitlements = row.Get("entitlements")
                .Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(e => Entitlement.Qualified(app, e))
                .ToList();
            accounts.Add(new Account(app, id, row.Optional("employee_id"), row.Optional("email"),
                Bool(row, "enabled"), Bool(row, "privileged"),
                Date(row, "created") ?? DateOnly.MinValue, Date(row, "last_login"), entitlements));
        }
        return accounts;
    }

    static string Required(Csv.Row row, string column) =>
        row.Optional(column) ?? throw new ImportException($"Line {row.Line}: \"{column}\" is empty.");

    static bool Bool(Csv.Row row, string column) => row.Get(column).ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "ja" or "y" or "x" => true,
        "false" or "no" or "0" or "nein" or "n" or "" => false,
        var v => throw new ImportException($"Line {row.Line}: \"{v}\" in \"{column}\" is not yes or no."),
    };

    static readonly string[] DateFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-dd HH:mm:ss"];

    static DateOnly? Date(Csv.Row row, string column)
    {
        if (row.Optional(column) is not { } v) return null;
        if (DateTime.TryParseExact(v, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return DateOnly.FromDateTime(d);
        throw new ImportException($"Line {row.Line}: \"{v}\" in \"{column}\" is not a date (use 2026-11-06 or 06.11.2026).");
    }
}
