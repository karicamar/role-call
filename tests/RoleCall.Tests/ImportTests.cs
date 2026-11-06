using RoleCall.Domain.Import;
using RoleCall.Domain.Model;

namespace RoleCall.Tests;

public class CsvTests
{
    [Fact]
    public void Parses_quoted_fields_with_commas_quotes_and_line_breaks()
    {
        var t = Csv.Parse("a,b,c\n1,\"x, y\",\"say \"\"hi\"\"\"\n2,\"two\nlines\",z\n");
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("x, y", t.Rows[0].Get("b"));
        Assert.Equal("say \"hi\"", t.Rows[0].Get("c"));
        Assert.Equal("two\nlines", t.Rows[1].Get("b"));
        Assert.Equal(3, t.Rows[1].Line);
    }

    [Fact]
    public void Detects_semicolons_from_German_Excel_and_strips_the_BOM()
    {
        var t = Csv.Parse("﻿Employee ID;Name\r\nE1;Müller, Anna\r\n");
        Assert.Equal(["employee_id", "name"], t.Header);
        Assert.Equal("Müller, Anna", t.Rows[0].Get("name"));
    }

    [Fact]
    public void Skips_empty_lines_and_handles_a_missing_final_newline()
    {
        var t = Csv.Parse("a,b\n1,2\n\n3,4");
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("4", t.Rows[1].Get("b"));
    }

