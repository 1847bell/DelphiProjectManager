using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DelphiBuilder;

public sealed class BuildService(ProjectService projects)
{
    private int running;
    private static readonly Regex OutputDirectoryArgument = new(@"^-(?:N0|LE|LN|E)(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<BuildPlan> CreatePlanAsync(string projectFile, string configuration, string platform,
        AppSettings settings, bool rebuild = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.DelphiVersion.Equals("XE7", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持的 Delphi 版本：{settings.DelphiVersion}。当前仅支持已实现参数映射的 Delphi XE7。");
        platform = platform.Equals("Win32", StringComparison.OrdinalIgnoreCase) ? "Win32"
            : platform.Equals("Win64", StringComparison.OrdinalIgnoreCase) ? "Win64"
            : throw new InvalidOperationException($"不支持的目标平台：{platform}。请选择 Win32 或 Win64。");
        var compilerOverride = platform == "Win64" ? settings.CompilerPath64 : settings.CompilerPath32;
        var rsVarsOverride = settings.RsVarsPath;
        var resourceCompiler = settings.ResourceCompilerPath;
        var resourceBuild = settings.ResourceBuild;
        var encoding = settings.OutputEncoding;
        var additionalArguments = settings.AdditionalArguments.ToArray();
        var overrides = new Dictionary<string, string>(settings.Environment, StringComparer.OrdinalIgnoreCase);
        try
        {
            _ = ToolOutputEncoding.Get(encoding);
            var fullProjectFile = Path.GetFullPath(projectFile);
            AssertFileExists(fullProjectFile, "Delphi 项目文件");
            var bds = await Xe7Environment.ResolveAsync(compilerOverride, rsVarsOverride, platform, overrides, cancellationToken).ConfigureAwait(false);
            var initialProperties = new Dictionary<string, string>(bds.Variables, StringComparer.OrdinalIgnoreCase)
            {
                ["DCC_UnitSearchPath"] = bds.LibraryPath
            };
            var evaluation = await Task.Run(() => projects.Load(fullProjectFile, configuration, platform, initialProperties), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AssertFileExists(evaluation.MainSource, "Delphi 主源文件");
            var warnings = new List<string>(bds.Warnings);
            warnings.AddRange(evaluation.Warnings);
            var arguments = Xe7Arguments.Create(evaluation, bds, rebuild, additionalArguments, warnings);
            var resources = await ProjectResources.CreatePlanAsync(evaluation, bds, resourceBuild,
                resourceCompiler, warnings, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new BuildPlan
            {
                Evaluation = evaluation,
                CompilerPath = bds.CompilerPath,
                WorkingDirectory = Path.GetDirectoryName(fullProjectFile)!,
                Environment = bds.Variables,
                Arguments = arguments,
                ResourceSteps = resources.Steps,
                ProjectResourcePath = resources.ProjectResource,
                ExpectedArtifacts = Xe7Arguments.ExpectedArtifacts(evaluation),
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
                OutputEncoding = encoding
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException($"无法创建编译计划：{exception.Message}", exception);
        }
    }

    public async Task<BuildResult> BuildAsync(BuildPlan plan, IProgress<string> output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("已有编译正在运行，请等待完成或先取消当前编译。");
        var stopwatch = Stopwatch.StartNew();
        var outputLock = new object();
        void Report(string text)
        {
            // stdout/stderr are decoded independently but a progress sink need not be thread-safe.
            lock (outputLock)
                output.Report(text);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertFileExists(plan.CompilerPath, "Delphi 编译器");
            AssertFileExists(plan.Evaluation.MainSource, "Delphi 主源文件");
            if (!Directory.Exists(plan.WorkingDirectory))
                throw new DirectoryNotFoundException($"编译工作目录不存在：{plan.WorkingDirectory}");
            var encoding = ToolOutputEncoding.Get(plan.OutputEncoding);
            foreach (var step in plan.ResourceSteps)
            {
                AssertFileExists(step.Executable, "资源编译器 BRCC32.exe");
                AssertFileExists(step.Input, "资源源文件");
            }
            PrepareOutputDirectories(plan, cancellationToken);
            foreach (var warning in plan.Warnings)
                Report("警告：" + warning + "\r\n");
            if (plan.ProjectResourcePath is { } projectResource)
            {
                var created = await ProjectResources.EnsureAsync(projectResource, cancellationToken).ConfigureAwait(false);
                Report($"项目资源（{(created ? "已创建" : "保留已有文件")}）：{projectResource}\r\n");
            }
            foreach (var step in plan.ResourceSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report($"正在编译资源：{step.Input}\r\n");
                var exitCode = await RunProcessAsync(step.Executable, step.Arguments, plan, encoding, Report, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    Report($"\r\n资源编译失败，退出代码：{exitCode}。未启动 Delphi 编译器。\r\n");
                    return new BuildResult(exitCode, false, stopwatch.Elapsed);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.ProjectResourcePath is { } versionResource && VersionResource.GetInfo(plan.Evaluation) is { } version)
            {
                await VersionResource.UpdateFileAsync(versionResource, version, cancellationToken).ConfigureAwait(false);
                Report($"已同步项目版本资源：{string.Join(".", version.FileVersion)}，{versionResource}\r\n");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Report($"正在使用 {Path.GetFileName(plan.CompilerPath)} 编译：{plan.Evaluation.MainSource}\r\n");
            var result = await RunProcessAsync(plan.CompilerPath, plan.Arguments, plan, encoding, Report, cancellationToken).ConfigureAwait(false);
            Report(result == 0 ? "\r\n编译成功。\r\n" : $"\r\n编译失败，退出代码：{result}。\r\n");
            return new BuildResult(result, false, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report("\r\n编译已取消，编译进程已结束。\r\n");
            return new BuildResult(-1, true, stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException($"编译准备或执行失败：{exception.Message}", exception);
        }
        finally
        {
            stopwatch.Stop();
            Volatile.Write(ref running, 0);
        }
    }

    private static async Task<int> RunProcessAsync(string executable, IReadOnlyList<string> arguments,
        BuildPlan plan, Encoding encoding, Action<string> output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (name, value) in plan.Environment)
            startInfo.Environment[name] = value;
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"无法启动编译工具：{executable}");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"无法启动编译工具：{executable}（系统错误码 {exception.NativeErrorCode}）。{exception.Message}", exception);
        }
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, output);
        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between HasExited and Kill.
                }
                catch (Win32Exception) when (process.HasExited)
                {
                    // The process exited between HasExited and Kill.
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            // Do not cancel pipe reads: the final compiler diagnostics must be drained after exit/kill.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> output)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            output(new string(buffer, 0, count));
    }

    private static void AssertFileExists(string file, string label)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException($"{label}不存在：{file}", file);
    }

    private static void PrepareOutputDirectories(BuildPlan plan, CancellationToken cancellationToken)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in plan.ExpectedArtifacts)
            directories.Add(Path.GetDirectoryName(BuildPaths.Resolve(artifact, plan.WorkingDirectory))!);
        foreach (var argument in plan.Arguments)
        {
            var match = OutputDirectoryArgument.Match(argument);
            if (match.Success)
                directories.Add(BuildPaths.Resolve(match.Groups[1].Value, plan.WorkingDirectory));
        }
        foreach (var step in plan.ResourceSteps)
            directories.Add(Path.GetDirectoryName(BuildPaths.Resolve(step.Output, plan.WorkingDirectory))!);
        if (plan.ProjectResourcePath is { } resource)
            directories.Add(Path.GetDirectoryName(resource)!);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
        }
    }
}
