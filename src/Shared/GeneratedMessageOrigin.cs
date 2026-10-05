namespace CodexProvisioning;

public enum CodexComponent
{
    Worker,
    Server
}

/// <summary>Public operational attribution, independent of authentication and internal node IDs.</summary>
public sealed record GeneratedMessageOrigin
{
    public const string WorkerMarker = "<!-- codex-generated:v1 component=worker -->";
    public const string ServerMarker = "<!-- codex-generated:v1 component=server -->";

    /// <summary>Only explicit product metadata establishes automatic provenance, never the author or visible heading.</summary>
    public static bool IsGenerated(string content) => content.Split('\n')
        .Any(line => line.TrimEnd('\r') is WorkerMarker or ServerMarker);

    public CodexComponent Component { get; }
    public string DisplayName { get; }

    public GeneratedMessageOrigin(CodexComponent component, string displayName)
    {
        if (component is not (CodexComponent.Worker or CodexComponent.Server))
            throw new ArgumentOutOfRangeException(nameof(component));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Component = component;
        DisplayName = displayName;
    }

    public string Format(string content)
    {
        // Encode node names as text so Markdown, HTML and mentions cannot change the report.
        var name = new System.Text.StringBuilder();
        foreach (var character in DisplayName)
        {
            if (char.IsControl(character)) name.Append(' ');
            else if ("\\`*_{}[]()#+!|~@".Contains(character)) name.Append($"&#{(int)character};");
            else name.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => character.ToString()
            });
        }
        var component = Component == CodexComponent.Worker ? "🤖 Codex Worker" : "🧭 Codex Server";
        var marker = Component == CodexComponent.Worker ? WorkerMarker : ServerMarker;
        return $"{component} · {name}\n{marker}\n\n{content}";
    }
}