    [Theory]
    [InlineData("a,b\n1,2,3\n", "Line 2: expected 2 fields, found 3")]
    [InlineData("a,b\n1,\"open\n", "not closed")]
    [InlineData("a,a\n1,2\n", "appears twice")]
    [InlineData("", "empty")]
    public void Reports_broken_files_with_the_line(string csv, string message)
    {
        var e = Assert.Throws<ImportException>(() => Csv.Parse(csv));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Names_missing_required_columns()
    {
        var e = Assert.Throws<ImportException>(() => Csv.Parse("a,b\n1,2\n", "a", "c", "d"));
        Assert.Equal("Missing column(s): c, d.", e.Message);
    }

    [Fact]
    public void Limits_the_length_of_a_field()
    {
        var e = Assert.Throws<ImportException>(() => Csv.Parse("a\n" + new string('x', Csv.MaxFieldLength + 1)));
        Assert.Contains("longer than", e.Message);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+49 69 1234", "'+49 69 1234")]
    [InlineData("-1+1", "'-1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Payments:Approve", "Payments:Approve")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData(null, "")]
    public void Export_fields_cannot_run_as_spreadsheet_formulas(string? value, string expected) =>
        Assert.Equal(expected, Csv.Field(value));
}

public class ImporterTests
{
    const string Header = "employee_id,name,email,department,position,manager_id,leave_date,previous_department,moved_on\n";

    [Fact]
    public void Reads_the_HR_feed_with_both_date_formats()
    {
        var people = Importers.People(Header +
            "E1,Anna Boss,anna@bank.example,Management,CEO,,,,\n" +
            "E2,Ben Clerk,ben@bank.example,Payments,Clerk,E1,31.10.2026,Treasury,2026-09-01\n");
        Assert.Equal(2, people.Count);
        Assert.Null(people[0].ManagerId);
        Assert.Equal(new DateOnly(2026, 10, 31), people[1].LeaveDate);
        Assert.True(people[1].IsMover);
        Assert.True(people[1].HasLeft(new DateOnly(2026, 11, 1)));
        Assert.False(people[1].HasLeft(new DateOnly(2026, 10, 31)));
    }

    [Theory]
    [InlineData("E1,A,a@x.example,D,P,,,,\nE1,B,b@x.example,D,P,,,,\n", "appears twice")]
    [InlineData("E1,A,not-an-email,D,P,,,,\n", "is not an email")]
    [InlineData("E1,A,a@x.example,D,P,E9,,,\n", "manager E9 is not in the HR feed")]
    [InlineData("E1,A,a@x.example,D,P,E1,,,\n", "their own manager")]
    [InlineData("E1,A,a@x.example,D,P,,2026/10/31,,\n", "is not a date")]
    [InlineData(",A,a@x.example,D,P,,,,\n", "\"employee_id\" is empty")]
    public void Rejects_an_inconsistent_HR_feed(string rows, string message)
    {
        var e = Assert.Throws<ImportException>(() => Importers.People(Header + rows));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Qualifies_entitlements_with_the_application()
    {
        var accounts = Importers.Accounts("Payments",
            "account_id,employee_id,enabled,privileged,last_login,entitlements\n" +
            "m.s,E1,ja,nein,2026-11-01T08:15:00Z,Create | approve|Create\n" +
            "x,,no,,,\n");
        Assert.Equal(["Payments:Create", "Payments:approve"], accounts[0].Entitlements);
        Assert.True(accounts[0].Enabled);
        Assert.False(accounts[0].Privileged);
        Assert.Equal(new DateOnly(2026, 11, 1), accounts[0].LastLogin);
        Assert.Equal("Payments/m.s", accounts[0].Key);
        Assert.Empty(accounts[1].Entitlements);
        Assert.False(accounts[1].Enabled);
    }

    [Theory]
    [InlineData("Pay:ments")]
    [InlineData("a/b")]
    [InlineData(" ")]
    public void Rejects_application_names_that_would_break_entitlement_keys(string app) =>
        Assert.Throws<ImportException>(() => Importers.Accounts(app, "account_id,enabled,entitlements\na,yes,X\n"));

    [Fact]
    public void Rejects_unclear_yes_no_values_and_duplicate_accounts()
    {
        Assert.Contains("is not yes or no", Assert.Throws<ImportException>(() =>
            Importers.Accounts("A", "account_id,enabled,entitlements\na,maybe,X\n")).Message);
        Assert.Contains("appears twice", Assert.Throws<ImportException>(() =>
            Importers.Accounts("A", "account_id,enabled,entitlements\na,yes,X\nA,yes,Y\n")).Message);
    }
}

public class PolicyFileTests
{
    static string SeedPolicy() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "policy.json"))
        .Replace("TODAY+20", "2026-12-01").Replace("TODAY-15", "2026-10-01");

    [Fact]
    public void Reads_the_demo_policy_with_comments()
    {
        var p = PolicyFile.Parse(SeedPolicy());
        Assert.Equal(4, p.SodRules.Count);
        Assert.Equal(Severity.Critical, p.SodRules[0].Severity);
        Assert.Equal("E1002", p.FallbackReviewer);
        Assert.Equal("E1020", p.AppOwners["Payments"]);
        Assert.Contains("Payments:Create", p.Baselines["Payments"]);
        Assert.Equal(new DateOnly(2026, 12, 1), p.Mitigations[0].Expires);
    }

    [Fact]
    public void Round_trips()
    {
        var p = PolicyFile.Parse(SeedPolicy());
        var again = PolicyFile.Parse(PolicyFile.Write(p));
        Assert.Equal(p.SodRules.Select(r => r.Id), again.SodRules.Select(r => r.Id));
        Assert.Equal(p.Mitigations, again.Mitigations);
    }

    [Theory]
    [InlineData("""{"sodRules":[{"id":"A","name":"x","sideA":["P:a"],"sideB":["P:b"],"severity":"High","why":""},{"id":"a","name":"y","sideA":["P:a"],"sideB":["P:c"],"severity":"High","why":""}]}""", "not unique")]
    [InlineData("""{"sodRules":[{"id":"A","name":"x","sideA":["P:a"],"sideB":["P:a"],"severity":"High","why":""}]}""", "on both sides")]
    [InlineData("""{"sodRules":[{"id":"A","name":"x","sideA":["a"],"sideB":["P:b"],"severity":"High","why":""}]}""", "App:Entitlement")]
    [InlineData("""{"sodRules":[{"id":"A","name":"x","sideA":[],"sideB":["P:b"],"severity":"High","why":""}]}""", "both sides")]
    [InlineData("""{"mitigations":[{"ruleId":"X","employeeId":"E1","control":"c","approvedBy":"E2","expires":"2026-12-01"}]}""", "unknown rule X")]
    [InlineData("""{"dormantDays":0}""", "DormantDays")]
    [InlineData("""{"sodRules": [""", "not valid JSON")]
    public void Rejects_a_policy_that_would_check_the_wrong_thing(string json, string message)
    {
        var e = Assert.Throws<ImportException>(() => PolicyFile.Parse(json));
        Assert.Contains(message, e.Message);
    }
}
