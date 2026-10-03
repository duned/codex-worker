namespace CodexProvisioning;

/// <summary>Common shape of declared requirements and advertised capabilities.</summary>
public interface ICapabilityDescriptor
{
    string Type { get; }
    string Name { get; }
    string? Version { get; }
    string? Scope { get; }
}

/// <summary>Deterministic matching for centrally declared project requirements.</summary>
public static class CapabilityEligibility
{
    public static IEnumerable<ICapabilityDescriptor> ForProject(IEnumerable<ICapabilityDescriptor>? requirements,
        string repository) => (requirements ?? []).Concat([
            new Descriptor("authentication", "github-api", null, repository),
            new Descriptor("authentication", "git-repository", null, repository),
            new Descriptor("agent-provider", "codex", null, null)
        ]);

    public static CapabilityEligibilityResult Evaluate(IEnumerable<ICapabilityDescriptor>? requirements,
        IEnumerable<ICapabilityDescriptor>? capabilities, IReadOnlyList<CapabilityState>? inventory = null)
    {
        var available = (capabilities ?? []).Select(Normalize).ToArray();
        var missing = new List<string>();
        if (inventory is not null) missing.AddRange(CapabilityCatalog.ExecutionReadiness(inventory).BlockingReasons);
        foreach (var requirement in requirements ?? [])
        {
            var normalized = Normalize(requirement);
            if (inventory is not null && normalized.Type == "tool" &&
                CapabilityCatalog.Definitions.FirstOrDefault(definition => definition.Id == normalized.Name) is { } provider)
            {
                var readiness = CapabilityCatalog.Evaluate(provider,
                    inventory.FirstOrDefault(state => state.Id == provider.Id) ?? CapabilityCatalog.Unknown(provider));
                if (!readiness.Available)
                {
                    missing.Add($"requires {Display(normalized)}; {string.Join(", ", readiness.BlockingReasons)}");
                    continue;
                }
            }
            var matches = available.Where(capability => capability.Type == normalized.Type && capability.Name == normalized.Name &&
                (normalized.Scope is null || string.Equals(capability.Scope, normalized.Scope, StringComparison.OrdinalIgnoreCase))).ToArray();
            var matching = matches.FirstOrDefault();
            if (matching is null)
            {
                missing.Add(DescribeRequired(normalized));
                continue;
            }
            if (normalized.Version is null) continue;
            if (!matches.Any(capability => capability.Version is not null && VersionSatisfies(normalized.Version, capability.Version)))
                missing.Add(DescribeMismatch(normalized, matching.Version));
        }
        return new CapabilityEligibilityResult(missing.Count == 0, missing);
    }

    private static Descriptor Normalize(ICapabilityDescriptor capability) => new(
        capability.Type.Trim().ToLowerInvariant(), CanonicalName(capability.Type, capability.Name),
        capability.Version?.Trim(), capability.Scope?.Trim().ToLowerInvariant());

    private sealed record Descriptor(string Type, string Name, string? Version, string? Scope) : ICapabilityDescriptor;

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

    private static string DescribeRequired(ICapabilityDescriptor requirement) =>
        $"requires {Display(requirement)}; capability unavailable";

    private static string DescribeMismatch(ICapabilityDescriptor requirement, string? actual) =>
        $"requires {Display(requirement)}; worker reports {DisplayName(requirement.Type, requirement.Name)} {DisplayVersion(actual)}";

    private static string Display(ICapabilityDescriptor requirement) =>
        $"{DisplayName(requirement.Type, requirement.Name)}{(requirement.Scope is null ? "" : " for " + requirement.Scope)}{(requirement.Version is null ? "" : " " + requirement.Version)}";

    private static string DisplayName(string type, string name) => type switch
    {
        "runtime" when name == "dotnet" || name == ".net" => ".NET",
        "tool" when name == "docker" => "Docker",
        "service" => name.ToUpperInvariant() switch { "POSTGRESQL" => "PostgreSQL", _ => name },
        _ => name
    };

    private static string DisplayVersion(string? version) => version is null ? "no version" : version;
}

public sealed record CapabilityEligibilityResult(bool IsEligible, IReadOnlyList<string> MissingRequirements);
