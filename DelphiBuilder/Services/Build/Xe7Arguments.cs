namespace DelphiBuilder;

internal static class Xe7Arguments
{
    // The XE7 mappings mirror VSCodeVersion/delphi-versions/XE7.json.
    private static readonly (string Property, string Switch)[] BooleanSwitches =
    [
        ("DCC_BooleanEvaluation", "B"), ("DCC_Assertions", "C"), ("DCC_LongStrings", "H"),
        ("DCC_IOChecking", "I"), ("DCC_WriteableConst", "J"), ("DCC_LocalDebugSymbols", "L"),
        ("DCC_Optimize", "O"), ("DCC_OpenStringParams", "P"), ("DCC_OverflowChecking", "Q"),
        ("DCC_RangeChecking", "R"), ("DCC_TypedAddress", "T"), ("DCC_PentiumSafeDivide", "U"),
        ("DCC_StrictVarStrings", "V"), ("DCC_GenerateStackFrames", "W"), ("DCC_ExtendedSyntax", "X")
    ];
    private static readonly (string Property, string Switch, bool List)[] PathSwitches =
    [
        ("DCC_ObjPath", "-O", true), ("DCC_ExeOutput", "-E", false),
        ("DCC_DcuOutput", "-N0", false), ("DCC_BplOutput", "-LE", false), ("DCC_DcpOutput", "-LN", false)
    ];
    private static readonly HashSet<string> Metadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "DCC_DependencyCheckOutputName", "DCC_Description", "DCC_OutputType", "DCC_Platform", "DCC_SanitizedProjectName"
    };

    public static IReadOnlyList<string> Create(ProjectEvaluation evaluation, Xe7Environment bds,
        bool rebuild, IReadOnlyList<string> additionalArguments, List<string> warnings)
    {
        var directory = Path.GetDirectoryName(evaluation.ProjectFile)!;
        var arguments = new List<string> { "--no-config" };
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expanded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Get(string name)
        {
            handled.Add(name);
            if (expanded.TryGetValue(name, out var cached))
                return cached;
            var value = evaluation.Properties.TryGetValue(name, out var raw)
                ? BuildPaths.Expand(raw, evaluation.Properties).Trim() : "";
            if (BuildPaths.HasVariable(value))
                warnings.Add($"编译属性 {name} 含有未解析的引用：{value}");
            expanded[name] = value;
            return value;
        }
        bool? Boolean(string name)
        {
            var value = Get(name);
            var parsed = BuildPaths.Boolean(value);
            if (value.Length != 0 && parsed is null)
                warnings.Add($"不支持的布尔属性值：{name}={value}");
            return parsed;
        }
        void Value(string name, string flag)
        {
            var value = Get(name);
            if (value.Length != 0)
                arguments.Add(flag + value);
        }
        void Enum(string name)
        {
            var value = Get(name);
            if (value.Length == 0)
                return;
            var argument = (name, value) switch
            {
                ("DCC_DebugInformation", "0") => "-$D0",
                ("DCC_DebugInformation", "1") => "-$D1",
                ("DCC_DebugInformation", "2") => "-$D2",
                ("DCC_SymbolReferenceInfo", "0") => "-$Y-",
                ("DCC_SymbolReferenceInfo", "1") => "-$YD",
                ("DCC_SymbolReferenceInfo", "2") => "-$Y+",
                ("DCC_MapFile", "0") or ("DCC_MinEnumSize", "0") => "",
                ("DCC_MapFile", "1") => "-GS",
                ("DCC_MapFile", "2") => "-GP",
                ("DCC_MapFile", "3") => "-GD",
                ("DCC_MinEnumSize", "1") => "-$Z1",
                ("DCC_MinEnumSize", "2") => "-$Z2",
                ("DCC_MinEnumSize", "4") => "-$Z4",
                ("DCC_Align", "1") => "-$A1",
                ("DCC_Align", "2") => "-$A2",
                ("DCC_Align", "4") => "-$A4",
                ("DCC_Align", "8") => "-$A8",
                ("DCC_Align", "16") => "-$A16",
                _ => null
            };
            if (argument is null)
                warnings.Add($"XE7 不支持此属性值，未映射到编译参数：{name}={value}");
            else if (argument.Length != 0)
                arguments.Add(argument);
        }
        Value("DCC_Define", "-D");
        Value("DCC_UnitAlias", "-A");
        Value("DCC_Namespace", "-NS");
        var packages = Get("DCC_UsePackage");
        var usePackages = Boolean("UsePackages");
        var enabledPackages = Boolean("DCC_EnabledPackages");
        if ((usePackages == true || enabledPackages == true) && packages.Length != 0)
            arguments.Add("-LU" + packages);
        var debugDcus = Boolean("DCC_DebugDCUs") == true;
        var translatedDebug = Get("DCC_TranslatedDebugLibraryPath");
        var translatedLibrary = Get("DCC_TranslatedLibraryPath");
        var units = Get("DCC_UnitSearchPath");
        var unitPath = BuildPaths.MergePaths(directory, debugDcus ? translatedDebug : null,
            debugDcus ? bds.DebugDcuPath : null, translatedLibrary, units, bds.LibraryPath);
        if (unitPath.Length != 0)
            arguments.Add("-U" + unitPath);
        var includePath = BuildPaths.MergePaths(directory, Get("DCC_IncludePath"), unitPath);
        if (includePath.Length != 0)
            arguments.Add("-I" + includePath);
        var resourcePath = BuildPaths.MergePaths(directory, Get("DCC_TranslatedResourcePath"), Get("BRCC_OutputDir"),
            units, Get("DCC_ResourcePath"), bds.LibraryPath);
        if (resourcePath.Length != 0)
            arguments.Add("-R" + resourcePath);
        foreach (var (property, flag, list) in PathSwitches)
        {
            var value = Get(property);
            if (value.Length != 0)
                arguments.Add(flag + (list ? BuildPaths.MergePaths(directory, value) : BuildPaths.Resolve(value, directory)));
        }
        foreach (var (property, flag) in BooleanSwitches)
        {
            var value = Boolean(property);
            if (value is not null)
                arguments.Add("-$" + flag + (value.Value ? "+" : "-"));
        }
        Enum("DCC_DebugInformation");
        Enum("DCC_SymbolReferenceInfo");
        if (Boolean("DCC_DebugInfoInExe") == true)
        {
            arguments.Add("-V");
            arguments.Add("-VN");
        }
        Enum("DCC_MapFile");
        Enum("DCC_MinEnumSize");
        Enum("DCC_Align");
        if (rebuild)
            arguments.Add("-B");
        foreach (var argument in additionalArguments)
        {
            if (argument is null || argument.Contains('\0'))
                throw new InvalidOperationException("附加编译参数不能为 null 或包含空字符；每一项必须是一个完整参数。");
            arguments.Add(argument);
        }
        foreach (var (name, value) in evaluation.Properties)
            if (name.StartsWith("DCC_", StringComparison.OrdinalIgnoreCase) && value.Length != 0
                && !handled.Contains(name) && !Metadata.Contains(name))
                warnings.Add($"DCC 属性未映射到 XE7 编译参数：{name}={value}");
        foreach (var name in new[] { "PreBuildEvent", "PostBuildEvent", "PreLinkEvent" })
            if (evaluation.Properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                warnings.Add($"直接 DCC 编译不会执行 MSBuild 事件：{name}");
        arguments.Add(Path.GetFullPath(evaluation.MainSource));
        return arguments;
    }

    public static IReadOnlyList<string> ExpectedArtifacts(ProjectEvaluation evaluation)
    {
        var directory = Path.GetDirectoryName(evaluation.ProjectFile)!;
        string Property(string name) => evaluation.Properties.TryGetValue(name, out var value)
            ? BuildPaths.Expand(value, evaluation.Properties).Trim() : "";
        var output = Property("DCC_ExeOutput");
        var outputDirectory = output.Length == 0 ? directory : BuildPaths.Resolve(output, directory);
        var configuredName = Property("DCC_DependencyCheckOutputName");
        if (configuredName.Length != 0)
            return [BuildPaths.Resolve(configuredName, outputDirectory)];
        var package = Path.GetExtension(evaluation.MainSource).Equals(".dpk", StringComparison.OrdinalIgnoreCase);
        var library = Property("DCC_OutputType").Equals("library", StringComparison.OrdinalIgnoreCase)
            || Property("AppType").Equals("library", StringComparison.OrdinalIgnoreCase)
            || BuildPaths.Boolean(Property("GenDll")) == true;
        var extension = package ? ".bpl" : library ? ".dll" : ".exe";
        var bplOutput = Property("DCC_BplOutput");
        if (package)
            outputDirectory = bplOutput.Length == 0 ? directory : BuildPaths.Resolve(bplOutput, directory);
        return [Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(evaluation.MainSource) + extension)];
    }
}
