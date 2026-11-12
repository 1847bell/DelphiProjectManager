namespace DelphiBuilder;

public sealed partial class MainForm
{
    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Delphi 项目管理器", Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold), ForeColor = Ui.Accent, AutoSize = true, Margin = new Padding(8, 6, 3, 10) }, 0, 0);
        header.Controls.Add(_saveLayout, 1, 0);
        header.Controls.Add(_settingsButton, 2, 0);
        root.Controls.Add(header, 0, 0);

        var leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Name = "WorkspaceSplit", Size = new Size(1470, 780), SplitterWidth = 6,
            SplitterDistance = 225, Panel1MinSize = 180, Panel2MinSize = 940
        };
        var workspacePanel = Ui.Table();
        workspacePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workspacePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workspacePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspacePanel.Controls.Add(Ui.Heading("工作区"), 0, 0);
        workspacePanel.Controls.Add(Ui.Toolbar(_addWorkspace, _removeWorkspace), 0, 1);
        workspacePanel.Controls.Add(_workspaces, 0, 2);
        leftSplit.Panel1.Controls.Add(workspacePanel);

        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Name = "DetailsSplit", Size = new Size(1230, 780), SplitterWidth = 6,
            SplitterDistance = 450, Panel1MinSize = 330, Panel2MinSize = 600
        };
        var middleSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Name = "FavoritesSplit", Orientation = Orientation.Horizontal, Size = new Size(450, 780),
            SplitterWidth = 6, SplitterDistance = 340, Panel1MinSize = 190, Panel2MinSize = 200
        };
        _workspaceSplit = leftSplit;
        _detailsSplit = rightSplit;
        _favoritesSplit = middleSplit;
        var favoritePanel = Ui.Table();
        favoritePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        favoritePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        favoritePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        favoritePanel.Controls.Add(Ui.Heading("偏好项目"), 0, 0);
        var createFolder = Ui.Button("新建文件夹", "NewFavoriteFolder", (_, _) => Run(CreateFavoriteFolderAsync));
        var renameFolder = Ui.Button("重命名", "RenameFavoriteFolder", (_, _) => Run(RenameFavoriteFolderAsync));
        var removeFavorite = Ui.Button("移除", "RemoveFavorite", (_, _) => Run(RemoveFavoriteAsync));
        var moveUp = Ui.Button("上移", "MoveFavoriteUp", (_, _) => Run(() => MoveFavoriteAsync(-1)));
        var moveDown = Ui.Button("下移", "MoveFavoriteDown", (_, _) => Run(() => MoveFavoriteAsync(1)));
        _favoriteActions.AddRange([createFolder, renameFolder, removeFavorite, moveUp, moveDown]);
        favoritePanel.Controls.Add(Ui.Toolbar(createFolder, renameFolder, removeFavorite, moveUp, moveDown), 0, 1);
        favoritePanel.Controls.Add(_favorites, 0, 2);
        middleSplit.Panel1.Controls.Add(favoritePanel);

        var projectPanel = Ui.Table();
        projectPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        projectPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        projectPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        projectPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        projectPanel.Controls.Add(Ui.Heading("工作区项目"), 0, 0);
        projectPanel.Controls.Add(_filter, 0, 1);
        projectPanel.Controls.Add(_projectCount, 0, 2);
        _projects.Columns.Add("项目名称", 160);
        _projects.Columns.Add("相对路径", 380);
        _projects.ShowItemToolTips = true;
        projectPanel.Controls.Add(_projects, 0, 3);
        middleSplit.Panel2.Controls.Add(projectPanel);
        rightSplit.Panel1.Controls.Add(middleSplit);
        rightSplit.Panel2.Controls.Add(CreateDetailsPanel());
        leftSplit.Panel2.Controls.Add(rightSplit);
        root.Controls.Add(leftSplit, 0, 1);
        root.Controls.Add(CreateFooter(), 0, 2);
        Controls.Add(root);
    }

    private Control CreateFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
            Name = "Footer"
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(new Label
        {
            Text = "Copyright (C) 2026 AlexNiu", Name = "Copyright", AutoSize = true,
            Anchor = AnchorStyles.Right, Margin = new Padding(8, 3, 4, 3), ForeColor = Color.DimGray
        }, 1, 0);
        var github = new Button
        {
            Name = "GitHubLink", Size = new Size(24, 24), Margin = new Padding(4, 3, 10, 3),
            FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, Cursor = Cursors.Hand,
            BackgroundImageLayout = ImageLayout.Zoom, AccessibleName = "GitHub：1847bell",
            AccessibleDescription = "https://github.com/1847bell/"
        };
        github.FlatAppearance.BorderSize = 0;
        using (var imageStream = typeof(MainForm).Assembly.GetManifestResourceStream("DelphiBuilder.Assets.GitHub.png")!)
        using (var image = Image.FromStream(imageStream))
            github.BackgroundImage = new Bitmap(image);
        var toolTip = new ToolTip();
        toolTip.SetToolTip(github, "https://github.com/1847bell/");
        github.Click += (_, _) => OpenGitHub();
        github.Disposed += (_, _) => { github.BackgroundImage?.Dispose(); toolTip.Dispose(); };
        footer.Controls.Add(github, 2, 0);
        return footer;
    }

    private Control CreateDetailsPanel()
    {
        var panel = Ui.Table();
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _projectTitle.Name = "ProjectTitle";
        panel.Controls.Add(_projectTitle, 0, 0);
        panel.Controls.Add(_projectFile, 0, 1);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 6, 0, 3) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        AddField(fields, "启用配置", _configuration, 0, 0);
        AddField(fields, "平台", _platform, 2, 0);
        AddField(fields, "版本号", _version, 0, 1);
        fields.SetColumnSpan(_version, 3);
        var path = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        path.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        path.Controls.Add(_outputPath, 0, 0);
        path.Controls.Add(_browseOutput, 1, 0);
        AddField(fields, "输出路径", path, 0, 2);
        fields.SetColumnSpan(path, 3);
        panel.Controls.Add(fields, 0, 2);
        panel.Controls.Add(Ui.Toolbar(_build, _cancelBuild, _rescan, _save, _addFavorite, _openExplorer), 0, 3);
        panel.Controls.Add(new Label
        {
            Text = "配置切换立即生效；版本和路径需保存后写入项目文件。相对路径以项目目录为基准。",
            Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 4, 3, 6),
            MaximumSize = new Size(800, 0)
        }, 0, 4);
        panel.Controls.Add(Ui.Heading("编译日志"), 0, 5);
        panel.Controls.Add(_log, 0, 6);
        return panel;
    }

    private static void AddField(TableLayoutPanel table, string title, Control control, int column, int row)
    {
        if (table.RowStyles.Count <= row) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 8, 3, 8) }, column, row);
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, column + 1, row);
    }

    private void RefreshFavorites(string? selectKey = null)
    {
        selectKey ??= _favorites.SelectedNode?.Name;
        var expanded = _favorites.Nodes.Cast<TreeNode>().Where(node => node.IsExpanded).Select(node => node.Name).ToHashSet();
        Suppress(() =>
        {
            _favorites.BeginUpdate();
            try
            {
                _favorites.Nodes.Clear();
                foreach (var folder in _store.GetFavoriteFolders(_workspace?.Id ?? 0))
                {
                    var node = new TreeNode(folder.Name) { Name = "folder:" + folder.Id, Tag = folder };
                    foreach (var favorite in _store.GetFavorites(folder.Id))
                        node.Nodes.Add(new TreeNode(favorite.Project.Name)
                        {
                            Name = "favorite:" + favorite.Id, Tag = favorite, ToolTipText = favorite.Project.FilePath
                        });
                    _favorites.Nodes.Add(node);
                    if (expanded.Contains(node.Name) || expanded.Count == 0) node.Expand();
                }
                if (selectKey is not null)
                {
                    var selected = _favorites.Nodes.Find(selectKey, true).FirstOrDefault();
                    if (selected is not null) { _favorites.SelectedNode = selected; selected.EnsureVisible(); }
                }
            }
            finally { _favorites.EndUpdate(); }
        });
    }

    private Task CreateFavoriteFolderAsync()
    {
        if (_workspace is null) return Task.CompletedTask;
        using var dialog = new TextPrompt("新建偏好文件夹", "文件夹名称");
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var folder = _store.AddFavoriteFolder(_workspace.Id, dialog.Value);
            RefreshFavorites("folder:" + folder.Id);
        }
        return Task.CompletedTask;
    }

    private Task RenameFavoriteFolderAsync()
    {
        var folder = _favorites.SelectedNode?.Tag as FavoriteFolderRecord
            ?? _favorites.SelectedNode?.Parent?.Tag as FavoriteFolderRecord;
        if (folder is null) return Task.CompletedTask;
        using var dialog = new TextPrompt("重命名偏好文件夹", "文件夹名称", folder.Name);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _store.RenameFavoriteFolder(folder.Id, dialog.Value);
            RefreshFavorites("folder:" + folder.Id);
        }
        return Task.CompletedTask;
    }

    private Task RemoveFavoriteAsync()
    {
        switch (_favorites.SelectedNode?.Tag)
        {
            case FavoriteFolderRecord folder:
                if (MessageBox.Show(this, $"移除偏好文件夹“{folder.Name}”及其中所有引用？\n不会删除磁盘中的项目文件。", "移除偏好文件夹",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return Task.CompletedTask;
                _store.RemoveFavoriteFolder(folder.Id);
                break;
            case FavoriteRecord favorite:
                _store.RemoveFavorite(favorite.Id);
                break;
        }
        RefreshFavorites();
        return Task.CompletedTask;
    }

    private Task MoveFavoriteAsync(int delta)
    {
        switch (_favorites.SelectedNode?.Tag)
        {
            case FavoriteFolderRecord folder: _store.MoveFavoriteFolder(folder.Id, delta); break;
            case FavoriteRecord favorite: _store.MoveFavorite(favorite.Id, delta); break;
        }
        RefreshFavorites();
        return Task.CompletedTask;
    }

    private async Task AddFavoriteAsync()
    {
        if (_activeProject is null || _workspace is null) return;
        var folders = _store.GetFavoriteFolders(_workspace.Id);
        if (folders.Count == 0)
        {
            await CreateFavoriteFolderAsync();
            folders = _store.GetFavoriteFolders(_workspace.Id);
            if (folders.Count == 0) return;
        }
        using var dialog = new FavoriteFolderPicker(folders);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.AddFavorite(dialog.FolderId, _activeProject.Id);
        RefreshFavorites("folder:" + dialog.FolderId);
        _status.Text = "已添加偏好：" + _activeProject.Name;
    }
}
