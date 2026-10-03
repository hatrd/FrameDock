namespace FrameDock;

internal sealed class ShortcutHelpForm : Form
{
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StudioTheme.ApplyTitleBar(this);
    }

    public ShortcutHelpForm()
    {
        Text = "FrameDock · 快捷键总览";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(740, 520);
        Size = new Size(800, 700);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = StudioTheme.Canvas;
        Font = StudioTheme.BodyFont;
        ForeColor = StudioTheme.Text;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(20) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill,
            Text = "当前快捷键（只读）\n编辑快捷键仅在主窗口生效；下拉框和输入框保留自己的键盘操作。",
            ForeColor = StudioTheme.Muted }, 0, 0);

        var list = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = StudioTheme.Surface, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.None, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            EnableHeadersVisualStyles = false };
        list.DefaultCellStyle.BackColor = list.BackgroundColor;
        list.DefaultCellStyle.ForeColor = StudioTheme.Text;
        list.DefaultCellStyle.SelectionBackColor = StudioTheme.Selection;
        list.DefaultCellStyle.SelectionForeColor = StudioTheme.Text;
        list.DefaultCellStyle.Padding = new Padding(10, 7, 10, 7);
        list.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        list.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        list.ColumnHeadersDefaultCellStyle.BackColor = StudioTheme.Raised;
        list.ColumnHeadersDefaultCellStyle.ForeColor = StudioTheme.Muted;
        list.ColumnHeadersDefaultCellStyle.SelectionBackColor = StudioTheme.Raised;
        list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        list.ColumnHeadersHeight = 36;
        list.RowTemplate.Height = 24;
        list.RowTemplate.MinimumHeight = 24;
        list.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "按键 / 操作", FillWeight = 34,
            SortMode = DataGridViewColumnSortMode.NotSortable });
        list.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "功能", FillWeight = 66,
            SortMode = DataGridViewColumnSortMode.NotSortable });
        layout.Controls.Add(list, 0, 1);

        void Group(string title, params (string Key, string Action)[] entries)
        {
            var heading = list.Rows[list.Rows.Add(title, "")];
            heading.MinimumHeight = 28;
            heading.DefaultCellStyle.BackColor = StudioTheme.Raised;
            heading.DefaultCellStyle.ForeColor = StudioTheme.Accent;
            foreach (var (key, action) in entries)
                list.Rows.Add(key, action);
        }

        Group("播放与定位",
            ("空格", "播放 / 暂停"),
            ("H / ←", "暂停并后退一帧"),
            ("L / →", "暂停并前进一帧"),
            ("J / ↓", "暂停并后退一秒"),
            ("K / ↑", "暂停并前进一秒"),
            ("Home / Shift+[", "暂停并跳到当前选段起点（不改变边界）"),
            ("End / Shift+]", "暂停并跳到当前选段终点（不改变边界）"));
        Group("选段与时间轴视图",
            ("[", "暂停并将当前画面设为选段起点"),
            ("]", "暂停并将当前画面设为选段终点"),
            ("R", "试听当前选段，到终点自动暂停"),
            ("F", "放大当前选段，保留边界"),
            ("0 / 小键盘 0", "恢复全片视图，保留边界"));
        Group("封面与导出",
            ("C", "锁定当前画面及字幕为封面；再次按 C 替换"),
            ("S", "导出已锁定封面；未锁定时保存当前画面"),
            ("Shift+S", "保存当前画面，不改变锁定封面"),
            ("D", "将当前选段加入后台导出队列"),
            ("Shift+D", "一起导出选段与封面；未锁定时使用当前画面"));
        Group("时间轴鼠标操作",
            ("滚轮", "以鼠标所在时间为中心缩放"),
            ("Shift+滚轮", "平移时间轴视图"),
            ("右键 / 中键拖动", "平移时间轴视图"),
            ("上方 [ / ] 手柄拖动", "调整选段起点 / 终点"),
            ("下方白色指针点击 / 拖动", "暂停并定位画面"),
            ("底部全片总览点击 / 拖动", "定位时间轴视角"),
            ("放大后将指针 / 手柄拖到边缘", "自动滚动时间轴"));
        Group("帮助",
            ("F1", "打开快捷键总览（未打开视频时也可使用）"),
            ("Esc", "关闭快捷键总览"));

        var close = new Button { Text = "关闭 (Esc)", DialogResult = DialogResult.Cancel,
            AutoSize = true, Anchor = AnchorStyles.Right, BackColor = StudioTheme.Raised,
            ForeColor = StudioTheme.Text, FlatStyle = FlatStyle.Flat };
        StudioTheme.StyleButton(close);
        close.Padding = new Padding(12, 5, 12, 5);
        layout.Controls.Add(close, 0, 2);
        CancelButton = close;
    }
}
