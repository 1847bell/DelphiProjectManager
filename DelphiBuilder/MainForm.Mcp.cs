using System.Text;
using System.Text.Json;

namespace DelphiBuilder;

public sealed partial class MainForm
{
    private McpServer? _mcpServer;

    private async Task ApplySettingsAsync(AppSettings settings, bool persist = true)
    {
        McpServer? started = null;
        try
        {
            if (settings.McpEnabled && _mcpServer is null)
            {
                started = new McpServer(InvokeMcpToolAsync);
                started.Start();
            }
            if (persist) _store.SaveSettings(settings);
        }
        catch
        {
            if (started is not null) await started.DisposeAsync();
            throw;
        }
        _settings = settings;
        if (started is not null) _mcpServer = started;
        if (!settings.McpEnabled) await StopMcpAsync();
    }

    private async Task StopMcpAsync()
    {
        var server = _mcpServer;
        _mcpServer = null;
        if (server is not null) await server.DisposeAsync();
    }

    private async Task<McpToolResult> InvokeMcpToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        return await this.InvokeAsync<McpToolResult>(async _ =>
        {
            if (_closing || !_settings.McpEnabled)
                return ToolError("MCP 已关闭或程序正在退出。");
            if (cancellationToken.IsCancellationRequested)
                return ToolError("MCP 操作已取消。");
            if (_busy)
                return ToolError("程序正忙，请等待当前操作完成后重试。");
            if (name == "build_project" && HasEdits)
                return ToolError("界面中有未保存的项目修改。请先在界面保存或放弃修改，再发起编译；MCP 不会自动保存。");
            _busy = true;
            UpdateButtons();
            try
            {
                var operation = ExecuteMcpToolAsync(name, arguments, cancellationToken);
                _operation = operation;
                return await operation;
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }, CancellationToken.None);
    }

    private async Task<McpToolResult> ExecuteMcpToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name == "list_projects")
            {
                var projects = _store.GetWorkspaces().SelectMany(workspace => _store.GetProjects(workspace.Id)
                    .Select(project => new
                    {
                        projectId = project.Id, name = project.Name, projectFile = project.FilePath,
                        workspaceId = workspace.Id, workspace = workspace.Name,
                        configuration = project.ActiveConfiguration, platform = project.Platform
                    })).ToArray();
                return new McpToolResult(new { projects });
            }

            var projectId = arguments.GetProperty("projectId").GetInt64();
            var project = _store.GetProject(projectId)
                ?? throw new InvalidOperationException("未找到已登记项目，请先在界面添加或扫描工作区。");
            var configuration = arguments.TryGetProperty("configuration", out var config) ? config.GetString()! : project.ActiveConfiguration;
            var platform = arguments.TryGetProperty("platform", out var target) ? target.GetString()! : project.Platform;
            if (name == "get_project")
            {
                var evaluation = await Task.Run(() => _projectService.Load(project.FilePath,
                    string.IsNullOrEmpty(configuration) ? null : configuration, platform, _settings.Environment), cancellationToken);
                return new McpToolResult(new
                {
                    projectId, name = project.Name, projectFile = project.FilePath,
                    configuration = evaluation.Configuration, platform = evaluation.Platform,
                    configurations = evaluation.Configurations.Where(item => !item.Name.Equals("Base", StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Name).ToArray(),
                    platforms = new[] { "Win32", "Win64" }, version = evaluation.Version,
                    outputPath = evaluation.OutputPath, warnings = evaluation.Warnings
                });
            }
            if (name == "build_project")
            {
                var rebuild = arguments.TryGetProperty("rebuild", out var value) && value.GetBoolean();
                return await ExecuteBuildAsync(project, configuration, platform, rebuild, cancellationToken, captureLog: true);
            }
            return ToolError("未知 MCP 工具。");
        }
        catch (OperationCanceledException) { return ToolError("MCP 操作已取消。"); }
        catch (Exception exception) { return ToolError(exception.Message); }
    }

    private static McpToolResult ToolError(string message) => new(new { error = message }, IsError: true);

    private async Task<McpToolResult> ExecuteBuildAsync(ProjectRecord project, string configuration, string platform,
        bool rebuild, CancellationToken cancellationToken, bool captureLog)
    {
        _pendingLog.Clear();
        _log.Clear();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _buildCancellation = cancellation;
        UpdateButtons();
        var progress = new BuildLogProgress(new Progress<string>(text => { if (!_closing) _pendingLog.Append(text); }), captureLog);
        try
        {
            _status.Text = "准备编译：" + project.Name;
            var plan = await _buildService.CreatePlanAsync(project.FilePath, configuration, platform, _settings,
                rebuild, cancellation.Token);
            _status.Text = $"正在编译 {project.Name} · {plan.Evaluation.Configuration} / {plan.Evaluation.Platform}";
            var result = await _buildService.BuildAsync(plan, progress, cancellation.Token);
            _status.Text = result.Cancelled ? "编译已取消" : result.ExitCode == 0
                ? $"编译成功 · {result.Duration.TotalSeconds:F2} 秒"
                : $"编译失败 · 退出代码 {result.ExitCode} · {result.Duration.TotalSeconds:F2} 秒";
            _pendingLog.AppendLine().AppendLine(_status.Text);
            return new McpToolResult(new
            {
                projectId = project.Id, projectFile = project.FilePath,
                configuration = plan.Evaluation.Configuration, platform = plan.Evaluation.Platform,
                success = result.ExitCode == 0 && !result.Cancelled, exitCode = result.ExitCode,
                cancelled = result.Cancelled, durationSeconds = result.Duration.TotalSeconds,
                artifacts = plan.ExpectedArtifacts.Where(File.Exists).ToArray(),
                log = progress.Text, logTruncated = progress.Truncated
            }, IsError: result.ExitCode != 0 || result.Cancelled);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "编译已取消";
            _pendingLog.AppendLine(_status.Text);
            return new McpToolResult(new { success = false, cancelled = true, error = _status.Text, log = progress.Text, logTruncated = progress.Truncated }, IsError: true);
        }
        catch (Exception exception)
        {
            _status.Text = "编译失败：" + exception.Message;
            _pendingLog.AppendLine(_status.Text);
            return new McpToolResult(new { success = false, cancelled = false, error = exception.Message, log = progress.Text, logTruncated = progress.Truncated }, IsError: true);
        }
        finally
        {
            _buildCancellation = null;
            FlushLog();
            UpdateButtons();
        }
    }

    private sealed class BuildLogProgress(IProgress<string> display, bool capture) : IProgress<string>
    {
        private const int MaximumLength = 128 * 1024;
        private readonly StringBuilder? _text = capture ? new StringBuilder() : null;
        public bool Truncated { get; private set; }
        public string Text => _text?.ToString() ?? "";

        public void Report(string value)
        {
            if (_text is not null)
            {
                if (value.Length >= MaximumLength)
                {
                    _text.Clear().Append(value.AsSpan(value.Length - MaximumLength));
                    Truncated = true;
                }
                else
                {
                    var excess = _text.Length + value.Length - MaximumLength;
                    if (excess > 0) { _text.Remove(0, excess); Truncated = true; }
                    _text.Append(value);
                }
            }
            display.Report(value);
        }
    }
}
