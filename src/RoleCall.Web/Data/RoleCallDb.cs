using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RoleCall.Domain.Analysis;
using RoleCall.Domain.Model;
using RoleCall.Domain.Reviews;

namespace RoleCall.Web.Data;

public sealed class PersonRow
{
    public required string EmployeeId { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public required string Department { get; set; }
    public string Position { get; set; } = "";
    public string? ManagerId { get; set; }
    public DateOnly? LeaveDate { get; set; }
    public string? PreviousDepartment { get; set; }
    public DateOnly? MovedOn { get; set; }

    public Person ToModel() => new(EmployeeId, Name, Email, Department, Position, ManagerId, LeaveDate, PreviousDepartment, MovedOn);

    public static PersonRow From(Person p) => new()
    {
        EmployeeId = p.EmployeeId, Name = p.Name, Email = p.Email, Department = p.Department, Position = p.Position,
        ManagerId = p.ManagerId, LeaveDate = p.LeaveDate, PreviousDepartment = p.PreviousDepartment, MovedOn = p.MovedOn,
    };
}

public sealed class AccountRow
{
    public int Id { get; set; }
    public required string App { get; set; }
    public required string AccountId { get; set; }
    public string? EmployeeId { get; set; }
    public string? Email { get; set; }
    public bool Enabled { get; set; }
    public bool Privileged { get; set; }
    public DateOnly Created { get; set; }
    public DateOnly? LastLogin { get; set; }
    public List<string> Entitlements { get; set; } = [];

    public Account ToModel() => new(App, AccountId, EmployeeId, Email, Enabled, Privileged, Created, LastLogin, Entitlements);

    public static AccountRow From(Account a) => new()
    {
        App = a.App, AccountId = a.AccountId, EmployeeId = a.EmployeeId, Email = a.Email, Enabled = a.Enabled,
        Privileged = a.Privileged, Created = a.Created, LastLogin = a.LastLogin, Entitlements = [.. a.Entitlements],
    };
}

/// <summary>Every upload: what came in, from whom, and the SHA-256 of the file, so the review can prove its basis.</summary>
public sealed class ImportRow
{
    public int Id { get; set; }
    public required string Source { get; set; }
    public required string FileName { get; set; }
    public required string Sha256 { get; set; }
    public int Rows { get; set; }
    public required string ImportedBy { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class PolicyRow
{
    public int Id { get; set; }
    public required string Json { get; set; }
    public required string UploadedBy { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class CampaignRow
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public required string StartedBy { get; set; }
    public DateOnly DueOn { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }

    /// <summary>SHA-256 over the HR feed, accounts and policy the campaign was built from.</summary>
    public required string BasisHash { get; set; }
    public required string PolicyJson { get; set; }
    public List<ReviewItemRow> Items { get; set; } = [];

    public bool IsOpen => ClosedAt is null;
}

public sealed class ReviewItemRow
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public CampaignRow? Campaign { get; set; }
    public required string SubjectId { get; set; }
    public required string SubjectName { get; set; }
    public required string AccountKey { get; set; }
    public required string App { get; set; }
    public required string Entitlement { get; set; }
    public bool Privileged { get; set; }
    public required string ReviewerId { get; set; }
    public required string ReviewerReason { get; set; }
    public string? OriginalReviewerId { get; set; }
    public List<FindingKind> Risks { get; set; } = [];
    public Severity Severity { get; set; }
    public string RiskText { get; set; } = "";
    public Decision? Decision { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Comment { get; set; }
    public bool Bulk { get; set; }

    /// <summary>Optimistic concurrency: two reviewers (or two tabs) cannot overwrite each other.</summary>
    public int Version { get; set; }
}

/// <summary>Append-only audit trail. Each entry hashes the previous one, so a deleted or edited line breaks the chain.</summary>
public sealed class AuditRow
{
    public int Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Detail { get; set; }
    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}

public sealed class RoleCallDb(DbContextOptions<RoleCallDb> options) : DbContext(options)
{
    public DbSet<PersonRow> People => Set<PersonRow>();
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<ImportRow> Imports => Set<ImportRow>();
    public DbSet<PolicyRow> Policies => Set<PolicyRow>();
    public DbSet<CampaignRow> Campaigns => Set<CampaignRow>();
    public DbSet<ReviewItemRow> Items => Set<ReviewItemRow>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PersonRow>().HasKey(p => p.EmployeeId);
        b.Entity<AccountRow>().HasIndex(a => new { a.App, a.AccountId }).IsUnique();
        b.Entity<AccountRow>().Property(a => a.Entitlements).HasConversion(Json<List<string>>(), Comparer<string>());
        b.Entity<ReviewItemRow>().Property(i => i.Risks).HasConversion(Json<List<FindingKind>>(), Comparer<FindingKind>());
        b.Entity<ReviewItemRow>().Property(i => i.Version).IsConcurrencyToken();
        b.Entity<ReviewItemRow>().HasIndex(i => new { i.CampaignId, i.ReviewerId });
        b.Entity<CampaignRow>().HasMany(c => c.Items).WithOne(i => i.Campaign).HasForeignKey(i => i.CampaignId);
        b.Entity<AuditRow>().HasIndex(a => a.At);

        // SQLite cannot order by DateTimeOffset; store it as a sortable number
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
    }

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static ValueConverter<T, string> Json<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), v => JsonSerializer.Deserialize<T>(v, JsonOptions) ?? new T());

    static Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<T>> Comparer<T>() =>
        new((a, b) => (a ?? new()).SequenceEqual(b ?? new()), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToList());
}
