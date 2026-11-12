namespace DelphiBuilder;

public sealed class SettingsForm : Form
{
    private readonly TextBox _compiler32 = PathBox("Compiler32Path");
    private readonly TextBox _compiler64 = PathBox("Compiler64Path");
    private readonly TextBox _rsvars = PathBox("RsVarsPath");
    private readonly TextBox _brcc = PathBox("ResourceCompilerPath");
    private readonly CheckBox _resources = new() { Text = "编译资源并同步项目版本信息", AutoSize = true, Name = "ResourceBuild" };
    private readonly NumericUpDown _history = new() { Minimum = 1, Maximum = 15, Width = 100, Name = "OutputHistoryLimit" };
    private readonly ComboBox _encoding = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Name = "OutputEncoding" };
    private readonly TextBox _arguments = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Name = "AdditionalArguments" };
    private readonly DataGridView _environment = new() { Dock = DockStyle.Fill, Name = "EnvironmentVariables", AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersWidth = 28, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    public AppSettings Settings { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        Settings = settings;
        Text = "设置 — Delphi 项目管理器";
        Name = "SettingsForm";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(850, 660);
        MinimumSize = new Size(720, 570);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        _compiler32.Text = settings.CompilerPath32;
        _compiler64.Text = settings.CompilerPath64;
        _rsvars.Text = settings.RsVarsPath;
        _brcc.Text = settings.ResourceCompilerPath;
        _resources.Checked = settings.ResourceBuild;
        _history.Value = Math.Clamp(settings.OutputPathHistoryLimit, 1, 15);
        _encoding.Items.AddRange(["系统 ANSI 代码页", "简体中文 CP936", "UTF-8"]);
        _encoding.SelectedIndex = settings.OutputEncoding switch { "cp936" => 1, "utf8" => 2, _ => 0 };
        _arguments.Lines = settings.AdditionalArguments.ToArray();
        _environment.Columns.Add("Variable", "变量名");
        _environment.Columns.Add("Value", "值");
        foreach (var item in settings.Environment) _environment.Rows.Add(item.Key, item.Value);

        var root = Ui.Table();
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill, Name = "SettingsTabs" };
        tabs.TabPages.Add(CreateCompilerTab());
        tabs.TabPages.Add(CreateOptionsTab());
        tabs.TabPages.Add(CreateEnvironmentTab());
        root.Controls.Add(tabs, 0, 0);
        var save = Ui.Button("保存设置", "SaveSettings", (_, _) => Save());
        var cancel = Ui.Button("取消", "CancelSettings", (_, _) => DialogResult = DialogResult.Cancel);
        var footer = Ui.Toolbar(save, cancel);
        footer.FlowDirection = FlowDirection.RightToLeft;
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private TabPage CreateCompilerTab()
    {
        var tab = new TabPage("编译器") { Padding = new Padding(8), UseVisualStyleBackColor = true };
        var table = Ui.Table(2);
        table.ColumnStyles.Clear();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, 0, "Delphi 版本", new Label { Text = "Delphi XE7 / BDS 15.0", AutoSize = true, Padding = new Padding(0, 6, 0, 6) });
        AddRow(table, 1, "Win32 编译器", WithBrowse(_compiler32, "DCC32.exe|DCC32.exe|可执行文件|*.exe|所有文件|*.*"));
        AddRow(table, 2, "Win64 编译器", WithBrowse(_compiler64, "DCC64.exe|DCC64.exe|可执行文件|*.exe|所有文件|*.*"));
        AddRow(table, 3, "环境脚本", WithBrowse(_rsvars, "环境脚本|rsvars.bat|批处理文件|*.bat|所有文件|*.*"));
        AddRow(table, 4, "资源编译器", WithBrowse(_brcc, "资源编译器|brcc32.exe|可执行文件|*.exe|所有文件|*.*"));
        var note = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(3, 15, 3, 3),
            Text = "路径留空时尝试从 BDS 环境、注册表和标准安装位置发现。\n非标准安装建议显式选择 DCC32.exe；Win64 需要实际可用的 DCC64.exe。\n仅支持并验证 XE7；本程序调用 DCC，不执行 MSBuild targets 或编译前后事件。\n\n设置和工作区保存在程序目录中的 delphi-builder.db。"
        };
        table.Controls.Add(note, 0, 5);
        table.SetColumnSpan(note, 2);
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tab.Controls.Add(table);
        return tab;
    }

    private TabPage CreateOptionsTab()
    {
        var tab = new TabPage("编译选项") { Padding = new Padding(8), UseVisualStyleBackColor = true };
        var table = Ui.Table(2);
        table.ColumnStyles.Clear();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, 0, "项目资源", _resources);
        AddRow(table, 1, "输出路径历史数量", _history);
        AddRow(table, 2, "编译输出编码", _encoding);
        AddRow(table, 3, "额外 DCC 参数", new Label { AutoSize = true, Text = "每行一个完整参数，不按空格拆分；主源文件由程序自动添加。" });
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(_arguments, 0, 4);
        table.SetColumnSpan(_arguments, 2);
        tab.Controls.Add(table);
        return tab;
    }

    private TabPage CreateEnvironmentTab()
    {
        var tab = new TabPage("环境变量") { Padding = new Padding(8), UseVisualStyleBackColor = true };
        var table = Ui.Table();
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label
        {
            Text = "在 BDS 环境之后覆盖变量。末行新增；选中行后按 Delete 删除。变量值按原样保存。",
            AutoSize = true, Margin = new Padding(3, 6, 3, 12)
        }, 0, 0);
        table.Controls.Add(_environment, 0, 1);
        tab.Controls.Add(table);
        return tab;
    }

    private static TextBox PathBox(string name) => new() { Name = name, Dock = DockStyle.Fill };

    private Control WithBrowse(TextBox box, string filter)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(Ui.Button("浏览…", box.Name + "Browse", (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, FileName = box.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
        }), 1, 0);
        return panel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control field)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 9, 6, 10) }, 0, row);
        field.Margin = new Padding(3, 6, 3, 6);
        table.Controls.Add(field, 1, row);
    }

    private void Save()
    {
        try
        {
            _environment.EndEdit();
            foreach (var box in new[] { _compiler32, _compiler64, _rsvars, _brcc })
                if (box.Text.Trim().Length > 0 && !File.Exists(box.Text.Trim()))
                    throw new InvalidOperationException($"文件不存在：{box.Text.Trim()}");
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in _environment.Rows)
            {
                if (row.IsNewRow) continue;
                var key = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "";
                var value = Convert.ToString(row.Cells[1].Value) ?? "";
                if (key.Length == 0 && value.Length == 0) continue;
                if (key.Length == 0 || key.IndexOfAny(['=', '\0', '\r', '\n']) >= 0 || value.Contains('\0'))
                    throw new InvalidOperationException("环境变量名不能为空，也不能包含等号或换行；值不能包含空字符。");
                if (!variables.TryAdd(key, value)) throw new InvalidOperationException($"环境变量重复：{key}");
            }
            Settings = new AppSettings
            {
                DelphiVersion = "XE7", CompilerPath32 = _compiler32.Text.Trim(), CompilerPath64 = _compiler64.Text.Trim(),
                RsVarsPath = _rsvars.Text.Trim(), ResourceCompilerPath = _brcc.Text.Trim(), ResourceBuild = _resources.Checked,
                OutputPathHistoryLimit = (int)_history.Value,
                OutputEncoding = _encoding.SelectedIndex switch { 1 => "cp936", 2 => "utf8", _ => "system" },
                AdditionalArguments = _arguments.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList(),
                Environment = variables
            };
            DialogResult = DialogResult.OK;
        }
        catch (Exception exception) { Ui.ShowError(this, exception); }
    }
}
