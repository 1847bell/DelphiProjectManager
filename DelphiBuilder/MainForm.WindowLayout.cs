using System.Text.Json;

namespace DelphiBuilder;

public sealed partial class MainForm
{
    private const string LayoutStateKey = "windowLayout";
    private SplitContainer _workspaceSplit = null!;
    private SplitContainer _detailsSplit = null!;
    private SplitContainer _favoritesSplit = null!;
    private WindowLayout? _savedLayout;

    private sealed class WindowLayout
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; }
        public string Monitor { get; set; } = "";
        public int MonitorLeft { get; set; }
        public int MonitorTop { get; set; }
        public bool Maximized { get; set; }
        public double WorkspaceRatio { get; set; }
        public double MiddleRatio { get; set; }
        public double FavoritesRatio { get; set; }
        public int[] ProjectColumnWidths { get; set; } = [];
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var json = _store.GetState(LayoutStateKey);
        if (json is not null)
        {
            try { _savedLayout = JsonSerializer.Deserialize<WindowLayout>(json); }
            catch (JsonException) { _savedLayout = null; }
        }
        if (_savedLayout is { } layout && (layout.Width <= 0 || layout.Height <= 0 || layout.Dpi <= 0
            || !ValidRatio(layout.WorkspaceRatio) || !ValidRatio(layout.MiddleRatio) || !ValidRatio(layout.FavoritesRatio)))
            _savedLayout = null;

        var screen = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == _savedLayout?.Monitor)
            ?? Screen.FromControl(this);
        var area = screen.WorkingArea;
        // Move onto the chosen monitor first, so DeviceDpi reflects its scaling before restoring sizes.
        StartPosition = FormStartPosition.Manual;
        Location = area.Location;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        if (_savedLayout is { } saved)
        {
            var scale = (double)DeviceDpi / saved.Dpi;
            var width = (int)Math.Clamp(Math.Round(saved.Width * scale), MinimumSize.Width, area.Width);
            var height = (int)Math.Clamp(Math.Round(saved.Height * scale), MinimumSize.Height, area.Height);
            var left = area.Left + Math.Clamp(((double)saved.Left - saved.MonitorLeft) * scale, 0, area.Width - width);
            var top = area.Top + Math.Clamp(((double)saved.Top - saved.MonitorTop) * scale, 0, area.Height - height);
            Bounds = new Rectangle((int)Math.Round(left), (int)Math.Round(top), width, height);
            if (saved.Maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
    }

    private void RestorePaneLayout()
    {
        PerformLayout();
        if (_savedLayout is { } saved)
        {
            SetSplitRatio(_workspaceSplit, saved.WorkspaceRatio);
            SetSplitRatio(_detailsSplit, saved.MiddleRatio);
            SetSplitRatio(_favoritesSplit, saved.FavoritesRatio);
            var widths = saved.ProjectColumnWidths ?? [];
            for (var index = 0; index < Math.Min(widths.Length, _projects.Columns.Count); index++)
                if (widths[index] > 0)
                    _projects.Columns[index].Width = (int)Math.Clamp(Math.Round(widths[index] * (double)DeviceDpi / saved.Dpi), 24, 65535);
        }
        else
        {
            var workspaceWidth = Math.Max(SingleRowPaneWidth(_addWorkspace.Parent!), (int)Math.Round(225d * DeviceDpi / 96));
            var middleWidth = Math.Max(SingleRowPaneWidth(_favoriteActions[0].Parent!), (int)Math.Round(450d * DeviceDpi / 96));
            var detailsWidth = SingleRowPaneWidth(_build.Parent!);
            var extraWidth = workspaceWidth + middleWidth + detailsWidth + _workspaceSplit.SplitterWidth + _detailsSplit.SplitterWidth
                - _workspaceSplit.ClientSize.Width;
            var area = Screen.FromControl(this).WorkingArea;
            if (extraWidth > 0)
            {
                var width = Math.Min(Width + extraWidth, area.Width);
                Bounds = new Rectangle(area.Left + (area.Width - width) / 2, Top, width, Height);
                PerformLayout();
            }
            SetSplitDistance(_workspaceSplit, workspaceWidth);
            SetSplitDistance(_detailsSplit, middleWidth);
        }
    }

    private Task SaveLayoutAsync()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var screen = Screen.FromControl(this);
        var layout = new WindowLayout
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Dpi = DeviceDpi, Monitor = screen.DeviceName, MonitorLeft = screen.WorkingArea.Left, MonitorTop = screen.WorkingArea.Top,
            Maximized = WindowState == FormWindowState.Maximized,
            WorkspaceRatio = SplitRatio(_workspaceSplit), MiddleRatio = SplitRatio(_detailsSplit), FavoritesRatio = SplitRatio(_favoritesSplit),
            ProjectColumnWidths = _projects.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray()
        };
        _store.SetState(LayoutStateKey, JsonSerializer.Serialize(layout));
        _status.Text = "布局已保存，下次启动将恢复窗口大小、分栏和项目列宽。";
        return Task.CompletedTask;
    }

    private static bool ValidRatio(double ratio) => double.IsFinite(ratio) && ratio is >= 0 and <= 1;

    private static double SplitRatio(SplitContainer split) => (double)split.SplitterDistance /
        Math.Max(1, SplitExtent(split) - split.SplitterWidth);

    private static int SplitExtent(SplitContainer split) => split.Orientation == Orientation.Vertical
        ? split.ClientSize.Width : split.ClientSize.Height;

    private static void SetSplitRatio(SplitContainer split, double ratio) =>
        SetSplitDistance(split, (int)Math.Round((SplitExtent(split) - split.SplitterWidth) * ratio));

    private static void SetSplitDistance(SplitContainer split, int distance)
    {
        var maximum = SplitExtent(split) - split.SplitterWidth - split.Panel2MinSize;
        if (maximum >= split.Panel1MinSize)
            split.SplitterDistance = Math.Clamp(distance, split.Panel1MinSize, maximum);
        split.PerformLayout();
    }

    private static int SingleRowPaneWidth(Control toolbar) => toolbar.Controls.Cast<Control>()
        .Sum(control => control.GetPreferredSize(Size.Empty).Width + control.Margin.Horizontal)
        + toolbar.Padding.Horizontal + toolbar.Margin.Horizontal + toolbar.Parent!.Padding.Horizontal + toolbar.Parent.Margin.Horizontal;
}
