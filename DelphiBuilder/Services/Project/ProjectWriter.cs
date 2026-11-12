using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DelphiBuilder;

internal static class ProjectWriter
{
    private const string LegacyVersionLabel = "DelphiDccBuilderVersion";
    private static readonly Regex SavedPropertyReference = new(
        @"\$\(\s*(?:VerInfo_(?:MajorVer|MinorVer|Release|Build|IncludeVerInfo|Keys)|DCC_ExeOutput)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Update(ProjectDocument document, ProjectService.EvaluationState state, string version, string outputPath)
    {
        var evaluation = state.Evaluation;
        var configuration = evaluation.Configurations.FirstOrDefault(item => item.Name.Equals(evaluation.Configuration, StringComparison.OrdinalIgnoreCase));
        var allowKey = configuration != null && IsExclusiveKey(evaluation.Configurations, configuration);
        bool Matches(XElement group) => Targets(group.Attribute("Condition")?.Value, evaluation.Configuration,
            allowKey ? configuration!.Key : null, evaluation.Platform);
        var groups = document.Children(document.Root, "PropertyGroup").ToArray();
        var legacy = groups.Where(group => group.Attribute("Label")?.Value == LegacyVersionLabel && Matches(group)).ToHashSet();
        var candidates = groups.Where(group => !legacy.Contains(group) && Matches(group)).ToArray();
        var target = candidates.LastOrDefault();
        if (target != null)
        {
            var targetEnd = document.Span(target).End;
            var warnings = new HashSet<string>(StringComparer.Ordinal);
            if (groups.Any(group => document.Span(group).Start > targetEnd && !legacy.Contains(group)
                && ProjectExpressions.Condition(group.Attribute("Condition")?.Value, evaluation.Properties, warnings)
                && group.Elements().Any(property => property.Name.Namespace == document.Namespace && !property.HasElements
                    && (ProjectService.VersionProperties.Contains(property.Name.LocalName)
                        || property.Name.LocalName.Equals("DCC_ExeOutput", StringComparison.OrdinalIgnoreCase))
                    && ProjectExpressions.Condition(property.Attribute("Condition")?.Value, evaluation.Properties, warnings))))
                target = null;
        }

        var changes = new List<ProjectDocument.TextChange>();
        IEnumerable<XElement> obsoleteGroups = legacy;
        if (candidates.Any(group => group != target) && !HasSavedPropertyReferences(document.Root))
            obsoleteGroups = legacy.Concat(candidates.Where(group => group != target));
        foreach (var group in obsoleteGroups)
        {
            var includeOutput = !legacy.Contains(group);
            var editedNodes = group.Elements().Where(property => property.Name.Namespace == document.Namespace
                && (ProjectService.VersionProperties.Contains(property.Name.LocalName)
                    || includeOutput && property.Name.LocalName.Equals("DCC_ExeOutput", StringComparison.OrdinalIgnoreCase))).ToHashSet();
            var hasOtherContent = group.Nodes().Any(node => node switch
            {
                XElement element => !editedNodes.Contains(element),
                XText text => !string.IsNullOrWhiteSpace(text.Value),
                _ => true
            });
            if (!hasOtherContent) changes.Add(document.RemoveElement(group));
            else foreach (var property in editedNodes) changes.Add(document.RemoveElement(property));
        }

        var components = version.Split('.');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ProjectService.VersionComponents.Length; index++)
            values[ProjectService.VersionComponents[index]] = index < components.Length ? components[index] : "0";
        values["VerInfo_IncludeVerInfo"] = ProjectService.EscapeXml(
            string.IsNullOrWhiteSpace(evaluation.Properties.GetValueOrDefault("VerInfo_IncludeVerInfo"))
                ? "true" : evaluation.Properties["VerInfo_IncludeVerInfo"].Trim());
        values["VerInfo_Keys"] = UpdateVersionKeys(state.RawVersionKeys, version);
        values["DCC_ExeOutput"] = ProjectService.EscapeXml(outputPath);
        if (target == null) InsertGroup(document, values, evaluation.Configuration, evaluation.Platform, changes);
        else UpdateGroup(document, target, values, changes);
        return document.Apply(changes);
    }

