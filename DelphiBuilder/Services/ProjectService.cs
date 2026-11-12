using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DelphiBuilder;

public sealed class ProjectService
{
    internal static readonly string[] VersionComponents = ["VerInfo_MajorVer", "VerInfo_MinorVer", "VerInfo_Release", "VerInfo_Build"];
    internal static readonly HashSet<string> VersionProperties = new(
        [.. VersionComponents, "VerInfo_IncludeVerInfo", "VerInfo_Keys"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionSelfReference = new(@"\$\(\s*VerInfo_Keys\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal sealed class EvaluationState
    {
        public required ProjectEvaluation Evaluation { get; init; }
        public string RawVersionKeys { get; init; } = "";
    }

    public ProjectEvaluation Load(string projectFile, string? configuration = null, string platform = "Win32",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        return Evaluate(ProjectDocument.Read(Path.GetFullPath(projectFile)), configuration, NormalizePlatform(platform),
            InitialProperties(environment)).Evaluation;
    }

    public void Save(string projectFile, string configuration, string platform, string version, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        var normalizedVersion = ValidateVersion(version);
        var normalizedOutput = outputPath?.Trim() ?? "";
        if (normalizedOutput.Length == 0) throw new ArgumentException("输出目录不能为空。", nameof(outputPath));
        platform = NormalizePlatform(platform);
        var document = ProjectDocument.Read(Path.GetFullPath(projectFile));
        var environment = InitialProperties(null);
        var original = Evaluate(document, configuration, platform, environment);
        if (ProjectExpressions.HasUnsupportedExpression(original.Evaluation.Properties.GetValueOrDefault("VerInfo_Keys", "")))
            throw new InvalidDataException("版本信息包含不支持的 MSBuild 表达式；为保留原有元数据，未修改项目文件。");
        if (ProjectExpressions.HasUnsupportedExpression(normalizedOutput))
            throw new ArgumentException("输出目录不支持 MSBuild 属性函数、项列表或项元数据。", nameof(outputPath));
        var expectedOutput = ProjectExpressions.Expand(normalizedOutput, original.Evaluation.Properties);
        if (expectedOutput.Unresolved.Count > 0)
            throw new ArgumentException($"输出目录含未定义或循环引用的属性：{string.Join("、", expectedOutput.Unresolved)}。未修改项目文件。", nameof(outputPath));
        if (expectedOutput.Value.Trim().Length == 0)
            throw new ArgumentException("输出目录展开后为空，未修改项目文件。", nameof(outputPath));

        var text = ProjectWriter.Update(document, original, normalizedVersion, normalizedOutput);
        var updated = document.WithText(text);
        var result = Evaluate(updated, original.Evaluation.Configuration, platform, environment).Evaluation;
        if (result.Version != normalizedVersion || result.OutputPath != expectedOutput.Value.Trim()
            || ReadVersionKey(result.Properties.GetValueOrDefault("VerInfo_Keys", ""), "ProductVersion") != normalizedVersion)
            throw new InvalidDataException("修改后的版本号或输出目录未能在所选配置和平台生效；项目可能包含后置覆盖或不支持的表达式，未修改原文件。");
        var components = normalizedVersion.Split('.');
        for (var index = 0; index < VersionComponents.Length; index++)
            if (result.Properties.GetValueOrDefault(VersionComponents[index]) != (index < components.Length ? components[index] : "0"))
                throw new InvalidDataException("修改后的数字版本字段未能生效，未修改原文件。");

        // A keyed Delphi configuration group also applies to its descendants. Never leak an edit into them.
        var configurations = original.Evaluation.Configurations.Where(item => !item.Name.Equals("Base", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name).Append(original.Evaluation.Configuration).Distinct(StringComparer.OrdinalIgnoreCase);
        var platforms = DiscoverPlatforms(document).Append(platform).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var otherConfiguration in configurations)
        foreach (var otherPlatform in platforms)
        {
            if (otherConfiguration.Equals(result.Configuration, StringComparison.OrdinalIgnoreCase)
                && otherPlatform.Equals(platform, StringComparison.OrdinalIgnoreCase)) continue;
            var before = Evaluate(document, otherConfiguration, otherPlatform, environment).Evaluation;
            var after = Evaluate(updated, otherConfiguration, otherPlatform, environment).Evaluation;
            if (before.MainSource != after.MainSource || !before.ResourceItems.SequenceEqual(after.ResourceItems)
                || before.Properties.Count != after.Properties.Count
                || before.Properties.Any(property => !after.Properties.TryGetValue(property.Key, out var value) || property.Value != value))
                throw new InvalidDataException($"修改会影响其他目标 {otherConfiguration}/{otherPlatform}；为保护配置继承，未修改原文件。");
        }
        document.SaveAtomically(text);
    }

    internal static EvaluationState Evaluate(ProjectDocument document, string? requestedConfiguration, string platform,
        IReadOnlyDictionary<string, string> environment)
    {
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var configurations = ReadConfigurations(document, warnings);
        var properties = CreateProperties(document, environment);
        if (string.IsNullOrWhiteSpace(requestedConfiguration))
        {
            var defaults = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase) { ["Platform"] = platform };
            string defaultKeys = "";
            EvaluateGroups(document, defaults, new HashSet<string>(), ref defaultKeys, false);
            requestedConfiguration = defaults.GetValueOrDefault("Config")?.Trim();
            if (string.IsNullOrEmpty(requestedConfiguration)) requestedConfiguration = defaults.GetValueOrDefault("Configuration")?.Trim();
        }
        var configuration = SelectConfiguration(configurations, requestedConfiguration);
        SeedConfiguration(properties, configurations, configuration, platform, warnings);
        var rawVersionKeys = EscapeXml(properties.GetValueOrDefault("VerInfo_Keys", ""));
        EvaluateGroups(document, properties, warnings, ref rawVersionKeys, true);

        foreach (var node in document.Root.Elements())
        {
            if (node.Name.Namespace != document.Namespace) continue;
            if (node.Name.LocalName == "Import")
                warnings.Add($"直接 DCC 编译不会执行 MSBuild 导入：{node.Attribute("Project")?.Value ?? "（未指定）"}。编译器环境由本程序单独加载。");
            else if (node.Name.LocalName is "ImportGroup" or "Target" or "Choose" or "ItemDefinitionGroup" or "UsingTask" or "Sdk")
                warnings.Add($"直接 DCC 编译不执行 MSBuild 的 {node.Name.LocalName} 节点；其中的设置或构建步骤未求值。");
        }
        if (document.Root.Attribute("Sdk") is { } sdk)
            warnings.Add($"直接 DCC 编译不执行 MSBuild SDK：{sdk.Value}。");

        var resourceItems = ReadResources(document, properties, warnings);
        var mainValue = properties.GetValueOrDefault("MainSource")?.Trim();
        if (string.IsNullOrEmpty(mainValue))
        {
            mainValue = Path.GetFileNameWithoutExtension(document.FilePath) + ".dpr";
            warnings.Add($"项目未指定 MainSource，使用同名主程序：{mainValue}。");
        }
        var mainExpansion = ProjectExpressions.Expand(mainValue, properties, false);
        AddUnresolved(mainExpansion, "MainSource", warnings);
        string mainSource;
        try { mainSource = Path.GetFullPath(mainExpansion.Value, Path.GetDirectoryName(document.FilePath)!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new InvalidDataException($"MainSource 路径无效：{mainExpansion.Value}", ex); }
        if (!File.Exists(mainSource)) warnings.Add($"主源文件不存在：{mainSource}");
        foreach (var property in properties)
            AddUnresolved(ProjectExpressions.Expand(property.Value, properties, false), property.Key, warnings);
        return new EvaluationState
        {
            Evaluation = new ProjectEvaluation
            {
                ProjectFile = document.FilePath, MainSource = mainSource, Configuration = configuration, Platform = platform,
                Configurations = configurations, Properties = properties, ResourceItems = resourceItems,
                Warnings = warnings.ToArray(), Version = ReadVersion(properties),
                OutputPath = properties.GetValueOrDefault("DCC_ExeOutput") is { } output && !string.IsNullOrWhiteSpace(output) ? output.Trim() : "."
            },
            RawVersionKeys = rawVersionKeys
        };
    }

    private static List<ProjectConfiguration> ReadConfigurations(ProjectDocument document, ISet<string> warnings)
    {
        var result = new List<ProjectConfiguration>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Children(document.Root, "ItemGroup").SelectMany(group => document.Children(group, "BuildConfiguration")))
        {
            var name = item.Attribute("Include")?.Value.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var key = document.Children(item, "Key").LastOrDefault()?.Value.Trim();
            if (string.IsNullOrEmpty(key)) key = name;
            var parent = document.Children(item, "CfgParent").LastOrDefault()?.Value.Trim();
            if (!names.Add(name)) { warnings.Add($"项目包含重复配置名称 {name}，使用首次声明。"); continue; }
            if (!keys.Add(key)) warnings.Add($"配置键 {key} 被多个配置共用，保存时将使用独立的名称和平台条件。");
            result.Add(new ProjectConfiguration(name, key, string.IsNullOrEmpty(parent) ? null : parent));
        }
        return result;
    }

    private static string SelectConfiguration(IReadOnlyList<ProjectConfiguration> configurations, string? requested)
    {
        var selectable = configurations.Where(item => !item.Name.Equals("Base", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            requested = requested.Trim();
            var match = selectable.FirstOrDefault(item => item.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (match == null && selectable.Length > 0)
                throw new InvalidDataException($"项目未定义配置“{requested}”。可用配置：{string.Join("、", selectable.Select(item => item.Name))}。");
            return match?.Name ?? requested;
        }
        return selectable.FirstOrDefault(item => item.Name.Equals("Debug", StringComparison.OrdinalIgnoreCase))?.Name
            ?? selectable.FirstOrDefault()?.Name ?? "Debug";
    }

    private static void SeedConfiguration(Dictionary<string, string> properties, IReadOnlyList<ProjectConfiguration> configurations,
        string configuration, string platform, ISet<string> warnings)
    {
        properties["Config"] = configuration;
        properties["Configuration"] = configuration;
        properties["Platform"] = platform;
        foreach (var item in configurations)
        {
            properties.Remove(item.Key);
            properties.Remove(item.Key + "_Win32");
            properties.Remove(item.Key + "_Win64");
            properties.Remove(item.Key + "_" + platform);
        }
        var byKey = configurations.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var current = configurations.FirstOrDefault(item => item.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current != null)
        {
            if (!visited.Add(current.Key)) { warnings.Add($"配置继承存在循环：{current.Key}。循环部分不再展开。"); break; }
            properties[current.Key] = "true";
            properties[current.Key + "_" + platform] = "true";
            if (current.ParentKey == null) break;
            if (!byKey.TryGetValue(current.ParentKey, out var parent))
            {
                properties[current.ParentKey] = "true";
                properties[current.ParentKey + "_" + platform] = "true";
                if (!current.ParentKey.Equals("Base", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"配置 {current.Name} 的父键 {current.ParentKey} 未声明 BuildConfiguration，仅启用该父键。");
                break;
            }
            current = parent;
        }
        var baseConfiguration = configurations.FirstOrDefault(item => item.Name.Equals("Base", StringComparison.OrdinalIgnoreCase));
        if (baseConfiguration != null)
        {
            properties[baseConfiguration.Key] = "true";
            properties[baseConfiguration.Key + "_" + platform] = "true";
        }
    }

    private static void EvaluateGroups(ProjectDocument document, Dictionary<string, string> properties,
        ISet<string> warnings, ref string rawVersionKeys, bool lockTarget)
    {
        foreach (var group in document.Children(document.Root, "PropertyGroup"))
        {
            if (!ProjectExpressions.Condition(group.Attribute("Condition")?.Value, properties, warnings)) continue;
            foreach (var property in group.Elements())
            {
                if (property.Name.Namespace != document.Namespace) continue;
                var name = property.Name.LocalName;
                if (lockTarget && IsTargetProperty(name)) continue;
                if (!lockTarget && name.Equals("Platform", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ProjectExpressions.Condition(property.Attribute("Condition")?.Value, properties, warnings)) continue;
                if (property.HasElements)
                {
                    warnings.Add($"属性 {name} 包含嵌套 XML，无法作为标量求值，已跳过。");
                    continue;
                }
                var raw = property.Value;
                if (ProjectExpressions.HasUnsupportedExpression(raw))
                {
                    properties[name] = raw;
                    warnings.Add($"属性 {name} 包含不支持的 MSBuild 属性函数、项列表或项元数据，保留原表达式但未求值：{raw}");
                    continue;
                }
                var expanded = ProjectExpressions.Expand(raw, properties);
                foreach (var unresolved in expanded.Unresolved)
                    if (!unresolved.Equals(name, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"属性 {name} 引用了未定义或循环引用的属性 {unresolved}，按 MSBuild 规则展开为空。");
                if (name.Equals("VerInfo_Keys", StringComparison.OrdinalIgnoreCase))
                {
                    var previous = rawVersionKeys;
                    var rawXml = property.Nodes().All(node => node is XText and not XCData)
                        ? document.InnerXml(property) : EscapeXml(raw);
                    rawVersionKeys = VersionSelfReference.Replace(rawXml, _ => previous);
                }
                properties[name] = expanded.Value;
            }
        }
    }

    private static List<ProjectResourceItem> ReadResources(ProjectDocument document, Dictionary<string, string> properties, ISet<string> warnings)
    {
        var result = new List<ProjectResourceItem>();
        foreach (var group in document.Children(document.Root, "ItemGroup"))
        {
            if (!ProjectExpressions.Condition(group.Attribute("Condition")?.Value, properties, warnings)) continue;
            foreach (var item in group.Elements())
            {
                if (item.Name.Namespace != document.Namespace || item.Name.LocalName is not ("RcCompile" or "RcItem")) continue;
                if (!ProjectExpressions.Condition(item.Attribute("Condition")?.Value, properties, warnings)) continue;
                var include = item.Attribute("Include")?.Value.Trim();
                if (string.IsNullOrEmpty(include)) continue;
                if (ProjectExpressions.HasUnsupportedExpression(include))
                { warnings.Add($"资源项 {item.Name.LocalName} 包含不支持的项表达式，无法展开：{include}"); continue; }
                var expanded = ProjectExpressions.Expand(include, properties);
                AddUnresolved(expanded, item.Name.LocalName + " Include", warnings);
                string? suffix = null;
                foreach (var metadata in document.Children(item, "Suffix"))
                {
                    if (!ProjectExpressions.Condition(metadata.Attribute("Condition")?.Value, properties, warnings)) continue;
                    if (ProjectExpressions.HasUnsupportedExpression(metadata.Value))
                    {
                        suffix = metadata.Value.Trim();
                        warnings.Add($"资源项 {item.Name.LocalName} 的 Suffix 含不支持的 MSBuild 表达式，保留原表达式但未求值：{suffix}");
                        continue;
                    }
                    var expandedSuffix = ProjectExpressions.Expand(metadata.Value, properties);
                    AddUnresolved(expandedSuffix, item.Name.LocalName + " Suffix", warnings);
                    suffix = expandedSuffix.Value.Trim();
                }
                foreach (var value in expanded.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    result.Add(new ProjectResourceItem(item.Name.LocalName, value, string.IsNullOrEmpty(suffix) ? null : suffix));
            }
        }
        return result;
    }

    private static Dictionary<string, string> InitialProperties(IReadOnlyDictionary<string, string>? environment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (item.Key is string key && item.Value is string value) result[key] = value;
        if (environment != null) foreach (var item in environment) result[item.Key] = item.Value;
        return result;
    }

    private static Dictionary<string, string> CreateProperties(ProjectDocument document, IReadOnlyDictionary<string, string> environment)
    {
        var properties = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(document.FilePath)!;
        properties["MSBuildProjectFullPath"] = document.FilePath;
        properties["MSBuildProjectDirectory"] = directory;
        properties["MSBuildProjectFile"] = Path.GetFileName(document.FilePath);
        properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(document.FilePath);
        properties["MSBuildProjectExtension"] = Path.GetExtension(document.FilePath);
        properties["MSBuildThisFileFullPath"] = document.FilePath;
        properties["MSBuildThisFileDirectory"] = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        properties["MSBuildThisFile"] = properties["MSBuildProjectFile"];
        properties["MSBuildThisFileName"] = properties["MSBuildProjectName"];
        properties["MSBuildThisFileExtension"] = properties["MSBuildProjectExtension"];
        return properties;
    }

    internal static bool IsTargetProperty(string name) => name.Equals("Config", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) || name.Equals("Platform", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> DiscoverPlatforms(ProjectDocument document)
    {
        yield return "Win32";
        yield return "Win64";
        foreach (var platform in document.Root.Descendants().Where(element => element.Name.LocalName == "Platform"))
        {
            var value = platform.Attribute("value")?.Value;
            if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
        }
        foreach (Match match in Regex.Matches(document.Text, "['\"]\\$\\(Platform\\)['\"]\\s*==\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase))
            yield return match.Groups[1].Value;
    }

    private static string NormalizePlatform(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        platform = platform.Trim();
        return platform.Equals("Win32", StringComparison.OrdinalIgnoreCase) ? "Win32"
            : platform.Equals("Win64", StringComparison.OrdinalIgnoreCase) ? "Win64" : platform;
    }

    private static void AddUnresolved(ProjectExpressions.Expansion expansion, string source, ISet<string> warnings)
    {
        foreach (var name in expansion.Unresolved) warnings.Add($"{source} 含未解析或循环引用的属性：{name}。");
    }

    private static string ReadVersion(IReadOnlyDictionary<string, string> properties)
    {
        var keys = properties.GetValueOrDefault("VerInfo_Keys", "");
        var version = ReadVersionKey(keys, "FileVersion") ?? ReadVersionKey(keys, "ProductVersion");
        if (version != null) return version;
        var components = VersionComponents.Select(name => properties.GetValueOrDefault(name)?.Trim()).ToArray();
        return components.Any(value => !string.IsNullOrEmpty(value))
            ? string.Join(".", components.Select(value => string.IsNullOrEmpty(value) ? "0" : value)) : "1.0.0.0";
    }

    internal static string? ReadVersionKey(string keys, string name)
    {
        var match = Regex.Match(keys, @"(?:^|;)\s*" + Regex.Escape(name) + @"\s*=\s*([^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && match.Groups[1].Value.Trim().Length > 0 ? match.Groups[1].Value.Trim() : null;
    }

    private static string ValidateVersion(string version)
    {
        var normalized = version?.Trim() ?? "";
        var components = normalized.Split('.');
        if (components.Length is < 1 or > 4 || components.Any(component => component.Length == 0
            || component.Any(character => character is < '0' or > '9')
            || !ushort.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException("版本号必须包含 1 至 4 个数字分量，各分量范围为 0 至 65535。", nameof(version));
        return normalized;
    }

    internal static string EscapeXml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
