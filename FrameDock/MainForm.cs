using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace FrameDock;

internal sealed class MainForm : Form
{
    private sealed record TrackOption(int? Index, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record CoverFrame(MediaInfo Media, double Position, int? SubtitleIndex, byte[] Png, SourceStamp Source);
    private sealed class Job(ExportSpec spec, CoverFrame? cover = null)
    {
        public ExportSpec Spec { get; } = spec;
        public CoverFrame? Cover { get; set; } = cover;
        public CancellationTokenSource Cancellation { get; } = new();
        public string State { get; set; } = "排队";
        public string? Failure { get; set; }
        public override string ToString() => $"{State}  {Path.GetFileName(Spec.OutputPath)}" +
            (Failure is null ? "" : " — " + Failure);
    }

    private readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black, AllowDrop = true };
    private readonly RangeBar range = new() { Dock = DockStyle.Fill };
    private readonly Label positionLabel = new() { AutoSize = true, ForeColor = Color.White };
    private readonly Label rangeLabel = new() { AutoSize = true, ForeColor = Color.White };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, AutoEllipsis = true };
    private readonly ComboBox audio = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205 };
    private readonly ComboBox subtitle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205 };
    private readonly ComboBox previewScale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 86 };
    private readonly CheckBox copy = new() { Text = "纯复制（不重编码）", Checked = true, AutoSize = true, ForeColor = Color.White };
    private readonly CheckBox startup = new() { Text = "开机时启动", AutoSize = true, ForeColor = Color.White };
    private readonly PictureBox coverPreview = new() { Size = new Size(86, 50), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label coverLabel = new() { AutoSize = false, Width = 175, Height = 50, ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolTip tips = new();
    private readonly NumericUpDown parallel = new() { Minimum = 1, Maximum = 4, Value = 1, Width = 48 };
    private readonly ListBox jobs = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(31, 34, 40), ForeColor = Color.White,
        BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly NotifyIcon tray;
    private readonly Queue<Job> waiting = new();
    private readonly HashSet<string> activePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Job> runningJobs = new();
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FrameDock", "settings.json");
    private MediaInfo? media;
    private MpvClient? player;
    private Task? releasingPlayer;
    private CancellationTokenSource? keyframeScan;
    private List<Keyframe>? keyframes;
    private bool loading;
    private CoverFrame? selectedCover;
    private bool capturingCover;
    private bool submittingExport;
    private bool previewingRange;
    private double? pendingSeek;
    private Task seekPump = Task.CompletedTask;
    private bool exiting;
    private bool exitAfterJobs;
    private double lastPosition;
    private readonly string? initialPath;
    private readonly bool startInTray;

    public MainForm(string? initialPath = null, bool startInTray = false)
    {
        this.initialPath = initialPath;
        this.startInTray = startInTray;
        Text = "FrameDock";
        MinimumSize = new Size(860, 680);
        Size = new Size(1040, 800);
        BackColor = Color.FromArgb(24, 27, 32);
        ForeColor = Color.White;
        KeyPreview = true;
        AllowDrop = true;
        Icon = SystemIcons.Application;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, async (_, _) => await ShowFromTrayAsync());
        menu.Items.Add("退出", null, async (_, _) => await ExitFromTrayAsync());
        tray = new NotifyIcon { Icon = Icon, Text = "FrameDock", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += async (_, _) => await ShowFromTrayAsync();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Controls.Add(root);
        root.Controls.Add(video, 0, 0);
        root.Controls.Add(range, 0, 1);

        var readout = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        readout.Controls.Add(positionLabel);
        readout.Controls.Add(new Label { Width = 22 });
        readout.Controls.Add(rangeLabel);
        root.Controls.Add(readout, 0, 2);

        var trimControls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var setStart = Button("[ 设为起点", async () => await SetBoundaryAsync(true));
        var setEnd = Button("] 设为终点", async () => await SetBoundaryAsync(false));
        var preview = Button("R 试听片段", PreviewRangeAsync);
        var fit = Button("F 放大选段", () => { range.FitSelection(); return Task.CompletedTask; });
        var overview = Button("0 全片视图", () => { range.ResetView(); return Task.CompletedTask; });
        var reset = Button("重置范围", () => { range.ResetRange(); return Task.CompletedTask; });
        trimControls.Controls.AddRange([setStart, setEnd, preview, fit, overview, reset]);
        root.Controls.Add(trimControls, 0, 3);
        tips.SetToolTip(setStart, "暂停在当前画面，把左边界设到播放指针（[）。");
        tips.SetToolTip(setEnd, "暂停在当前画面，把右边界设到播放指针（]）。");
        tips.SetToolTip(range, "上方绿/橙手柄拖边界，下方白色指针拖播放位置。滚轮缩放；Shift+滚轮或右键拖动平移；底部总览定位视角。");

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var open = Button("打开视频", async () => await PickVideoAsync());
        var play = Button("播放/暂停", async () => await TogglePauseAsync());
        var export = Button("D 导出片段", async () => await SubmitExportAsync());
        var outputFolder = Button("打开输出目录", () => {
            if (media is not null) Process.Start(new ProcessStartInfo(Path.GetDirectoryName(media.Path)!) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        controls.Controls.AddRange([open, play, export, copy, outputFolder]);
        root.Controls.Add(controls, 0, 4);
        tips.SetToolTip(copy, "纯复制会从所选起点之前的关键帧开始，可能多留一小段头部。需要准确去头时取消勾选。");

        var coverControls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var chooseCover = Button("C 选封面", SelectCoverAsync);
        var shot = Button("S 导出封面", async () => await ScreenshotAsync());
        var bundle = Button("Shift+D 片段 + 封面", async () => await SubmitExportAsync(true));
        var clearCover = Button("清除封面", () => { ClearCover(); return Task.CompletedTask; });
        coverControls.Controls.AddRange([coverPreview, coverLabel, chooseCover, shot, bundle, clearCover]);
        root.Controls.Add(coverControls, 0, 5);
        tips.SetToolTip(chooseCover, "锁定当前画面和字幕为封面。之后移动播放指针不会改变它。");
        tips.SetToolTip(shot, "导出已选封面；尚未选封面时导出当前画面。Shift+S 始终截取当前画面。");
        tips.SetToolTip(bundle, "将片段加入后台队列，并保存同名 PNG 封面。尚未选封面时使用当前画面。");

        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(lower, 0, 6);
        var selectors = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        selectors.Controls.AddRange([TinyLabel("音轨"), audio, TinyLabel("字幕"), subtitle,
            TinyLabel("预览"), previewScale]);
        lower.Controls.Add(selectors, 0, 0);
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        settings.Controls.AddRange([TinyLabel("并发"), parallel, startup]);
        lower.Controls.Add(settings, 0, 1);
        lower.Controls.Add(jobs, 0, 2);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.Silver, Text = "滚轮缩放 · Shift+滚轮平移 · ←/→ 逐帧 · ↑/↓ 跳秒 · [ / ] 去头尾 · C 选封面 · Shift+D 一起导出", AutoEllipsis = true }, 0, 7);
        root.Controls.Add(status, 0, 8);

        var jobsMenu = new ContextMenuStrip();
        jobsMenu.Items.Add("取消所选任务", null, (_, _) => CancelSelectedJob());
        jobs.ContextMenuStrip = jobsMenu;

        audio.SelectedIndexChanged += async (_, _) => await SelectTrackAsync("aid", audio);
        subtitle.SelectedIndexChanged += async (_, _) => await SelectTrackAsync("sid", subtitle);
        previewScale.Items.AddRange(["原尺寸", "1/2", "1/4"]);
        previewScale.SelectedIndex = 0;
        previewScale.SelectedIndexChanged += async (_, _) => await SetPreviewScaleAsync();
        range.SeekRequested += async time => await SeekAsync(time);
        range.RangeChanged += UpdateRange;
        copy.CheckedChanged += (_, _) => UpdateRange();
        parallel.ValueChanged += (_, _) => { SaveSettings(); PumpQueue(); };
        startup.CheckedChanged += (_, _) => UpdateStartup();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        video.DragEnter += OnDragEnter;
        video.DragDrop += OnDragDrop;
        FormClosing += OnClosing;
        Shown += async (_, _) => {
            if (this.startInTray && this.initialPath is null) Hide();
            await InitializeAsync();
            if (this.initialPath is not null) await OpenVideoAsync(this.initialPath);
        };
        LoadSettings();
        SetStatus("把 MKV 或 MP4 拖进窗口。");
        ClearCover();
        UpdatePosition();
        UpdateRange();
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(43, 49, 59), ForeColor = Color.White };
        button.FlatAppearance.BorderColor = Color.FromArgb(78, 88, 104);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Label TinyLabel(string text) => new() {
        Text = text, AutoSize = true, ForeColor = Color.White, Margin = new Padding(7, 8, 2, 0)
    };

    private void SetStatus(string message) => status.Text = message;
    private void Error(Exception error)
    {
        SetStatus(error.Message);
        MessageBox.Show(this, error.Message, "FrameDock", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private async Task InitializeAsync()
    {
        while (true)
        {
            try { await ToolPaths.EnsureFfmpegAsync(); return; }
            catch (Exception error)
            {
                SetStatus(error.Message);
                var choice = MessageBox.Show(this, error.Message + "\n\n重试安装或检测 FFmpeg？", "FrameDock",
                    MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                if (choice != DialogResult.Retry) return;
            }
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await OpenVideoAsync(files[0]);
    }

    private async Task PickVideoAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "视频|*.mkv;*.mp4;*.mov;*.webm;*.avi|所有文件|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await OpenVideoAsync(dialog.FileName);
    }

    private async Task OpenVideoAsync(string path)
    {
        if (loading || capturingCover || submittingExport)
        {
            SetStatus("正在读取画面或提交导出，请稍后再打开视频。");
            return;
        }
        try
        {
            loading = true;
            previewingRange = false;
            await seekPump;
            keyframeScan?.Cancel();
            if (player is not null) { await player.DisposeAsync(); player = null; }
            var info = await Probe.ReadAsync(path);
            media = info;
            ClearCover();
            keyframes = null;
            lastPosition = 0;
            range.Duration = info.Duration;
            UpdatePosition();
            audio.Items.Clear();
            audio.Items.Add(new TrackOption(null, "无音频"));
            foreach (var track in info.Audio) audio.Items.Add(new TrackOption(track.Index, track.Label));
            var preferred = info.Audio.FirstOrDefault(t => t.IsDefault) ?? info.Audio.FirstOrDefault();
            audio.SelectedIndex = preferred is null ? 0 : info.Audio.ToList().FindIndex(t => t.Index == preferred.Index) + 1;
            subtitle.Items.Clear();
            subtitle.Items.Add(new TrackOption(null, "关闭字幕"));
            foreach (var track in info.Subtitles) subtitle.Items.Add(new TrackOption(track.Index, track.Label));
            subtitle.SelectedIndex = 0;
            await AttachPlayerAsync(path, 0);
            SetStatus($"已打开 {Path.GetFileName(path)}；正在分析可复制切点…");
            var scan = keyframeScan = new CancellationTokenSource();
            _ = ScanKeyframesAsync(info, scan.Token);
        }
        catch (Exception error) { Error(error); }
        finally { loading = false; }
    }

    private async Task WaitForTracksAsync()
    {
        if (player is null) return;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                var list = await player.PropertyAsync("track-list");
                if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0) return;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("视频预览加载超时。");
    }

    private async Task AttachPlayerAsync(string path, double restorePosition)
    {
        player = await MpvClient.StartAsync(video.Handle);
        try
        {
            var attachedPlayer = player;
            player.PositionChanged += position => {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() => {
                    if (player != attachedPlayer || loading || !seekPump.IsCompleted || range.IsScrubbing) return;
                    lastPosition = position;
                    range.Position = position;
                    UpdatePosition();
                    if (previewingRange && position >= range.End)
                    {
                        previewingRange = false;
                        _ = StopRangePreviewAsync();
                    }
                });
            };
            await player.LoadAsync(path, media!.TimelineOrigin);
            await WaitForTracksAsync();
            await player.SetTrackAsync("aid", (audio.SelectedItem as TrackOption)?.Index);
            await player.SetTrackAsync("sid", (subtitle.SelectedItem as TrackOption)?.Index);
            await player.CommandAsync("set_property", "video-zoom", -previewScale.SelectedIndex);
            if (restorePosition > 0) await player.SeekAsync(restorePosition);
            await player.PauseAsync(true);
        }
        catch
        {
            await player.DisposeAsync();
            player = null;
            throw;
        }
    }

    private async Task ScanKeyframesAsync(MediaInfo target, CancellationToken cancellation)
    {
        try
        {
            var result = await Probe.ReadKeyframesAsync(target.Path, cancellation);
            if (cancellation.IsCancellationRequested || media != target || IsDisposed) return;
            BeginInvoke(() => { keyframes = result; UpdateRange(); SetStatus($"已找到 {result.Count} 个可复制切点。"); });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed) BeginInvoke(() => SetStatus(error.Message));
        }
    }

    private async Task SelectTrackAsync(string property, ComboBox selector)
    {
        if (loading || player is null || selector.SelectedItem is not TrackOption choice) return;
        try { await player.SetTrackAsync(property, choice.Index); }
        catch (Exception error) { Error(error); }
    }

    private async Task SetPreviewScaleAsync()
    {
        if (player is null) return;
        try { await player.CommandAsync("set_property", "video-zoom", -previewScale.SelectedIndex); }
        catch (Exception error) { Error(error); }
    }

    private Task SeekAsync(double time)
    {
        if (player is null || loading) return Task.CompletedTask;
        previewingRange = false;
        pendingSeek = Math.Clamp(time, 0, range.Duration);
        if (seekPump.IsCompleted) seekPump = PumpSeekAsync();
        return seekPump;
    }

    private async Task PumpSeekAsync()
    {
        var target = player;
        if (target is null) return;
        try
        {
            await target.PauseAsync(true);
            while (pendingSeek is double time && player == target)
            {
                pendingSeek = null;
                await target.SeekAsync(time);
                if (pendingSeek is not null || player != target) continue;
                lastPosition = (await target.PropertyAsync("time-pos")).GetDouble();
                if (pendingSeek is not null) continue;
                if (!range.IsScrubbing) range.Position = lastPosition;
                UpdatePosition();
            }
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally { pendingSeek = null; }
    }

    private async Task SetBoundaryAsync(bool isStart)
    {
        if (player is null || loading) return;
        try
        {
            await seekPump;
            await player.PauseAsync(true);
            previewingRange = false;
            var time = (await player.PropertyAsync("time-pos")).GetDouble();
            if (isStart && time >= range.End || !isStart && time <= range.Start)
            {
                SetStatus(isStart ? "起点必须早于终点；先移动终点或重置范围。" : "终点必须晚于起点；先移动起点或重置范围。");
                return;
            }
            if (isStart) range.Start = time; else range.End = time;
            lastPosition = range.Position = time;
            range.EnsureVisible(time);
            UpdatePosition();
            SetStatus($"已将{(isStart ? "起点" : "终点")}设为 {Clock(time)}。");
        }
        catch (Exception error) { Error(error); }
    }

    private async Task PreviewRangeAsync()
    {
        if (player is null || loading) return;
        try
        {
            await SeekAsync(range.Start);
            range.EnsureVisible(range.Start);
            previewingRange = true;
            await player.PauseAsync(false);
            SetStatus("正在试听所选片段，到终点自动暂停。空格可随时暂停。");
        }
        catch (Exception error) { previewingRange = false; Error(error); }
    }

    private async Task StopRangePreviewAsync()
    {
        if (player is null) return;
        try { await SeekAsync(range.End); SetStatus("片段试听结束。"); }
        catch (Exception error) { SetStatus(error.Message); }
    }

    private double ActualStart()
    {
        if (!copy.Checked || keyframes is not { Count: > 0 }) return range.Start;
        var index = keyframes.FindLastIndex(frame => frame.Position <= range.Start + 0.0005);
        return index < 0 ? Math.Max(0, keyframes[0].Position) : Math.Max(0, keyframes[index].Position);
    }

    private void UpdateRange()
    {
        if (IsDisposed) return;
        var actual = ActualStart();
        rangeLabel.Text = $"保留 {Clock(range.Start)}–{Clock(range.End)}  ({range.End - range.Start:F3}s)" +
            (copy.Checked ? keyframes is null ? "  · 切点分析中" : $"  · 复制起点 {Clock(actual)}" : "  · 精确截取");
    }

    private void UpdatePosition() => positionLabel.Text = $"画面 {Clock(lastPosition)}";
    private static string Clock(double time) => TimeSpan.FromSeconds(Math.Max(0, time)).ToString(
        time >= 3600 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private async Task TogglePauseAsync()
    {
        if (player is null) return;
        try
        {
            var paused = await player.PropertyAsync("pause");
            previewingRange = false;
            await player.PauseAsync(!paused.GetBoolean());
        }
        catch (Exception error) { Error(error); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & (Keys.Control | Keys.Alt)) != 0) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        // Let selectors keep their own arrows and typing; buttons and the timeline use editing shortcuts.
        if (ActiveControl is ComboBox or NumericUpDown or TextBoxBase || parallel.ContainsFocus)
            return base.ProcessCmdKey(ref msg, keyData);
        if (key is Keys.D0 or Keys.NumPad0) { range.ResetView(); return true; }
        if (key == Keys.F) { range.FitSelection(); return true; }
        if (player is null || loading) return base.ProcessCmdKey(ref msg, keyData);
        if (key is not (Keys.Space or Keys.H or Keys.Left or Keys.L or Keys.Right or Keys.J or Keys.Down or
            Keys.K or Keys.Up or Keys.S or Keys.D or Keys.C or Keys.R or Keys.OemOpenBrackets or Keys.OemCloseBrackets or Keys.Home or Keys.End))
            return base.ProcessCmdKey(ref msg, keyData);
        _ = HandleShortcutAsync(key, (keyData & Keys.Shift) != 0);
        return true;
    }

    private async Task HandleShortcutAsync(Keys key, bool shift)
    {
        if (player is null) return;
        try
        {
            await seekPump;
            switch (key)
            {
                case Keys.Space: await TogglePauseAsync(); break;
                case Keys.H: case Keys.Left: previewingRange = false; await player.PauseAsync(true); await player.StepAsync(true); break;
                case Keys.L: case Keys.Right: previewingRange = false; await player.PauseAsync(true); await player.StepAsync(false); break;
                case Keys.J: case Keys.Down: previewingRange = false; await player.PauseAsync(true); await player.JumpAsync(-1); break;
                case Keys.K: case Keys.Up: previewingRange = false; await player.PauseAsync(true); await player.JumpAsync(1); break;
                case Keys.OemOpenBrackets: await SetBoundaryAsync(true); break;
                case Keys.OemCloseBrackets: await SetBoundaryAsync(false); break;
                case Keys.Home: await SeekAsync(range.Start); break;
                case Keys.End: await SeekAsync(range.End); break;
                case Keys.R: await PreviewRangeAsync(); break;
                case Keys.C: await SelectCoverAsync(); break;
                case Keys.S: await ScreenshotAsync(shift); break;
                case Keys.D: await SubmitExportAsync(shift); break;
                default: return;
            }
            if (key is Keys.H or Keys.Left or Keys.L or Keys.Right or Keys.J or Keys.Down or Keys.K or Keys.Up or Keys.Home or Keys.End)
            {
                lastPosition = (await player.PropertyAsync("time-pos")).GetDouble();
                range.Position = lastPosition;
                range.EnsureVisible(lastPosition);
                UpdatePosition();
            }
        }
        catch (Exception error) { Error(error); }
    }

    private void ClearCover()
    {
        selectedCover = null;
        coverPreview.Image?.Dispose();
        coverPreview.Image = null;
        coverLabel.Text = "封面未锁定\n导出时使用当前画面";
    }

    private async Task<CoverFrame?> CaptureCoverAsync()
    {
        if (player is null || media is null || loading || capturingCover) return null;
        capturingCover = true;
        var source = media;
        var target = player;
        var temporary = Path.Combine(Path.GetTempPath(), "framedock-cover-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await seekPump;
            previewingRange = false;
            await target.PauseAsync(true);
            var stamp = SourceStamp.Capture(source.Path);
            var position = (await target.PropertyAsync("time-pos")).GetDouble();
            var sub = (subtitle.SelectedItem as TrackOption)?.Index;
            await target.ScreenshotAsync(temporary, sub is not null);
            using var saved = Image.FromFile(temporary);
            if (saved.Width <= 0 || saved.Height <= 0) throw new InvalidOperationException("封面尺寸无效。");
            if (SourceStamp.Capture(source.Path) != stamp) throw new InvalidOperationException("源视频已变化，请重新打开后选封面。");
            return new CoverFrame(source, position, sub, await File.ReadAllBytesAsync(temporary), stamp);
        }
        finally
        {
            capturingCover = false;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task SelectCoverAsync()
    {
        try
        {
            var cover = await CaptureCoverAsync();
            if (cover is null) return;
            selectedCover = cover;
            using var stream = new MemoryStream(cover.Png);
            using var image = Image.FromStream(stream);
            coverPreview.Image?.Dispose();
            coverPreview.Image = new Bitmap(image);
            coverLabel.Text = $"封面 {Clock(cover.Position)}\n已锁定 · {(cover.SubtitleIndex is null ? "无字幕" : "含字幕")}";
            SetStatus("封面已锁定；可继续调整首尾，Shift+D 一起导出。");
        }
        catch (Exception error) { Error(error); }
    }

    private static async Task SaveCoverAsync(CoverFrame cover, string path)
    {
        if (SourceStamp.Capture(cover.Media.Path) != cover.Source)
            throw new InvalidOperationException("源视频已变化，请重新打开后选封面。");
        if (File.Exists(path))
        {
            try
            {
                using var existing = Image.FromFile(path);
                if (existing.Width <= 0 || existing.Height <= 0) throw new InvalidOperationException("图片尺寸无效。");
                return;
            }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or InvalidOperationException)
            {
                ArchiveInvalid(path);
            }
        }
        var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(temporary, cover.Png); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task ScreenshotAsync(bool currentFrame = false)
    {
        if (media is null || loading) return;
        try
        {
            var cover = currentFrame ? await CaptureCoverAsync() : selectedCover ?? await CaptureCoverAsync();
            if (cover is null) return;
            var path = OutputNames.Screenshot(cover.Media, cover.Position, cover.SubtitleIndex);
            await SaveCoverAsync(cover, path);
            SetStatus("封面已保存：" + path);
        }
        catch (Exception error) { Error(error); }
    }

    private async Task SubmitExportAsync(bool includeCover = false)
    {
        if (media is null || loading || submittingExport || capturingCover) return;
        submittingExport = true;
        try
        {
            if (copy.Checked && keyframes is null)
            {
                SetStatus("正在分析可复制切点，请稍候。");
                return;
            }
            var sub = (subtitle.SelectedItem as TrackOption)?.Index;
            var audioTrack = (audio.SelectedItem as TrackOption)?.Index;
            var doCopy = copy.Checked;
            var burn = sub is not null;
            if (doCopy && burn)
            {
                var choice = MessageBox.Show(this,
                    "当前显示字幕，纯复制无法把字幕画入视频。\n\n是：画入字幕并使用精确模式\n否：本次不带字幕，继续纯复制\n取消：返回",
                    "字幕与纯复制", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel) return;
                doCopy = choice == DialogResult.No;
                burn = choice == DialogResult.Yes;
            }
            if (!doCopy && media.VideoCodec is not ("h264" or "hevc"))
            {
                var proceed = MessageBox.Show(this,
                    $"源视频编码为 {media.VideoCodec}。精确模式将输出 H.264 视频，所选音轨尽量原样复制。继续？",
                    "视频编码", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (proceed != DialogResult.Yes) return;
            }
            var start = doCopy ? ActualStart() : range.Start;
            if (range.End - start < 0.001) throw new InvalidOperationException("截取范围太短。");
            var extension = Exporter.ExtensionFor(media, audioTrack);
            if (extension != Path.GetExtension(media.Path).ToLowerInvariant())
            {
                var proceed = MessageBox.Show(this, "所选音轨与源容器可能不兼容，改用 MKV 导出？", "容器兼容性",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (proceed != DialogResult.Yes) return;
            }
            var output = OutputNames.Clip(media, start, range.End, audioTrack, burn ? sub : null, doCopy, burn, extension);
            var copyKeyframe = doCopy ? keyframes?.FindLast(frame => frame.Position <= start + 0.0005) : null;
            var spec = new ExportSpec(media, start, range.End, audioTrack, burn ? sub : null, doCopy, burn, output,
                copyKeyframe, SourceStamp.Capture(media.Path));
            var cover = includeCover ? selectedCover ?? await CaptureCoverAsync() : null;
            if (includeCover && cover is null) return;
            if (cover is not null && (cover.Media != media || cover.Source != spec.Source))
                throw new InvalidOperationException("源视频已变化，请重新打开后选封面。");
            if (File.Exists(output))
            {
                try
                {
                    await Exporter.VerifyAsync(spec, output, CancellationToken.None);
                }
                catch (Exception error) when (error is InvalidOperationException or FormatException)
                {
                    var archived = ArchiveInvalid(output);
                    SetStatus("既有片段验证失败，原文件保留在 " + archived + "；正在重新生成。");
                }
                if (File.Exists(output))
                {
                    if (cover is not null) await SaveCoverAsync(cover, OutputNames.ClipCover(output, cover.Media, cover.Position, cover.SubtitleIndex));
                    SetStatus((cover is null ? "片段已完成：" : "片段与封面已保存：") + output);
                    return;
                }
            }
            if (activePaths.Contains(output))
            {
                var existing = jobs.Items.OfType<Job>().First(j => j.Spec.OutputPath == output && j.State is "排队" or "运行");
                if (cover is not null && existing.Cover is null) existing.Cover = cover;
                SetStatus("任务已在排队或运行：" + output);
                return;
            }
            var job = new Job(spec, cover);
            waiting.Enqueue(job);
            activePaths.Add(output);
            jobs.Items.Add(job);
            PumpQueue();
            SetStatus(cover is null ? "片段已加入导出队列。" : "片段与已锁定的封面已加入导出队列。");
        }
        catch (Exception error) { Error(error); }
        finally { submittingExport = false; }
    }

    private static string ArchiveInvalid(string path)
    {
        var archived = path + ".invalid-" + Guid.NewGuid().ToString("N");
        File.Move(path, archived);
        return archived;
    }

    private void PumpQueue()
    {
        while (runningJobs.Count < (int)parallel.Value && waiting.Count > 0)
        {
            var job = waiting.Dequeue();
            if (job.Cancellation.IsCancellationRequested)
            {
                UpdateJob(job, "取消");
                job.Cover = null;
                activePaths.Remove(job.Spec.OutputPath);
                job.Cancellation.Dispose();
                continue;
            }
            runningJobs.Add(job);
            UpdateJob(job, "运行");
            _ = RunJobAsync(job);
        }
    }

    private void UpdateJob(Job job, string state)
    {
        job.State = state;
        var index = jobs.Items.IndexOf(job);
        if (index >= 0) jobs.Items[index] = job;
    }

    private void CancelSelectedJob()
    {
        if (jobs.SelectedItem is not Job job || job.State is not ("排队" or "运行")) return;
        job.Cancellation.Cancel();
        SetStatus("已请求取消：" + Path.GetFileName(job.Spec.OutputPath));
        PumpQueue();
    }

    private async Task RunJobAsync(Job job)
    {
        try
        {
            var output = await Exporter.RunAsync(job.Spec, job.Cancellation.Token);
            if (job.Cover is { } cover)
                await SaveCoverAsync(cover, OutputNames.ClipCover(job.Spec.OutputPath, cover.Media, cover.Position, cover.SubtitleIndex));
            UpdateJob(job, "完成");
            var timeline = output.Verified!;
            SetStatus($"{(job.Cover is null ? "片段" : "片段与封面")}已保存（核实源视频首帧 {Clock(timeline.FirstVideoSourcePosition)}，末帧 {Clock(timeline.LastVideoSourcePosition)}，" +
                $"成品时长 {output.Duration:F3} 秒）：{job.Spec.OutputPath}");
        }
        catch (OperationCanceledException) { UpdateJob(job, "取消"); }
        catch (Exception error) { job.Failure = error.Message; UpdateJob(job, "失败"); SetStatus(error.Message); }
        finally
        {
            job.Cancellation.Dispose();
            job.Cover = null;
            activePaths.Remove(job.Spec.OutputPath);
            runningJobs.Remove(job);
            PumpQueue();
            if (exitAfterJobs && runningJobs.Count == 0 && waiting.Count == 0) ExitNow();
        }
    }

    private async Task ShowFromTrayAsync()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (releasingPlayer is not null) await releasingPlayer;
        if (media is not null && player is null) await ResumePlayerAsync();
    }

    private async Task ResumePlayerAsync()
    {
        if (media is null || player is not null) return;
        try
        {
            await AttachPlayerAsync(media.Path, lastPosition);
        }
        catch (Exception error) { Error(error); }
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        Hide();
        previewingRange = false;
        await seekPump;
        if (player is not null)
        {
            var previous = player;
            player = null;
            releasingPlayer = previous.DisposeAsync().AsTask();
            await releasingPlayer;
            releasingPlayer = null;
        }
        SetStatus("已缩回托盘；预览资源已释放。");
    }

    private async Task ExitFromTrayAsync()
    {
        if (runningJobs.Count + waiting.Count == 0) { ExitNow(); return; }
        var choice = MessageBox.Show(this,
            "还有导出任务。\n\n是：等待完成后退出\n否：取消任务并退出\n取消：继续运行",
            "退出 FrameDock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        if (choice == DialogResult.Yes)
        {
            exitAfterJobs = true;
            Hide();
            SetStatus("任务完成后退出。");
            return;
        }
        foreach (var job in runningJobs) job.Cancellation.Cancel();
        while (waiting.TryDequeue(out var job))
        {
            activePaths.Remove(job.Spec.OutputPath);
            job.Cancellation.Dispose();
        }
        exitAfterJobs = true;
        if (runningJobs.Count == 0) ExitNow();
        await Task.CompletedTask;
    }

    private void ExitNow()
    {
        exiting = true;
        keyframeScan?.Cancel();
        tray.Visible = false;
        tray.Dispose();
        if (player is not null) _ = player.DisposeAsync();
        Close();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("maxParallel", out var value))
                    parallel.Value = Math.Clamp(value.GetInt32(), 1, 4);
            }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            startup.Checked = key?.GetValue("FrameDock") is not null;
        }
        catch (Exception error) { SetStatus("读取设置失败：" + error.Message); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { maxParallel = (int)parallel.Value }));
        }
        catch (Exception error) { SetStatus("保存设置失败：" + error.Message); }
    }

    private void UpdateStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (startup.Checked) key.SetValue("FrameDock", "\"" + Environment.ProcessPath + "\" --tray");
            else key.DeleteValue("FrameDock", false);
        }
        catch (Exception error) { Error(error); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tips.Dispose();
            coverPreview.Image?.Dispose();
            tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
