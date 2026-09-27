using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Loads a deliberately small dotenv format without evaluating shell syntax.</summary>
public static class ProjectEnvironmentFile
{
    private static readonly Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.Exists(fullPath))
            throw new InvalidDataException($"Configured environment file does not exist: {fullPath}");

        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
                throw new InvalidDataException($"Configured environment file is not a regular file: {fullPath}");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = new StreamReader(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            var lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    throw Malformed(lineNumber, "expected KEY=VALUE");
                var key = line[..separator];
                if (!VariableName.IsMatch(key))
                    throw Malformed(lineNumber, "invalid variable name");
                result[key] = line[(separator + 1)..];
            }
            return result;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Configured environment file could not be read: {fullPath}", ex);
        }
    }

    private static InvalidDataException Malformed(int line, string problem) =>
        new($"Environment file has a malformed entry on line {line}: {problem}.");
}
