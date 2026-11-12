namespace DelphiBuilder;

internal static class Ui
{
    public static readonly Color Background = Color.FromArgb(244, 246, 249);
    public static readonly Color Accent = Color.FromArgb(35, 91, 168);

    public static Button Button(string text, string name, EventHandler? click = null)
    {
        var button = new Button
        {
            Text = text, Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 4), Margin = new Padding(3), UseVisualStyleBackColor = true
        };
        if (click is not null) button.Click += click;
        return button;
    }

    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static TableLayoutPanel Table(int columns = 1)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = columns, Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White,
            Margin = new Padding(4)
        };
        for (var index = 0; index < columns; index++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        return table;
    }

    public static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
        Margin = new Padding(3, 5, 3, 8)
    };

    public static void ShowError(IWin32Window owner, Exception exception) =>
        MessageBox.Show(owner, exception.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal sealed class TextPrompt : Form
{
    private readonly TextBox _text = new() { Dock = DockStyle.Fill, Name = "PromptValue" };
    public string Value => _text.Text.Trim();

    public TextPrompt(string title, string label, string initialValue = "")
    {
        Text = title;
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(440, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        var layout = Ui.Table();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 8) }, 0, 0);
        _text.Text = initialValue;
        layout.Controls.Add(_text, 0, 1);
        var ok = Ui.Button("确定", "PromptOk", (_, _) =>
        {
            if (Value.Length == 0) { _text.Focus(); return; }
            DialogResult = DialogResult.OK;
        });
        var cancel = Ui.Button("取消", "PromptCancel", (_, _) => DialogResult = DialogResult.Cancel);
        var buttons = Ui.Toolbar(ok, cancel);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _text.SelectAll(); _text.Focus(); };
    }
}

internal sealed class FavoriteFolderPicker : Form
{
    private readonly ComboBox _folders = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Name = "FavoriteFolderChoice" };
    public long FolderId => ((FavoriteFolderRecord)_folders.SelectedItem!).Id;

    public FavoriteFolderPicker(IReadOnlyList<FavoriteFolderRecord> folders)
    {
        Text = "添加到偏好文件夹";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(420, 145);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        _folders.DisplayMember = nameof(FavoriteFolderRecord.Name);
        foreach (var folder in folders) _folders.Items.Add(folder);
        if (_folders.Items.Count > 0) _folders.SelectedIndex = 0;
        var layout = Ui.Table();
        layout.Controls.Add(new Label { Text = "选择偏好文件夹（可在偏好区创建文件夹）", AutoSize = true }, 0, 0);
        layout.Controls.Add(_folders, 0, 1);
        var ok = Ui.Button("添加", "FavoriteFolderOk", (_, _) => DialogResult = DialogResult.OK);
        ok.Enabled = folders.Count > 0;
        var cancel = Ui.Button("取消", "FavoriteFolderCancel", (_, _) => DialogResult = DialogResult.Cancel);
        layout.Controls.Add(Ui.Toolbar(ok, cancel), 0, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
