using System.Text.RegularExpressions;

namespace DelphiBuilder;

internal static class BuildPaths
{
    private static readonly Regex Variable = new(@"\$\(([^)]+)\)|%([^%]+)%", RegexOptions.Compiled);

    public static string Expand(string value, IReadOnlyDictionary<string, string> properties)
    {
        for (var pass = 0; pass < 16; pass++)
        {
            var next = Variable.Replace(value, match =>
            {
                var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                return properties.TryGetValue(name, out var replacement) ? replacement : match.Value;
            });
            if (next == value)
                break;
            value = next;
        }
        return value;
    }

    public static bool HasVariable(string value) => Variable.IsMatch(value);

    public static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    public static string Resolve(string value, string directory) => Path.GetFullPath(Unquote(value), directory);

    public static string MergePaths(string directory, params string?[] values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            foreach (var entry in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Resolve(entry, directory);
                if (seen.Add(path))
                    paths.Add(path);
            }
        }
        return string.Join(';', paths);
    }

    public static bool? Boolean(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => null
    };
}
