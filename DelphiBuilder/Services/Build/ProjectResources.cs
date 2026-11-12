using System.Text;
using System.Text.RegularExpressions;

namespace DelphiBuilder;

internal static class ProjectResources
{
    private static readonly Regex WildcardResource = new(
        @"\{\$\s*(?:R|RESOURCE)\s+[""']?\*\.res[""']?\s*\}|\(\*\$\s*(?:R|RESOURCE)\s+[""']?\*\.res[""']?\s*\*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly byte[] EmptyResource =
    [
        0, 0, 0, 0, 32, 0, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];
    private static readonly HashSet<string> HandledProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "BRCC_CompilerToUse", "BRCC_OutputDir", "BRCC_IncludePath", "BRCC_Defines",
        "BRCC_DeleteIncludePath", "BRCC_EnableMultiByte", "BRCC_Verbose", "BRCC_CodePage", "BRCC_Language"
    };

    public static async Task<(string? ProjectResource, IReadOnlyList<ResourceBuildStep> Steps)> CreatePlanAsync(
        ProjectEvaluation evaluation, Xe7Environment bds, bool enabled, string compilerOverride,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var source = await File.ReadAllTextAsync(evaluation.MainSource, Encoding.Latin1, cancellationToken).ConfigureAwait(false);
        var projectResource = WildcardResource.IsMatch(source) ? Path.ChangeExtension(evaluation.MainSource, ".res") : null;
        var rcCompile = evaluation.ResourceItems.Where(item => item.Kind.Equals("RcCompile", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rcItems = evaluation.ResourceItems.Where(item => item.Kind.Equals("RcItem", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projectResource is null && evaluation.ResourceItems.Count == 0)
            return (null, []);
        if (!enabled)
        {
            var required = evaluation.ResourceItems.Select(item => item.Include).ToList();
            if (projectResource is not null)
                required.Add(projectResource);
            warnings.Add($"已禁用资源预处理和版本资源同步；编译需要已有的 .res 文件：{string.Join("，", required)}");
            return (null, []);
        }
        if (rcItems.Length != 0)
        {
            warnings.Add($"RcItem 需要 Delphi IDE/MSBuild 的资源生成流程，直接 DCC 编译不会重建这些资源，也不会自动改写项目 .res：{string.Join("，", rcItems.Select(item => item.Include))}");
            projectResource = null;
        }
        foreach (var item in evaluation.ResourceItems)
            if (!item.Kind.Equals("RcCompile", StringComparison.OrdinalIgnoreCase)
                && !item.Kind.Equals("RcItem", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"不支持的资源项目类型，未自动处理：{item.Kind}，{item.Include}");
        if (projectResource is not null)
            _ = VersionResource.GetInfo(evaluation);
        if (rcCompile.Length == 0)
            return (projectResource, []);
        string Property(string name) => evaluation.Properties.TryGetValue(name, out var raw)
            ? BuildPaths.Expand(raw, evaluation.Properties).Trim() : "";
        var compilerToUse = Property("BRCC_CompilerToUse");
        if (compilerToUse.Length != 0 && !compilerToUse.Equals("brcc32", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持的资源编译器：{compilerToUse}。当前仅支持 Delphi XE7 BRCC32。");
        var executable = bds.ResolveResourceCompiler(compilerOverride);
        var directory = Path.GetDirectoryName(evaluation.ProjectFile)!;
        var outputSetting = Property("BRCC_OutputDir");
        var outputDirectory = outputSetting.Length == 0 ? directory : BuildPaths.Resolve(outputSetting, directory);
        var commonArguments = new List<string>();
        var includes = BuildPaths.MergePaths(directory, Property("BRCC_IncludePath"), Property("DCC_IncludePath"),
            Property("DCC_TranslatedLibraryPath"), Property("DCC_UnitSearchPath"), bds.LibraryPath);
        if (includes.Length != 0)
            commonArguments.Add("-i" + includes);
        var defines = Property("BRCC_Defines") + ";" + Property("DCC_Define");
        foreach (var define in defines.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
            commonArguments.Add("-d" + define);
        void Boolean(string property, string argument)
        {
            var value = Property(property);
            var parsed = BuildPaths.Boolean(value);
            if (parsed == true)
                commonArguments.Add(argument);
            else if (value.Length != 0 && parsed is null)
                warnings.Add($"不支持的资源布尔属性值：{property}={value}");
        }
        Boolean("BRCC_DeleteIncludePath", "-x");
        Boolean("BRCC_EnableMultiByte", "-m");
        Boolean("BRCC_Verbose", "-v");
        var codePage = Property("BRCC_CodePage");
        if (codePage.Length != 0)
            commonArguments.Add("-c" + codePage);
        var language = Property("BRCC_Language");
        if (language.Length != 0)
            commonArguments.Add("-l" + language);
        foreach (var (name, value) in evaluation.Properties)
            if (name.StartsWith("BRCC_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value)
                && !HandledProperties.Contains(name))
                warnings.Add($"BRCC 属性未映射到资源编译参数：{name}={value}");
        var steps = new List<ResourceBuildStep>(rcCompile.Length);
        foreach (var item in rcCompile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var include = BuildPaths.Expand(item.Include, evaluation.Properties);
            var suffix = BuildPaths.Expand(item.Suffix ?? "", evaluation.Properties);
            var input = BuildPaths.Resolve(include, directory);
            var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + suffix + ".res");
            var arguments = new List<string>(commonArguments.Count + 2);
            arguments.AddRange(commonArguments);
            arguments.Add("-fo" + output);
            arguments.Add(input);
            steps.Add(new ResourceBuildStep(executable, arguments, input, output));
        }
        return (projectResource, steps);
    }

    public static async Task<bool> EnsureAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
            return false;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        await using (stream)
        {
            // Once created, write the complete null header even if cancellation arrives mid-write.
            await stream.WriteAsync(EmptyResource, CancellationToken.None).ConfigureAwait(false);
        }
        return true;
    }

    public static ReadOnlySpan<byte> EmptyHeader => EmptyResource;
}
