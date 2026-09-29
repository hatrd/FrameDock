using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace FrameDock;

internal sealed class MainForm : Form
{
    private sealed record TrackOption(int? Index, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record Job(ExportSpec Spec, CancellationTokenSource Cancellation);

    private readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black, AllowDrop = true };
    private readonly RangeBar range = new() { Dock = DockStyle.Fill };
    private readonly Label positionLabel = new() { AutoSize = true, ForeColor = Color.White };
    private readonly Label rangeLabel = new() { AutoSize = true, ForeColor = Color.White };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, AutoEllipsis = true };
    private readonly ComboBox audio = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205 };
    private readonly ComboBox subtitle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205 };
    private readonly ComboBox previewScale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 86 };
    private readonly CheckBox copy = new() { Text = "纯复制（不重编码）", Checked = true, AutoSize = true, ForeColor = Color.White };
    private readonly CheckBox startup = new() { Text = "登录时启动", AutoSize = true, ForeColor = Color.White };
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
    private bool snapping;
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
        MinimumSize = new Size(680, 480);
        Size = new Size(920, 680);
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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Controls.Add(root);
        root.Controls.Add(video, 0, 0);
        root.Controls.Add(range, 0, 1);

        var readout = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        readout.Controls.Add(positionLabel);
        readout.Controls.Add(new Label { Width = 22 });
        readout.Controls.Add(rangeLabel);
        root.Controls.Add(readout, 0, 2);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var open = Button("打开视频", async () => await PickVideoAsync());
        var play = Button("播放/暂停", async () => await TogglePauseAsync());
        var shot = Button("S 截图", async () => await ScreenshotAsync());
        var export = Button("D 导出", async () => await SubmitExportAsync());
        controls.Controls.AddRange([open, play, shot, export, copy]);
        root.Controls.Add(controls, 0, 3);

        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 73));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(lower, 0, 4);
        var selectors = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        selectors.Controls.AddRange([TinyLabel("音轨"), audio, TinyLabel("字幕"), subtitle,
            TinyLabel("预览"), previewScale]);
        lower.Controls.Add(selectors, 0, 0);
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        settings.Controls.AddRange([TinyLabel("并发"), parallel, startup]);
        lower.Controls.Add(settings, 1, 0);
        lower.Controls.Add(jobs, 0, 1);
        lower.SetColumnSpan(jobs, 2);
        root.Controls.Add(status, 0, 5);

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
        KeyDown += OnKeyDown;
        FormClosing += OnClosing;
        Shown += async (_, _) => {
            if (this.startInTray && this.initialPath is null) Hide();
            await InitializeAsync();
            if (this.initialPath is not null) await OpenVideoAsync(this.initialPath);
        };
        LoadSettings();
        SetStatus("把 MKV 或 MP4 拖进窗口。");
        UpdateRange();
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2) };
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
        try { await ToolPaths.EnsureFfmpegAsync(); }
        catch (Exception error) { Error(error); }
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
        try
        {
            loading = true;
            keyframeScan?.Cancel();
            if (player is not null) { await player.DisposeAsync(); player = null; }
            var info = await Probe.ReadAsync(path);
            media = info;
            keyframes = null;
            lastPosition = 0;
            range.Duration = info.Duration;
            audio.Items.Clear();
            audio.Items.Add(new TrackOption(null, "无音频"));
            foreach (var track in info.Audio) audio.Items.Add(new TrackOption(track.Index, track.Label));
            var preferred = info.Audio.FirstOrDefault(t => t.IsDefault) ?? info.Audio.FirstOrDefault();
            audio.SelectedIndex = preferred is null ? 0 : info.Audio.ToList().FindIndex(t => t.Index == preferred.Index) + 1;
            subtitle.Items.Clear();
            subtitle.Items.Add(new TrackOption(null, "关闭字幕"));
            foreach (var track in info.Subtitles) subtitle.Items.Add(new TrackOption(track.Index, track.Label));
            subtitle.SelectedIndex = 0;
            player = await MpvClient.StartAsync(video.Handle);
            player.PositionChanged += position => {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() => { lastPosition = position; range.Position = position; UpdatePosition(); });
            };
            await player.LoadAsync(path);
            await WaitForTracksAsync();
            loading = false;
            await SelectTrackAsync("aid", audio);
            await SelectTrackAsync("sid", subtitle);
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

    private async Task SeekAsync(double time)
    {
        if (player is null) return;
        try { await player.SeekAsync(time); }
        catch (Exception error) { SetStatus(error.Message); }
    }

    private double ActualStart()
    {
        if (!copy.Checked || keyframes is not { Count: > 0 }) return range.Start;
        var index = keyframes.FindLastIndex(frame => frame.Pts <= range.Start + 0.0005);
        return index < 0 ? 0 : keyframes[index].Pts;
    }

    private void UpdateRange()
    {
        if (snapping || IsDisposed) return;
        var actual = ActualStart();
        if (copy.Checked && keyframes is not null && Math.Abs(actual - range.Start) > 0.001)
        {
            snapping = true;
            range.Start = actual;
            snapping = false;
        }
        rangeLabel.Text = $"范围 {Clock(range.Start)}–{Clock(range.End)}  |  预计 {Clock(actual)}–{Clock(range.End)}";
    }

    private void UpdatePosition() => positionLabel.Text = $"画面 {Clock(lastPosition)}";
    private static string Clock(double time) => TimeSpan.FromSeconds(Math.Max(0, time)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private async Task TogglePauseAsync()
    {
        if (player is null) return;
        try
        {
            var paused = await player.PropertyAsync("pause");
            await player.PauseAsync(!paused.GetBoolean());
        }
        catch (Exception error) { Error(error); }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (player is null || e.Control || e.Alt) return;
        try
        {
            switch (e.KeyCode)
            {
                case Keys.Space: await TogglePauseAsync(); break;
                case Keys.H: case Keys.Left: await player.PauseAsync(true); await player.StepAsync(true); break;
                case Keys.L: case Keys.Right: await player.PauseAsync(true); await player.StepAsync(false); break;
                case Keys.J: case Keys.Down: await player.JumpAsync(-1); break;
                case Keys.K: case Keys.Up: await player.JumpAsync(1); break;
                case Keys.S: await ScreenshotAsync(); break;
                case Keys.D: await SubmitExportAsync(); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        catch (Exception error) { Error(error); }
    }

    private async Task ScreenshotAsync()
    {
        if (player is null || media is null) return;
        try
        {
            var paused = (await player.PropertyAsync("pause")).GetBoolean();
            await player.PauseAsync(true);
            var position = (await player.PropertyAsync("time-pos")).GetDouble();
            var sub = (subtitle.SelectedItem as TrackOption)?.Index;
            var path = OutputNames.Screenshot(media, position, sub);
            if (File.Exists(path)) SetStatus("截图已存在：" + path);
            else
            {
                await player.ScreenshotAsync(path, sub is not null);
                if (!File.Exists(path)) throw new InvalidOperationException("截图没有写入文件。");
                SetStatus("截图已保存：" + path);
            }
            if (!paused) await player.PauseAsync(false);
        }
        catch (Exception error) { Error(error); }
    }

    private async Task SubmitExportAsync()
    {
        if (media is null) return;
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
            if (File.Exists(output)) { SetStatus("片段已完成：" + output); return; }
            if (activePaths.Contains(output)) { SetStatus("任务已在排队或运行：" + output); return; }
            var copySeek = doCopy ? Math.Max(0, (keyframes?.FindLast(frame => frame.Pts <= start + 0.0005)?.Dts ?? start) - 0.005) : (double?)null;
            var spec = new ExportSpec(media, start, range.End, audioTrack, burn ? sub : null, doCopy, burn, output, copySeek);
            var job = new Job(spec, new CancellationTokenSource());
            waiting.Enqueue(job);
            activePaths.Add(output);
            jobs.Items.Add("排队  " + Path.GetFileName(output));
            PumpQueue();
        }
        catch (Exception error) { Error(error); }
        await Task.CompletedTask;
    }

    private void PumpQueue()
    {
        while (runningJobs.Count < (int)parallel.Value && waiting.Count > 0)
        {
            var job = waiting.Dequeue();
            if (job.Cancellation.IsCancellationRequested)
            {
                jobs.Items.Add("取消  " + Path.GetFileName(job.Spec.OutputPath));
                activePaths.Remove(job.Spec.OutputPath);
                job.Cancellation.Dispose();
                continue;
            }
            runningJobs.Add(job);
            jobs.Items.Add("运行  " + Path.GetFileName(job.Spec.OutputPath));
            _ = RunJobAsync(job);
        }
    }

    private void CancelSelectedJob()
    {
        if (jobs.SelectedItem is not string selected) return;
        var name = selected.StartsWith("排队  ", StringComparison.Ordinal) ? selected[4..] :
            selected.StartsWith("运行  ", StringComparison.Ordinal) ? selected[4..] : null;
        if (name is null) return;
        var job = runningJobs.Concat(waiting).FirstOrDefault(item =>
            string.Equals(Path.GetFileName(item.Spec.OutputPath), name, StringComparison.OrdinalIgnoreCase));
        if (job is null) return;
        job.Cancellation.Cancel();
        SetStatus("已请求取消：" + name);
        PumpQueue();
    }

    private async Task RunJobAsync(Job job)
    {
        var name = Path.GetFileName(job.Spec.OutputPath);
        try
        {
            var output = await Exporter.RunAsync(job.Spec, job.Cancellation.Token);
            jobs.Items.Add("完成  " + name);
            SetStatus($"片段已保存（实际时长 {output.Duration:F3} 秒）：{job.Spec.OutputPath}");
        }
        catch (OperationCanceledException) { jobs.Items.Add("取消  " + name); }
        catch (Exception error) { jobs.Items.Add("失败  " + name + " — " + error.Message); SetStatus(error.Message); }
        finally
        {
            job.Cancellation.Dispose();
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
            player = await MpvClient.StartAsync(video.Handle);
            player.PositionChanged += position => {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() => { lastPosition = position; range.Position = position; UpdatePosition(); });
            };
            await player.LoadAsync(media.Path);
            await WaitForTracksAsync();
            await SelectTrackAsync("aid", audio);
            await SelectTrackAsync("sid", subtitle);
            await player.SeekAsync(lastPosition);
            await player.PauseAsync(true);
        }
        catch (Exception error) { Error(error); }
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        Hide();
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
}