    private static bool HasSavedPropertyReferences(XElement root)
    {
        // Earlier assignments can feed later expressions even when a final group overrides them.
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
                if (SavedPropertyReference.IsMatch(attribute.Value)) return true;
            foreach (var node in element.Nodes())
                if (node is XText text && SavedPropertyReference.IsMatch(text.Value)) return true;
        }
        return false;
    }

    private static bool IsExclusiveKey(IReadOnlyList<ProjectConfiguration> configurations, ProjectConfiguration selected)
    {
        if (selected.Name.Equals("Base", StringComparison.OrdinalIgnoreCase)) return false;
        var byKey = configurations.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var other in configurations)
        {
            if (ReferenceEquals(other, selected)) continue;
            var current = other;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (visited.Add(current.Key))
            {
                if (current.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase)) return false;
                if (current.ParentKey == null) break;
                if (current.ParentKey.Equals(selected.Key, StringComparison.OrdinalIgnoreCase)) return false;
                if (!byKey.TryGetValue(current.ParentKey, out var parent)) break;
                current = parent;
            }
        }
        return true;
    }

    private static bool Targets(string? condition, string configuration, string? key, string platform)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;
        var normalized = Normalize(condition);
        var platformCondition = $"'$(Platform)'=={Quote(platform)}";
        var configurationConditions = new List<string>
        {
            $"'$(Config)'=={Quote(configuration)}", $"'$(Configuration)'=={Quote(configuration)}"
        };
        if (key != null)
        {
            if (normalized == Normalize($"'$({key}_{platform})'!=''")
                || normalized == Normalize($"'$({key}_{platform})'=='true'")) return true;
            configurationConditions.Add($"'$({key})'!=''");
            configurationConditions.Add($"'$({key})'=='true'");
        }
        foreach (var value in configurationConditions)
            if (normalized == Normalize($"{value} and {platformCondition}")
                || normalized == Normalize($"{platformCondition} and {value}")) return true;
        return normalized == Normalize($"'$(Config)|$(Platform)'=={Quote(configuration + "|" + platform)}")
            || normalized == Normalize($"'$(Configuration)|$(Platform)'=={Quote(configuration + "|" + platform)}");
    }

    private static string Normalize(string condition)
    {
        var result = new StringBuilder(condition.Length);
        for (var index = 0; index < condition.Length; index++)
        {
            var value = condition[index];
            if (value is '\'' or '"')
            {
                var quote = value;
                result.Append('\'');
                while (++index < condition.Length)
                {
                    value = condition[index];
                    if (value == quote)
                    {
                        if (index + 1 < condition.Length && condition[index + 1] == quote)
                        { result.Append(quote); index++; continue; }
                        break;
                    }
                    result.Append(char.ToLowerInvariant(value));
                }
                result.Append('\'');
            }
            else if (!char.IsWhiteSpace(value) && value is not '(' and not ')') result.Append(char.ToLowerInvariant(value));
        }
        return result.ToString();
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void UpdateGroup(ProjectDocument document, XElement group, IReadOnlyDictionary<string, string> values,
        List<ProjectDocument.TextChange> changes)
    {
        var span = document.Span(group);
        var groupIndent = document.Indent(group);
        var childIndent = group.Elements().Select(document.Indent).FirstOrDefault(indent => indent.Length > groupIndent.Length)
            ?? groupIndent + "  ";
        var prefix = document.Prefix(group);
        if (span.Empty)
        {
            var opening = document.Text[span.Start..span.OpenEnd];
            var text = new StringBuilder(opening[..^2]).Append('>').Append(document.Newline);
            foreach (var value in values) AppendProperty(text, prefix, childIndent, value.Key, value.Value, document.Newline);
            text.Append(groupIndent).Append("</").Append(span.QualifiedName).Append('>');
            changes.Add(new ProjectDocument.TextChange(span.Start, span.End - span.Start, text.ToString()));
            return;
        }
        var additions = new StringBuilder();
        foreach (var value in values)
        {
            var property = group.Elements().LastOrDefault(element => element.Name.Namespace == document.Namespace
                && element.Name.LocalName.Equals(value.Key, StringComparison.OrdinalIgnoreCase));
            if (property == null || !string.IsNullOrWhiteSpace(property.Attribute("Condition")?.Value)
                || property.Nodes().Any(node => node is not XText))
            {
                AppendProperty(additions, prefix, childIndent, value.Key, value.Value, document.Newline);
                continue;
            }
            var propertySpan = document.Span(property);
            if (propertySpan.Empty)
            {
                var opening = document.Text[propertySpan.Start..propertySpan.OpenEnd];
                changes.Add(new ProjectDocument.TextChange(propertySpan.Start, propertySpan.End - propertySpan.Start,
                    opening[..^2] + ">" + value.Value + "</" + propertySpan.QualifiedName + ">"));
            }
            else
            {
                changes.Add(new ProjectDocument.TextChange(propertySpan.OpenEnd, propertySpan.CloseStart - propertySpan.OpenEnd, value.Value));
            }
        }
        if (additions.Length == 0) return;
        var insertion = document.LineStart(span.CloseStart);
        if (!ProjectDocument.IsIndent(document.Text.AsSpan(insertion, span.CloseStart - insertion))) insertion = span.CloseStart;
        var needsNewline = insertion > 0 && document.Text[insertion - 1] is not '\r' and not '\n';
        changes.Add(new ProjectDocument.TextChange(insertion, 0,
            (needsNewline ? document.Newline : "") + additions + (insertion == span.CloseStart ? groupIndent : "")));
    }

    private static void InsertGroup(ProjectDocument document, IReadOnlyDictionary<string, string> values,
        string configuration, string platform, List<ProjectDocument.TextChange> changes)
    {
        var rootSpan = document.Span(document.Root);
        var firstGroup = document.Children(document.Root, "PropertyGroup").FirstOrDefault();
        var indent = firstGroup == null ? document.Indent(document.Root) + "  " : document.Indent(firstGroup);
        var prefix = document.Prefix(document.Root);
        var condition = $"'$(Config)'=={Quote(configuration)} and '$(Platform)'=={Quote(platform)}";
        var text = new StringBuilder().Append(indent).Append('<').Append(prefix).Append("PropertyGroup Condition=\"")
            .Append(ProjectService.EscapeXml(condition).Replace("\"", "&quot;", StringComparison.Ordinal)).Append("\">").Append(document.Newline);
        foreach (var value in values) AppendProperty(text, prefix, indent + "  ", value.Key, value.Value, document.Newline);
        text.Append(indent).Append("</").Append(prefix).Append("PropertyGroup>").Append(document.Newline);
        if (rootSpan.Empty)
        {
            var opening = document.Text[rootSpan.Start..rootSpan.OpenEnd];
            changes.Add(new ProjectDocument.TextChange(rootSpan.Start, rootSpan.End - rootSpan.Start,
                opening[..^2] + ">" + document.Newline + text + document.Indent(document.Root) + "</" + rootSpan.QualifiedName + ">"));
            return;
        }
        var lastPropertyGroup = document.Children(document.Root, "PropertyGroup").LastOrDefault();
        var after = lastPropertyGroup == null ? rootSpan.OpenEnd : document.Span(lastPropertyGroup).End;
        var before = document.Root.Elements().FirstOrDefault(element => document.Span(element).Start >= after
            && element.Name.Namespace == document.Namespace && element.Name.LocalName is "Import" or "ImportGroup" or "ProjectExtensions");
        var insertion = before == null ? rootSpan.CloseStart : document.Span(before).Start;
        var lineStart = document.LineStart(insertion);
        if (ProjectDocument.IsIndent(document.Text.AsSpan(lineStart, insertion - lineStart))) insertion = lineStart;
        if (insertion > 0 && document.Text[insertion - 1] is not '\r' and not '\n') text.Insert(0, document.Newline);
        changes.Add(new ProjectDocument.TextChange(insertion, 0, text.ToString()));
    }

    private static void AppendProperty(StringBuilder text, string prefix, string indent, string name, string rawXml, string newline)
    {
        text.Append(indent).Append('<').Append(prefix).Append(name).Append('>').Append(rawXml)
            .Append("</").Append(prefix).Append(name).Append('>').Append(newline);
    }

    private static string UpdateVersionKeys(string rawXml, string version)
    {
        foreach (var name in new[] { "FileVersion", "ProductVersion" })
        {
            // An entity's semicolon is not a version-info key separator. Preserve every unrelated byte.
            var pattern = @"(^|;)([ \t]*" + name + @"[ \t]*=[ \t]*)(?:(?:&(?:#[xX][0-9a-fA-F]+|#[0-9]+|[A-Za-z][A-Za-z0-9]*);)|[^;])*";
            var matched = false;
            rawXml = Regex.Replace(rawXml, pattern, match =>
            {
                matched = true;
                var trailing = Regex.Match(match.Value, @"\s*$").Value;
                return match.Groups[1].Value + match.Groups[2].Value + version + trailing;
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matched) continue;
            var trailingWhitespace = Regex.Match(rawXml, @"\s*$").Value;
            var body = rawXml[..(rawXml.Length - trailingWhitespace.Length)];
            rawXml = body + (body.Length > 0 && !body.EndsWith(';') ? ";" : "") + name + "=" + version + trailingWhitespace;
        }
        return rawXml;
    }
}
