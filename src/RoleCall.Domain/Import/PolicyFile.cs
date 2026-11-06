using System.Text.Json;
using System.Text.Json.Serialization;
using RoleCall.Domain.Model;

namespace RoleCall.Domain.Import;

/// <summary>The policy as a JSON file the IAM team keeps in version control (SoD matrix, baselines, owners).</summary>
public static class PolicyFile
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Policy Parse(string json)
    {
        Policy? policy;
        try { policy = JsonSerializer.Deserialize<Policy>(json, Json); }
        catch (JsonException e) { throw new ImportException($"The policy is not valid JSON: {e.Message}"); }
        if (policy is null) throw new ImportException("The policy is empty.");
        Validate(policy);
        return policy;
    }

    public static string Write(Policy policy) => JsonSerializer.Serialize(policy, Json);

    public static void Validate(Policy policy)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in policy.SodRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id)) throw new ImportException($"SoD rule id \"{rule.Id}\" is empty or not unique.");
            if (rule.SideA.Count == 0 || rule.SideB.Count == 0) throw new ImportException($"SoD rule {rule.Id} needs entitlements on both sides.");
            var overlap = rule.SideA.Intersect(rule.SideB, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (overlap is not null) throw new ImportException($"SoD rule {rule.Id}: \"{overlap}\" is on both sides.");
            foreach (var e in rule.SideA.Concat(rule.SideB))
                if (!e.Contains(':')) throw new ImportException($"SoD rule {rule.Id}: \"{e}\" must be written as App:Entitlement.");
        }
        foreach (var m in policy.Mitigations)
            if (!ids.Contains(m.RuleId)) throw new ImportException($"Mitigation for {m.EmployeeId} refers to unknown rule {m.RuleId}.");
        if (policy.DormantDays < 1 || policy.LeaverGraceDays < 0 || policy.MoverGraceDays < 0)
            throw new ImportException("DormantDays must be positive, grace days zero or more.");
    }
}
