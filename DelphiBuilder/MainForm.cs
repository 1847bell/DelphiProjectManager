using System.Diagnostics;
using System.Text;

namespace DelphiBuilder;

public sealed partial class MainForm : Form
{
    private readonly AppStore _store;
    private readonly ProjectService _projectService = new();
    private readonly BuildService _buildService;
    private AppSettings _settings;
    private readonly ListBox _workspaces = new() { Dock = DockStyle.Fill, Name = "Workspaces", DisplayMember = nameof(WorkspaceRecord.Name), IntegralHeight = false };
    private readonly TreeView _favorites = new() { Dock = DockStyle.Fill, Name = "Favorites", HideSelection = false, ShowNodeToolTips = true, FullRowSelect = true };
    private readonly ListView _projects = new() { Dock = DockStyle.Fill, Name = "Projects", View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _filter = new() { Dock = DockStyle.Fill, Name = "ProjectFilter", PlaceholderText = "输入项目名称，按完整字符串包含匹配…" };
    private readonly Label _projectCount = new() { AutoSize = true, Name = "ProjectCount", Margin = new Padding(3, 6, 3, 6) };
    private readonly Label _projectTitle = Ui.Heading("请选择项目");
    private readonly TextBox _projectFile = new() { Dock = DockStyle.Fill, ReadOnly = true, Name = "ProjectFile", BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly ComboBox _configuration = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Name = "ActiveConfiguration" };
    private readonly ComboBox _platform = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Name = "ActivePlatform" };
    private readonly TextBox _version = new() { Dock = DockStyle.Fill, Name = "ProjectVersion" };
    private readonly ComboBox _outputPath = new() { Dock = DockStyle.Fill, Name = "ProjectOutputPath" };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, Name = "BuildLog", ReadOnly = true, WordWrap = false, DetectUrls = false, Font = new Font("Consolas", 10), BackColor = Color.FromArgb(25, 30, 39), ForeColor = Color.FromArgb(222, 229, 239), BorderStyle = BorderStyle.None };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "就绪", Name = "Status", Margin = new Padding(10, 4, 3, 5) };
    private readonly Button _build;
    private readonly Button _cancelBuild;
    private readonly Button _save;
    private readonly Button _rescan;
    private readonly Button _addFavorite;
    private readonly Button _openExplorer;
    private readonly Button _browseOutput;
    private readonly Button _settingsButton;
    private readonly Button _saveLayout;
    private readonly Button _addWorkspace;
    private readonly Button _removeWorkspace;
    private readonly List<Control> _favoriteActions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _pendingLog = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 70 };
    private IReadOnlyList<ProjectRecord> _workspaceProjects = [];
    private WorkspaceRecord? _workspace;
    private ProjectRecord? _activeProject;
    private ProjectEvaluation? _evaluation;
    private CancellationTokenSource? _buildCancellation;
    private Task _operation = Task.CompletedTask;
    private bool _suppress;
    private bool _busy;
    private bool _closing;
    private bool _allowClose;

    public MainForm(AppStore store)
    {
        _store = store;
        _settings = store.LoadSettings();
        _buildService = new BuildService(_projectService);
        Text = "Delphi 项目管理器";
        Name = "MainForm";
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("DelphiBuilder.Assets.App.ico")!)
            Icon = new Icon(iconStream);
        Disposed += (_, _) => Icon?.Dispose();
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = Ui.Background;
        ClientSize = new Size(1500, 880);
        MinimumSize = new Size(1200, 760);
        StartPosition = FormStartPosition.CenterScreen;
        _build = Ui.Button("编译", "BuildProject", (_, _) => Run(BuildAsync));
        _cancelBuild = Ui.Button("取消编译", "CancelBuild", (_, _) => _buildCancellation?.Cancel());
        _save = Ui.Button("保存修改", "SaveProject", (_, _) => Run(SaveCurrentAsync));
        _rescan = Ui.Button("更新工作区文件", "RescanWorkspace", (_, _) => Run(RescanAsync));
        _addFavorite = Ui.Button("添加偏好", "AddFavorite", (_, _) => Run(AddFavoriteAsync));
        _openExplorer = Ui.Button("在资源管理器中打开", "OpenExplorer", (_, _) => Run(OpenExplorerAsync));
        _browseOutput = Ui.Button("浏览…", "BrowseOutputPath", (_, _) => BrowseOutput());
        _settingsButton = Ui.Button("设置", "OpenSettings", (_, _) => Run(OpenSettingsAsync));
        _saveLayout = Ui.Button("保存布局", "SaveLayout", (_, _) => Run(SaveLayoutAsync));
        _addWorkspace = Ui.Button("添加工作区", "AddWorkspace", (_, _) => Run(AddWorkspaceAsync));
        _removeWorkspace = Ui.Button("移除", "RemoveWorkspace", (_, _) => Run(RemoveWorkspaceAsync));
        BuildLayout();
        _platform.Items.AddRange(["Win32", "Win64"]);
        _workspaces.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress && _workspaces.SelectedItem is WorkspaceRecord selected && selected.Id != _workspace?.Id)
                Run(() => SwitchWorkspaceAsync(selected));
        };
        _projects.SelectedIndexChanged += (_, _) => SelectListProject();
        _projects.KeyUp += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Tab) SelectListProject();
        };
        _favorites.AfterSelect += (_, eventArgs) =>
        {
            // Mouse clicks include retained nodes and precede the native selection change.
            if (eventArgs.Action != TreeViewAction.ByMouse) SelectFavoriteProject(eventArgs.Node);
        };
        _favorites.NodeMouseClick += SelectClickedFavorite;
        _favorites.NodeMouseDoubleClick += SelectClickedFavorite;
        _favorites.KeyUp += (_, eventArgs) =>
        {
            // Modal focus restoration must not reactivate a canceled project switch.
            if (eventArgs.KeyCode == Keys.Tab) SelectFavoriteProject(_favorites.SelectedNode);
        };
        _filter.TextChanged += (_, _) => { if (!_suppress) ApplyFilter(); };
        _configuration.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress && _configuration.SelectedItem is string selected && selected != _evaluation?.Configuration)
                Run(() => ChangeTargetAsync(selected, _evaluation?.Platform ?? "Win32"));
        };
        _platform.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress && _platform.SelectedItem is string selected && selected != _evaluation?.Platform)
                Run(() => ChangeTargetAsync(_evaluation?.Configuration ?? "", selected));
        };
        _version.TextChanged += (_, _) => UpdateButtons();
        _outputPath.TextChanged += (_, _) => UpdateButtons();
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
        FormClosing += OnClosing;
        Shown += (_, _) =>
        {
            RestorePaneLayout();
            Run(InitializeAsync);
        };
        UpdateButtons();
    }

    private void SelectListProject()
    {
        if (!_suppress && _projects.SelectedItems.Count == 1 && _projects.SelectedItems[0].Tag is ProjectRecord project && project.Id != _activeProject?.Id)
            Run(() => SelectProjectAsync(project));
    }

    private void SelectFavoriteProject(TreeNode? node)
    {
        if (!_suppress && node?.Tag is FavoriteRecord favorite && favorite.Project.Id != _activeProject?.Id)
            Run(() => SelectProjectAsync(favorite.Project, fromFavorite: true));
    }

    private void SelectClickedFavorite(object? sender, TreeNodeMouseClickEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left) SelectFavoriteProject(eventArgs.Node);
    }

    private bool HasEdits => _evaluation is not null &&
        (_version.Text != _evaluation.Version || _outputPath.Text != _evaluation.OutputPath);

    private async void Run(Func<Task> action)
    {
        if (_busy || _closing) return;
        _busy = true;
        UpdateButtons();
        _operation = ExecuteAsync(action);
        await _operation;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = "操作已取消"; }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            RestoreSelection();
            if (!_closing) Ui.ShowError(this, exception);
        }
        finally { _busy = false; UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        if (_build is null) return;
        var idle = !_busy && !_closing;
        var selected = _activeProject is not null && _evaluation is not null;
        _workspaces.Enabled = _projects.Enabled = _favorites.Enabled = _filter.Enabled = idle;
        _addWorkspace.Enabled = _settingsButton.Enabled = _saveLayout.Enabled = idle;
        _removeWorkspace.Enabled = idle && _workspace is not null;
        foreach (var action in _favoriteActions) action.Enabled = idle && _workspace is not null;
        _configuration.Enabled = _platform.Enabled = _version.Enabled = _outputPath.Enabled = _browseOutput.Enabled = idle && selected;
        _save.Enabled = idle && selected && HasEdits;
        _build.Enabled = _addFavorite.Enabled = _openExplorer.Enabled = idle && selected;
        _rescan.Enabled = idle && _workspace is not null;
        _cancelBuild.Enabled = _buildCancellation is not null && !_buildCancellation.IsCancellationRequested;
    }

    private void Suppress(Action action)
    {
        var previous = _suppress;
        _suppress = true;
        try { action(); }
        finally { _suppress = previous; }
    }

    private async Task InitializeAsync()
    {
        RefreshWorkspaces();
        if (long.TryParse(_store.GetState("workspace"), out var id) &&
            _workspaces.Items.Cast<WorkspaceRecord>().FirstOrDefault(item => item.Id == id) is { } previous)
            await LoadWorkspaceAsync(previous);
        else if (_workspaces.Items.Count > 0)
            await LoadWorkspaceAsync((WorkspaceRecord)_workspaces.Items[0]);
        if (long.TryParse(_store.GetState("project"), out var projectId) && _store.GetProject(projectId) is { } project && project.WorkspaceId == _workspace?.Id)
            await ActivateProjectAsync(project);
        _status.Text = "就绪 · 数据库：" + Path.Combine(AppContext.BaseDirectory, "delphi-builder.db");
    }

    private void RefreshWorkspaces()
    {
        Suppress(() =>
        {
            _workspaces.Items.Clear();
            foreach (var workspace in _store.GetWorkspaces()) _workspaces.Items.Add(workspace);
            if (_workspace is not null)
                _workspaces.SelectedItem = _workspaces.Items.Cast<WorkspaceRecord>().FirstOrDefault(item => item.Id == _workspace.Id);
        });
    }

    private async Task LoadWorkspaceAsync(WorkspaceRecord workspace)
    {
        _workspace = workspace;
        _store.SetState("workspace", workspace.Id.ToString());
        RefreshFavorites();
        _workspaceProjects = await Task.Run(() => _store.GetProjects(workspace.Id), _lifetime.Token);
        Suppress(() =>
        {
            _workspaces.SelectedItem = _workspaces.Items.Cast<WorkspaceRecord>().FirstOrDefault(item => item.Id == workspace.Id);
            _filter.Clear();
            ApplyFilter();
        });
        _status.Text = $"{workspace.Name} · {workspace.RootPath} · {_workspaceProjects.Count} 个项目";
    }

    private void ApplyFilter()
    {
        var query = _filter.Text;
        // Deliberately one contiguous, case-insensitive substring; never split into words.
        var filtered = _workspaceProjects.Where(project => project.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Suppress(() =>
        {
            _projects.BeginUpdate();
            try
            {
                _projects.Items.Clear();
                foreach (var project in filtered)
                {
                    var item = new ListViewItem(project.Name) { Tag = project, ToolTipText = project.FilePath };
                    item.SubItems.Add(_workspace is null ? project.FilePath : Path.GetRelativePath(_workspace.RootPath, project.FilePath));
                    _projects.Items.Add(item);
                    if (project.Id == _activeProject?.Id) item.Selected = true;
                }
            }
            finally { _projects.EndUpdate(); }
        });
        _projectCount.Text = $"{filtered.Count} / {_workspaceProjects.Count} 个项目";
    }

    private async Task SwitchWorkspaceAsync(WorkspaceRecord selected)
    {
        if (!await ConfirmEditsAsync()) { RestoreSelection(); return; }
        ClearProject();
        await LoadWorkspaceAsync(selected);
    }

    private async Task SelectProjectAsync(ProjectRecord project, bool fromFavorite = false)
    {
        project = _store.GetProject(project.Id) ?? throw new InvalidOperationException("该项目已被移除，请重新扫描工作区。");
        if (!await ConfirmEditsAsync()) { RestoreSelection(); return; }
        if (project.WorkspaceId != _workspace?.Id)
            throw new InvalidOperationException("该项目不属于当前工作区，请先切换工作区。");
        if (fromFavorite) Suppress(() => { _filter.Clear(); ApplyFilter(); });
        await ActivateProjectAsync(project);
    }

    private async Task ActivateProjectAsync(ProjectRecord project, string? configuration = null, string? platform = null)
    {
        _status.Text = "读取项目：" + project.Name;
        var evaluation = await Task.Run(() => _projectService.Load(project.FilePath,
            configuration ?? (string.IsNullOrEmpty(project.ActiveConfiguration) ? null : project.ActiveConfiguration),
            platform ?? project.Platform, _settings.Environment), _lifetime.Token);
        _store.SetActiveTarget(project.Id, evaluation.Configuration, evaluation.Platform);
        _store.SetState("project", project.Id.ToString());
        _activeProject = project with { ActiveConfiguration = evaluation.Configuration, Platform = evaluation.Platform };
        _evaluation = evaluation;
        DisplayEvaluation();
        RestoreSelection();
        _status.Text = $"{project.Name} · {evaluation.Configuration} / {evaluation.Platform}";
    }

    private void DisplayEvaluation()
    {
        if (_evaluation is not { } evaluation) return;
        Suppress(() =>
        {
            _projectTitle.Text = _activeProject!.Name;
            _projectFile.Text = evaluation.ProjectFile;
            _configuration.Items.Clear();
            foreach (var configuration in evaluation.Configurations) _configuration.Items.Add(configuration.Name);
            if (!_configuration.Items.Contains(evaluation.Configuration)) _configuration.Items.Add(evaluation.Configuration);
            _configuration.SelectedItem = evaluation.Configuration;
            _platform.SelectedItem = evaluation.Platform;
            _version.Text = evaluation.Version;
            _outputPath.Items.Clear();
            foreach (var history in _store.GetOutputHistory(_activeProject.Id, _settings.OutputPathHistoryLimit))
                _outputPath.Items.Add(history);
            _outputPath.Text = evaluation.OutputPath;
        });
    }

    private void RestoreSelection()
    {
        Suppress(() =>
        {
            _workspaces.SelectedItem = _workspaces.Items.Cast<WorkspaceRecord>().FirstOrDefault(item => item.Id == _workspace?.Id);
            foreach (ListViewItem item in _projects.Items) item.Selected = ((ProjectRecord)item.Tag!).Id == _activeProject?.Id;
            if (_evaluation is not null)
            {
                _configuration.SelectedItem = _evaluation.Configuration;
                _platform.SelectedItem = _evaluation.Platform;
            }
        });
    }

    private void ClearProject()
    {
        _activeProject = null;
        _evaluation = null;
        Suppress(() =>
        {
            _projectTitle.Text = "请选择项目";
            _projectFile.Clear();
            _configuration.Items.Clear();
            _platform.SelectedIndex = -1;
            _version.Clear();
            _outputPath.Items.Clear();
            _outputPath.Text = "";
        });
    }

    private async Task ChangeTargetAsync(string configuration, string platform)
    {
        if (_activeProject is null) return;
        if (!await ConfirmEditsAsync()) { RestoreSelection(); return; }
        await ActivateProjectAsync(_activeProject, configuration, platform);
    }

    private async Task<bool> ConfirmEditsAsync()
    {
        if (!HasEdits) return true;
        var answer = MessageBox.Show(this, "版本号或输出路径尚未保存。是否保存修改？\n选择“否”将放弃本次编辑。", "未保存修改",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes) await SaveCurrentAsync();
        if (answer == DialogResult.No) DisplayEvaluation();
        return true;
    }

    private async Task SaveCurrentAsync()
    {
        if (_activeProject is null || _evaluation is null) return;
        var project = _activeProject;
        var target = _evaluation;
        var version = _version.Text;
        var output = _outputPath.Text;
        await Task.Run(() => _projectService.Save(project.FilePath, target.Configuration, target.Platform, version, output), _lifetime.Token);
        _store.AddOutputHistory(project.Id, output, _settings.OutputPathHistoryLimit);
        await ActivateProjectAsync(project, target.Configuration, target.Platform);
        _status.Text = "已保存到 .dproj：" + project.FilePath;
    }

    private void BrowseOutput()
    {
        try
        {
            using var dialog = new FolderBrowserDialog { Description = "选择项目输出目录", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (_evaluation is not null)
            {
                var candidate = Path.GetFullPath(_outputPath.Text.Length == 0 ? "." : _outputPath.Text, Path.GetDirectoryName(_evaluation.ProjectFile)!);
                if (Directory.Exists(candidate)) dialog.InitialDirectory = candidate;
            }
            if (dialog.ShowDialog(this) == DialogResult.OK) _outputPath.Text = dialog.SelectedPath;
        }
        catch (Exception exception) { Ui.ShowError(this, exception); }
    }

    private async Task AddWorkspaceAsync()
    {
        if (!await ConfirmEditsAsync()) return;
        using var dialog = new FolderBrowserDialog { Description = "选择工作区根目录（递归扫描所有 .dproj）", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var workspace = _store.AddWorkspace(dialog.SelectedPath);
        RefreshWorkspaces();
        ClearProject();
        await ScanAndLoadAsync(workspace);
    }

    private async Task RescanAsync()
    {
        if (_workspace is null || !await ConfirmEditsAsync()) return;
        var previousId = _activeProject?.Id;
        await ScanAndLoadAsync(_workspace);
        if (previousId is { } id && _store.GetProject(id) is { } project)
            await ActivateProjectAsync(project);
        else ClearProject();
    }

    private async Task ScanAndLoadAsync(WorkspaceRecord workspace)
    {
        _status.Text = "扫描工作区：" + workspace.RootPath;
        var summary = await Task.Run(() => _store.ScanWorkspace(workspace, _lifetime.Token), _lifetime.Token);
        await LoadWorkspaceAsync(workspace);
        _status.Text = $"扫描完成 · {summary.ProjectCount} 个项目 · {summary.Warnings.Count} 条提示";
        if (summary.Warnings.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine, summary.Warnings), "工作区扫描提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task RemoveWorkspaceAsync()
    {
        if (_workspace is not { } workspace || !await ConfirmEditsAsync()) return;
        if (MessageBox.Show(this, $"从列表移除工作区“{workspace.Name}”？\n仅删除数据库记录及该工作区的偏好文件夹和项目引用，不删除磁盘文件。",
            "移除工作区", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _store.RemoveWorkspace(workspace.Id);
        _workspace = null;
        _workspaceProjects = [];
        ClearProject();
        RefreshWorkspaces();
        RefreshFavorites();
        ApplyFilter();
        if (_workspaces.Items.Count > 0) await LoadWorkspaceAsync((WorkspaceRecord)_workspaces.Items[0]);
        else _status.Text = "请添加工作区";
    }

    private Task OpenExplorerAsync()
    {
        if (_activeProject is not null)
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(_activeProject.FilePath);
            Process.Start(start)?.Dispose();
        }
        return Task.CompletedTask;
    }

    private void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/1847bell/") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) { Ui.ShowError(this, exception); }
    }

    private async Task OpenSettingsAsync()
    {
        if (!await ConfirmEditsAsync()) return;
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveSettings(dialog.Settings);
        _settings = dialog.Settings;
        if (_activeProject is not null) await ActivateProjectAsync(_activeProject);
        _status.Text = "设置已保存";
    }

    private async Task BuildAsync()
    {
        if (_activeProject is null || _evaluation is null || !await ConfirmEditsAsync()) return;
        // A discarded draft must not remain visible as if it were being compiled.
        DisplayEvaluation();
        _pendingLog.Clear();
        _log.Clear();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _buildCancellation = cancellation;
        UpdateButtons();
        var progress = new Progress<string>(text => { if (!_closing) _pendingLog.Append(text); });
        try
        {
            _status.Text = "准备编译：" + _activeProject.Name;
            var plan = await _buildService.CreatePlanAsync(_activeProject.FilePath, _evaluation.Configuration,
                _evaluation.Platform, _settings, cancellationToken: cancellation.Token);
            _status.Text = $"正在编译 {_activeProject.Name} · {plan.Evaluation.Configuration} / {plan.Evaluation.Platform}";
            var result = await _buildService.BuildAsync(plan, progress, cancellation.Token);
            _status.Text = result.Cancelled ? "编译已取消" : result.ExitCode == 0
                ? $"编译成功 · {result.Duration.TotalSeconds:F2} 秒"
                : $"编译失败 · 退出代码 {result.ExitCode} · {result.Duration.TotalSeconds:F2} 秒";
            _pendingLog.AppendLine().AppendLine(_status.Text);
        }
        catch (OperationCanceledException) { _status.Text = "编译已取消"; _pendingLog.AppendLine(_status.Text); }
        catch (Exception exception)
        {
            _status.Text = "编译失败：" + exception.Message;
            _pendingLog.AppendLine(_status.Text);
        }
        finally
        {
            _buildCancellation = null;
            FlushLog();
            UpdateButtons();
        }
    }

    private void FlushLog()
    {
        if (_pendingLog.Length == 0 || IsDisposed) return;
        _log.AppendText(_pendingLog.ToString());
        _pendingLog.Clear();
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private async void OnClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose) return;
        eventArgs.Cancel = true;
        if (_closing) return;
        _closing = true;
        UpdateButtons();
        try
        {
            _buildCancellation?.Cancel();
            await _operation;
            if (HasEdits)
            {
                var answer = MessageBox.Show(this, "仍有未保存的项目修改，是否保存后退出？", "未保存修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) { _closing = false; UpdateButtons(); return; }
                if (answer == DialogResult.Yes && _activeProject is not null && _evaluation is not null)
                    _projectService.Save(_activeProject.FilePath, _evaluation.Configuration, _evaluation.Platform, _version.Text, _outputPath.Text);
            }
            _lifetime.Cancel();
            _logTimer.Stop();
            _logTimer.Dispose();
            _lifetime.Dispose();
            _allowClose = true;
            Close();
        }
        catch (Exception exception) { _closing = false; UpdateButtons(); Ui.ShowError(this, exception); }
    }
}
