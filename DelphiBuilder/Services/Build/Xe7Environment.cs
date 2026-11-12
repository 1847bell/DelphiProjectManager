using System.Collections;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DelphiBuilder;

internal sealed record Xe7Environment(string CompilerPath, string RootDirectory, string RsVarsPath,
    bool RsVarsFound, Dictionary<string, string> Variables, string LibraryPath,
    string DebugDcuPath, IReadOnlyList<string> Warnings)
{
    private const string RegistryPath = @"Software\Embarcadero\BDS\15.0";
    private static readonly Regex SetDeclaration = new(@"^\s*@?SET\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<Xe7Environment> ResolveAsync(string compilerOverride, string rsVarsOverride,
        string platform, IReadOnlyDictionary<string, string> overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string name && entry.Value is string value)
                inherited[name] = value;
        var configured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
                throw new InvalidOperationException($"环境变量名称或值无效：{name}");
            configured[name] = value;
        }
        var discovery = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in configured)
            discovery[name] = BuildPaths.Expand(value, discovery);

        var roots = new List<string>();
        if (discovery.TryGetValue("BDS", out var bds) && !string.IsNullOrWhiteSpace(bds))
            roots.Add(BuildPaths.Resolve(BuildPaths.Expand(bds, discovery), Environment.CurrentDirectory));
        var registryRoots = ReadRegistryRoots(warnings);
        roots.AddRange(registryRoots);
        if (!string.IsNullOrWhiteSpace(rsVarsOverride))
        {
            var configuredScript = BuildPaths.Resolve(BuildPaths.Expand(rsVarsOverride, discovery), Environment.CurrentDirectory);
            if (!File.Exists(configuredScript))
                throw new FileNotFoundException($"配置的 rsvars.bat 不存在：{configuredScript}", configuredScript);
            var scriptRoot = InferRoot(configuredScript);
            if (scriptRoot is not null)
                roots.Add(scriptRoot);
        }
        foreach (var name in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            if (discovery.TryGetValue(name, out var directory) && !string.IsNullOrWhiteSpace(directory))
                roots.Add(Path.Combine(BuildPaths.Unquote(directory), "Embarcadero", "Studio", "15.0"));
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
                continue;
            roots.Add(Path.Combine(drive.Name, "Program Files (x86)", "Embarcadero", "Studio", "15.0"));
            roots.Add(Path.Combine(drive.Name, "Program Files", "Embarcadero", "Studio", "15.0"));
        }
        roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var compilerName = platform == "Win64" ? "DCC64.exe" : "DCC32.exe";
        string compiler;
        if (!string.IsNullOrWhiteSpace(compilerOverride))
        {
            compiler = BuildPaths.Resolve(BuildPaths.Expand(compilerOverride, discovery), Environment.CurrentDirectory);
            if (!File.Exists(compiler))
                throw new FileNotFoundException($"配置的 {compilerName} 编译器不存在：{compiler}", compiler);
        }
        else
        {
            compiler = roots.Select(root => Path.Combine(root, "bin", compilerName)).FirstOrDefault(File.Exists) ?? "";
            if (compiler.Length == 0)
                throw new FileNotFoundException($"未找到 Delphi XE7 的 {compilerName}。请在设置中指定 {platform} 编译器路径。已查找：\r\n{string.Join("\r\n", roots.Select(root => Path.Combine(root, "bin", compilerName)))}");
            compiler = Path.GetFullPath(compiler);
        }
        var rootDirectory = InferRoot(compiler) ?? registryRoots.FirstOrDefault()
            ?? Path.GetDirectoryName(compiler)!;
        var rsVarsPath = string.IsNullOrWhiteSpace(rsVarsOverride)
            ? Path.Combine(rootDirectory, "bin", "rsvars.bat")
            : BuildPaths.Resolve(BuildPaths.Expand(rsVarsOverride, discovery), Environment.CurrentDirectory);
        var rsVarsFound = File.Exists(rsVarsPath);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0)
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        var derived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BDS"] = rootDirectory,
            ["BDSBIN"] = Path.Combine(rootDirectory, "bin"),
            ["BDSINCLUDE"] = Path.Combine(rootDirectory, "include"),
            ["BDSLIB"] = Path.Combine(rootDirectory, "lib"),
            ["BDSUSERDIR"] = Path.Combine(documents, "Embarcadero", "Studio", "15.0"),
            ["BDSCOMMONDIR"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Embarcadero", "Studio", "15.0"),
            ["DELPHI"] = rootDirectory
        };
        var variables = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Merge(variables, derived, touched);
        if (rsVarsFound)
        {
            var content = await File.ReadAllTextAsync(rsVarsPath, ToolOutputEncoding.Get("cp936"), cancellationToken).ConfigureAwait(false);
            foreach (var line in content.Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trimmed = line.Trim();
                var match = SetDeclaration.Match(trimmed);
                if (!match.Success)
                {
                    if (trimmed.Length != 0 && !trimmed.StartsWith("::") && !trimmed.StartsWith(":")
                        && !Regex.IsMatch(trimmed, @"^@?(?:REM(?:\s|$)|ECHO(?:\s|$))", RegexOptions.IgnoreCase))
                        warnings.Add($"rsvars.bat 中的非 SET 指令未执行：{trimmed}");
                    continue;
                }
                var assignment = match.Groups[1].Value.Trim();
                if (assignment.StartsWith('"') && assignment.EndsWith('"'))
                    assignment = assignment[1..^1];
                var separator = assignment.IndexOf('=');
                if (separator <= 0 || assignment.StartsWith("/", StringComparison.Ordinal))
                {
                    warnings.Add($"rsvars.bat 中的 SET 指令不受支持：{trimmed}");
                    continue;
                }
                var name = assignment[..separator].Trim();
                var value = BuildPaths.Unquote(assignment[(separator + 1)..]);
                value = value.Replace("%~dp0", Path.GetDirectoryName(rsVarsPath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                variables[name] = BuildPaths.Expand(value, variables);
                touched.Add(name);
            }
        }
        else
            warnings.Add($"未找到 rsvars.bat，使用注册表和编译器目录推导的环境：{rsVarsPath}");
        // An explicit compiler must not inherit another installation's BDS root from rsvars.
        Merge(variables, derived.Where(pair => pair.Key != "BDSCOMMONDIR"), touched);
        Merge(variables, ReadRegistry(RegistryPath + @"\Environment Variables", warnings), touched);
        // User settings deliberately take precedence over all discovered values.
        Merge(variables, configured, touched);
        var library = ReadRegistry(RegistryPath + @"\Library\" + platform, warnings);
        var libraryProperties = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase) { ["Platform"] = platform };
        var libraryPath = BuildPaths.Expand(library.GetValueOrDefault("Search Path", ""), libraryProperties).Trim();
        var debugDcuPath = BuildPaths.Expand(library.GetValueOrDefault("Debug DCU Path", ""), libraryProperties).Trim();
        var effectiveLib = variables.GetValueOrDefault("BDSLIB", Path.Combine(rootDirectory, "lib"));
        if (libraryPath.Length == 0)
        {
            var conventional = Path.Combine(BuildPaths.Unquote(effectiveLib), platform.ToLowerInvariant(), "release");
            if (Directory.Exists(conventional))
            {
                libraryPath = conventional;
                warnings.Add($"未读取到 XE7 {platform} 注册表库路径，使用安装目录的默认库：{libraryPath}");
            }
            else
                warnings.Add($"未读取到 XE7 {platform} 的库搜索路径，请检查 BDS 15.0 注册表设置。");
        }
        if (debugDcuPath.Length == 0)
        {
            var conventional = Path.Combine(BuildPaths.Unquote(effectiveLib), platform.ToLowerInvariant(), "debug");
            if (Directory.Exists(conventional))
                debugDcuPath = conventional;
        }
        foreach (var name in touched)
            if (BuildPaths.HasVariable(variables[name]))
                warnings.Add($"环境变量 {name} 含有未解析的引用：{variables[name]}");
        if (BuildPaths.HasVariable(libraryPath))
            warnings.Add($"BDS 库搜索路径含有未解析的引用：{libraryPath}");
        if (BuildPaths.HasVariable(debugDcuPath))
            warnings.Add($"BDS 调试 DCU 路径含有未解析的引用：{debugDcuPath}");
        cancellationToken.ThrowIfCancellationRequested();
        return new Xe7Environment(compiler, rootDirectory, rsVarsPath, rsVarsFound, variables,
            libraryPath, debugDcuPath, warnings);
    }

    public string ResolveResourceCompiler(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var resolved = BuildPaths.Resolve(BuildPaths.Expand(configuredPath, Variables), Environment.CurrentDirectory);
            if (!File.Exists(resolved))
                throw new FileNotFoundException($"配置的 BRCC32.exe 不存在：{resolved}", resolved);
            return resolved;
        }
        var candidates = new List<string>
        {
            Path.Combine(Path.GetDirectoryName(CompilerPath)!, "brcc32.exe"),
            Path.Combine(RootDirectory, "bin", "brcc32.exe")
        };
        if (Variables.TryGetValue("BDSBIN", out var bin) && !string.IsNullOrWhiteSpace(bin))
            candidates.Add(Path.Combine(BuildPaths.Unquote(bin), "brcc32.exe"));
        foreach (var directory in Variables.GetValueOrDefault("PATH", "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(BuildPaths.Unquote(directory), "brcc32.exe"));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
            return Path.GetFullPath(found);
        throw new FileNotFoundException($"项目需要 BRCC32.exe 编译资源，但未找到该工具。请在设置中指定资源编译器或 rsvars.bat 路径。\r\nrsvars.bat：{RsVarsPath}{(RsVarsFound ? "" : "（不存在）")}\r\n已查找：\r\n{string.Join("\r\n", candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(10))}");
    }

    private static void Merge(Dictionary<string, string> variables, IEnumerable<KeyValuePair<string, string>> stage,
        HashSet<string> touched)
    {
        foreach (var (name, value) in stage)
        {
            variables[name] = BuildPaths.Expand(value, variables);
            touched.Add(name);
        }
    }

    private static string? InferRoot(string executable)
    {
        var parent = Path.GetDirectoryName(executable)!;
        return Path.GetFileName(parent).Equals("bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(parent) : null;
    }

    private static List<string> ReadRegistryRoots(List<string> warnings)
    {
        var roots = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                var values = ReadKey(hive, view, RegistryPath, warnings);
                if (values.TryGetValue("RootDir", out var root) && !string.IsNullOrWhiteSpace(root))
                    roots.Add(Path.GetFullPath(BuildPaths.Unquote(Environment.ExpandEnvironmentVariables(root))));
            }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadRegistry(string path, List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                foreach (var (name, value) in ReadKey(hive, view, path, warnings))
                    result[name] = value;
        return result;
    }

    private static Dictionary<string, string> ReadKey(RegistryHive hive, RegistryView view, string path, List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            if (key is null)
                return result;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is string text)
                    result[name] = text;
                else if (value is string[] strings)
                    result[name] = string.Join(';', strings);
                else if (value is int number)
                    result[name] = number.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            warnings.Add($"无法读取注册表 {hive}\\{path}（{view}）：{exception.Message}");
        }
        return result;
    }
}
