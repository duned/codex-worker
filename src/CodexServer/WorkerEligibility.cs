namespace CodexServer;

/// <summary>Deterministic matching for centrally declared project requirements.</summary>
public static class WorkerEligibility
{
    public static WorkerEligibilityResult Evaluate(IEnumerable<ProjectRequirement>? requirements,
        IEnumerable<WorkerCapability>? capabilities)
    {
        var available = (capabilities ?? []).Select(Normalize).ToArray();
        var missing = new List<string>();
        foreach (var requirement in requirements ?? [])
        {
            var normalized = CentralProjectValidation.Normalize(requirement) with
            { Name = CanonicalName(requirement.Type, requirement.Name) };
            var matching = available.FirstOrDefault(capability => capability.Type == normalized.Type && capability.Name == normalized.Name);
            if (matching is null)
            {
                missing.Add(DescribeRequired(normalized));
                continue;
            }
            if (normalized.Version is null) continue;
            if (matching.Version is null || !VersionSatisfies(normalized.Version, matching.Version))
                missing.Add(DescribeMismatch(normalized, matching.Version));
        }
        return new WorkerEligibilityResult(missing.Count == 0, missing);
    }

    private static WorkerCapability Normalize(WorkerCapability capability) => capability with
    {
        Type = capability.Type.Trim().ToLowerInvariant(),
        Name = CanonicalName(capability.Type, capability.Name),
        Version = capability.Version?.Trim()
    };

    private static string CanonicalName(string type, string name)
    {
        var normalizedType = type.Trim().ToLowerInvariant();
        var normalizedName = name.Trim().ToLowerInvariant();
        return normalizedType == "runtime" && normalizedName == ".net" ? "dotnet" : normalizedName;
    }

    private static bool VersionSatisfies(string requirement, string available)
    {
        var minimum = requirement.StartsWith(">=", StringComparison.Ordinal);
        var requiredVersion = ParseVersion(minimum ? requirement[2..] : requirement);
        var availableVersion = ParseVersion(available);
        if (requiredVersion is null || availableVersion is null) return false;
        var comparison = CompareVersions(availableVersion, requiredVersion);
        return minimum ? comparison >= 0 : comparison == 0;
    }

    private static int[]? ParseVersion(string value)
    {
        var parts = value.Split('.');
        var result = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
            if (!int.TryParse(parts[index], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out result[index])) return null;
        return result;
    }

    private static int CompareVersions(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var comparison = (index < left.Count ? left[index] : 0).CompareTo(index < right.Count ? right[index] : 0);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string DescribeRequired(ProjectRequirement requirement) =>
        $"requires {Display(requirement)}; capability unavailable";

    private static string DescribeMismatch(ProjectRequirement requirement, string? actual) =>
        $"requires {Display(requirement)}; worker reports {DisplayName(requirement.Type, requirement.Name)} {DisplayVersion(actual)}";

    private static string Display(ProjectRequirement requirement) =>
        $"{DisplayName(requirement.Type, requirement.Name)}{(requirement.Version is null ? "" : " " + requirement.Version)}";

    private static string DisplayName(string type, string name) => type switch
    {
        "runtime" when name == "dotnet" || name == ".net" => ".NET",
        "tool" when name == "docker" => "Docker",
        "service" => name.ToUpperInvariant() switch { "POSTGRESQL" => "PostgreSQL", _ => name },
        _ => name
    };

    private static string DisplayVersion(string? version) => version is null ? "no version" : version;
}

public sealed record WorkerEligibilityResult(bool IsEligible, IReadOnlyList<string> MissingRequirements);
