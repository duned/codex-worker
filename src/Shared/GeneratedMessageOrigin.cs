namespace CodexProvisioning;

public enum CodexComponent
{
    Worker,
    Server
}

/// <summary>Public operational attribution, independent of authentication and internal node IDs.</summary>
public sealed record GeneratedMessageOrigin
{
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
        return $"{component} · {name}\n\n{content}";
    }
}
