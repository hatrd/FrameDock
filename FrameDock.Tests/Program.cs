using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FrameDock;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var settingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../.scratch/window-tests", Guid.NewGuid().ToString("N"), "settings.json"));
        using var form = new MainForm(settingsPath: settingsPath);
        form.Shown += async (_, _) => {
            try
            {
                if (args.Contains("--tray-memory"))
                    await TestTrayMemoryAsync(form);
                else
                {
                    TestExportQueueMenu(form);
                    await TestWindowSettingsAsync(form, settingsPath);
                    TestVisualLayout(form);
                    if (!args.Contains("--window-layout"))
                    {
                        TestTimeline(Field<RangeBar>(form, "range"));
                        await TestWorkflowAsync(form);
                        await TestTrayMemoryAsync(form);
                    }
                }
                Console.WriteLine($"PASS: {checks} checks");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }
            finally
            {
                if (Field<MpvClient?>(form, "player") is { } player) await player.DisposeAsync();
                SetField(form, "player", null);
                SetField(form, "exiting", true);
                form.Close();
            }
        };
        Application.Run(form);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static Task CallAsync(object target, string name, params object[] args) => (Task)Call(target, name, args)!;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
    private static bool Near(double actual, double expected, double tolerance = 0.002) => Math.Abs(actual - expected) < tolerance;

    private static void TestExportQueueMenu(MainForm form)
    {
        var scratch = Path.GetFullPath(Path.Combine(".scratch/export-menu", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        var output = Path.Combine(scratch, "导出 文件, & 片段.mp4");
        File.WriteAllText(output, "fixture");
        var media = new MediaInfo(Path.Combine(scratch, "source.mp4"), 10, "h264", [], []);
        var jobType = typeof(MainForm).GetNestedType("Job", BindingFlags.NonPublic)!;
        object Job(string path, string state)
        {
            var job = Activator.CreateInstance(jobType,
                new object?[] { new ExportSpec(media, 0, 1, null, null, false, false, path), null })!;
            jobType.GetProperty("State")!.SetValue(job, state);
            return job;
        }
        var queue = Field<ListBox>(form, "jobs");
        var menu = queue.ContextMenuStrip!;
        void RightClick(Point point) => typeof(Control).GetMethod("OnMouseDown", Private)!
            .Invoke(queue, [new MouseEventArgs(MouseButtons.Right, 1, point.X, point.Y, 0)]);
        System.ComponentModel.CancelEventArgs OpenMenu()
        {
            var args = new System.ComponentModel.CancelEventArgs();
            typeof(ToolStripDropDown).GetMethod("OnOpening", Private)!.Invoke(menu, [args]);
            return args;
        }
        queue.Items.Add(Job(Path.Combine(scratch, "pending.mp4"), "排队"));
        queue.Items.Add(Job(output, "完成"));
        try
        {
            queue.SelectedIndex = 0;
            var row = queue.GetItemRectangle(1);
            RightClick(new Point(row.Left + 4, row.Top + row.Height / 2));
            Check(queue.SelectedIndex == 1, "right click targets the clicked export instead of the old selection");
            Check(!OpenMenu().Cancel && menu.Items[0].Enabled && !menu.Items[1].Enabled,
                "completed exports can be revealed and cannot be cancelled");
            var start = MainForm.CreateExplorerSelection(output);
            Check(start.FileName == "explorer.exe" && start.UseShellExecute &&
                start.Arguments == $"/select,\"{output}\"",
                "Explorer selects the output file with spaces, Unicode and commas quoted");
            queue.SelectedIndex = 0;
            Check(!OpenMenu().Cancel && !menu.Items[0].Enabled && menu.Items[1].Enabled,
                "pending exports without a final file can only be cancelled");
            queue.SelectedIndex = 1;
            jobType.GetProperty("State")!.SetValue(queue.SelectedItem, "失败");
            Check(!OpenMenu().Cancel && menu.Items[0].Enabled,
                "a video remains accessible if a later cover export fails");
            File.Delete(output);
            Check(!OpenMenu().Cancel && !menu.Items[0].Enabled, "moved or deleted outputs cannot be revealed");
            Call(form, "RevealSelectedJob");
            Check(Field<Label>(form, "status").Text.Contains("请重新导出"),
                "a file removed after opening the menu reports a recovery action");
            RightClick(new Point(4, queue.ClientSize.Height - 1));
            Check(queue.SelectedIndex == -1 && OpenMenu().Cancel,
                "right clicking empty queue space does not act on a previous export");
        }
        finally
        {
            foreach (var job in queue.Items.Cast<object>())
                ((CancellationTokenSource)jobType.GetProperty("Cancellation")!.GetValue(job)!).Dispose();
            queue.Items.Clear();
        }
    }

    private static void TestVisualLayout(MainForm form)
    {
        var scratch = Path.GetFullPath(".scratch/design-review");
        Directory.CreateDirectory(scratch);
        foreach (var size in new[] { form.MinimumSize, new Size(1220, 860) })
        {
            form.Size = size;
            form.PerformLayout();
            Application.DoEvents();
            var clipped = AllControls(form)
                .Where(control => control is Button or CheckBox or ComboBox or NumericUpDown)
                .Where(control => !control.Parent!.ClientRectangle.Contains(control.Bounds))
                .Select(control => $"{control.Text}: {control.Bounds} outside {control.Parent!.ClientRectangle}")
                .ToArray();
            Check(clipped.Length == 0, $"interactive controls fit at {size.Width}x{size.Height}: {string.Join("; ", clipped)}");
            var queue = Field<ListBox>(form, "jobs");
            Check(queue.ClientSize.Height >= queue.ItemHeight, "minimum queue area shows a complete task row");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(scratch, $"workspace-{size.Width}x{size.Height}.png"));
        }
        using var help = new ShortcutHelpForm();
        help.Show(form);
        help.PerformLayout();
        using var helpBitmap = new Bitmap(help.Width, help.Height);
        help.DrawToBitmap(helpBitmap, new Rectangle(Point.Empty, help.Size));
        helpBitmap.Save(Path.Combine(scratch, "shortcuts.png"));
        Check(AllControls(help).OfType<Button>().All(button => button.Parent!.ClientRectangle.Contains(button.Bounds)),
            "shortcut help close button remains accessible");
        help.Close();
    }

    private static async Task TestWindowSettingsAsync(MainForm form, string settingsPath)
    {
        var video = Field<Panel>(form, "video");
        Check(Math.Abs(video.Height - video.Width * 9d / 16) <= 1,
            "first launch sizes the preview to 16:9");
        Check(Screen.FromControl(form).WorkingArea.Contains(form.Bounds), "default window fits the screen");
        form.Bounds = new Rectangle(90, 80, 980, 900);
        Call(form, "OnResizeEnd", EventArgs.Empty);
        Field<NumericUpDown>(form, "parallel").Value = 3;
        Field<CheckBox>(form, "normalizeAudio").Checked = true;
        using (var restored = new MainForm(settingsPath: settingsPath))
        {
            restored.Show();
            Check(Field<CheckBox>(restored, "normalizeAudio").Checked,
                "loudness normalization survives restart alongside window settings");
            Check(restored.Bounds == form.Bounds && Field<NumericUpDown>(restored, "parallel").Value == 3,
                "resizing persists size and position alongside existing settings");
            SetField(restored, "exiting", true);
            restored.Close();
        }

        Field<CheckBox>(form, "normalizeAudio").Checked = false;
        using (var restored = new MainForm(settingsPath: settingsPath))
        {
            Check(!Field<CheckBox>(restored, "normalizeAudio").Checked,
                "turning loudness normalization off also survives restart");
            SetField(restored, "exiting", true);
        }

        var normalBounds = form.Bounds;
        form.WindowState = FormWindowState.Maximized;
        Application.DoEvents();
        form.Close();
        Check(!form.Visible, "closing still hides the window in the tray");
        await CallAsync(form, "ShowFromTrayAsync");
        Check(form.WindowState == FormWindowState.Maximized, "tray reopening preserves maximized state");
        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        Call(form, "SaveWindowSettings");
        using (var restored = new MainForm(settingsPath: settingsPath))
        {
            restored.Show();
            Application.DoEvents();
            Check(restored.WindowState == FormWindowState.Maximized,
                "restarting after minimization restores the last visible state");
            restored.WindowState = FormWindowState.Normal;
            Application.DoEvents();
            Check(restored.Bounds == normalBounds, "maximization does not overwrite the user-resized bounds");
            SetField(restored, "exiting", true);
            restored.Close();
        }
        form.WindowState = FormWindowState.Normal;
        form.Bounds = new Rectangle(120, 100, 1000, 920);
        form.Close();
        using (var restored = new MainForm(settingsPath: settingsPath))
        {
            restored.Show();
            Check(restored.Bounds == form.Bounds, "closing to tray also saves the latest bounds");
            SetField(restored, "exiting", true);
            restored.Close();
        }
        await CallAsync(form, "ShowFromTrayAsync");

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new {
            maxParallel = 2, window = new { x = -100000, y = -100000, width = 5000, height = 4000, maximized = false }
        }));
        using (var restored = new MainForm(settingsPath: settingsPath))
        {
            restored.Show();
            Check(Screen.FromControl(restored).WorkingArea.Contains(restored.Bounds),
                "saved bounds from a removed screen are clamped to an available screen");
            SetField(restored, "exiting", true);
            restored.Close();
        }
        File.WriteAllText(settingsPath, "{\"maxParallel\":4}");
        using (var restored = new MainForm(settingsPath: settingsPath))
        {
            restored.Show();
            var preview = Field<Panel>(restored, "video");
            Check(Field<NumericUpDown>(restored, "parallel").Value == 4 &&
                Math.Abs(preview.Height - preview.Width * 9d / 16) <= 1,
                "legacy settings retain parallelism and use the new 16:9 default");
            SetField(restored, "exiting", true);
            restored.Close();
        }
        Call(form, "SaveWindowSettings");
    }
    private static int X(RangeBar bar, double time) => 18 + (int)Math.Round((bar.Width - 36) * (time - bar.ViewStart) / bar.ViewLength);
    private static void Mouse(RangeBar bar, string method, int x, int y, MouseButtons button = MouseButtons.Left, int delta = 0)
        => Call(bar, method, new MouseEventArgs(button, 1, x, y, delta));

    private static void TestTimeline(RangeBar bar)
    {
        bar.Duration = 120;
        var seeks = 0;
        bar.SeekRequested += CountSeek;
        void CountSeek(double _) => seeks++;
        // Click inside the grip, rather than exactly on the line. A click must not move the boundary.
        Mouse(bar, "OnMouseDown", X(bar, 0) + 8, 28);
        Check(Near(bar.Start, 0), "clicking inside start grip does not jump");
        Mouse(bar, "OnMouseMove", X(bar, 12) + 8, 28);
        Mouse(bar, "OnMouseUp", X(bar, 12) + 8, 28);
        Check(Near(bar.Start, 12, 0.15) && seeks == 0, "left boundary drags when overlapping the playhead");
        bar.Position = bar.End;
        Mouse(bar, "OnMouseDown", X(bar, 120) - 8, 28);
        Mouse(bar, "OnMouseMove", X(bar, 105) - 8, 28);
        Mouse(bar, "OnMouseUp", X(bar, 105) - 8, 28);
        Check(Near(bar.End, 105, 0.15) && seeks == 0, "right boundary drags when overlapping the playhead");
        Mouse(bar, "OnMouseDown", X(bar, 50), 55);
        Mouse(bar, "OnMouseUp", X(bar, 50), 55);
        Check(Near(bar.Position, 50, 0.15) && seeks == 1, "lower playhead zone seeks independently");
        var anchorX = X(bar, 60);
        Mouse(bar, "OnMouseWheel", anchorX, 40, MouseButtons.None, 480);
        var anchor = bar.ViewStart + (anchorX - 18d) / (bar.Width - 36) * bar.ViewLength;
        Check(bar.ViewLength < 32 && Near(anchor, 60, 0.15), "wheel zoom keeps mouse time anchored");
        var oldStart = bar.ViewStart;
        Mouse(bar, "OnMouseDown", 500, 40, MouseButtons.Right);
        Mouse(bar, "OnMouseMove", 400, 40, MouseButtons.Right);
        Mouse(bar, "OnMouseUp", 400, 40, MouseButtons.Right);
        Check(bar.ViewStart > oldStart, "right drag pans the zoomed timeline");
        Mouse(bar, "OnMouseWheel", 400, 40, MouseButtons.None, 12000);
        Check(Near(bar.ViewLength, 0.5), "zoom has a usable minimum span");
        var bounds = (bar.Start, bar.End);
        bar.ResetView();
        Check(Near(bar.ViewLength, 120) && (bar.Start, bar.End) == bounds, "reset view preserves trim bounds");
        bar.Start = 50;
        bar.End = 50.1;
        bar.Position = 50;
        Mouse(bar, "OnMouseDown", X(bar, 50) + 10, 28);
        Mouse(bar, "OnMouseMove", X(bar, 40) + 10, 28);
        Mouse(bar, "OnMouseUp", X(bar, 40) + 10, 28);
        Check(Near(bar.Start, 40, 0.15), "narrow range still exposes the start grip");
        Mouse(bar, "OnMouseDown", X(bar, bar.End) - 10, 28);
        Mouse(bar, "OnMouseMove", X(bar, 60) - 10, 28);
        Mouse(bar, "OnMouseUp", X(bar, 60) - 10, 28);
        Check(Near(bar.End, 60, 0.15), "narrow range still exposes the end grip");
        bar.Start = 100;
        Check(bar.Start < bar.End, "dragging cannot cross the other boundary");
        bar.ResetRange();
        Check(Near(bar.Start, 0) && Near(bar.End, 120), "reset range restores full duration");
        bar.Duration = 0.001;
        bar.Start = 1;
        bar.End = 0;
        using var shortBitmap = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(shortBitmap, bar.ClientRectangle);
        Check(Near(bar.Start, 0) && Near(bar.End, 0.001), "very short media stays valid and renders");
        bar.SeekRequested -= CountSeek;
    }

    private static async Task TestWorkflowAsync(MainForm form)
    {
        var scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.scratch/interaction-test"));
        Directory.CreateDirectory(scratch);
        var source = Path.Combine(scratch, "two-minute.mp4");
        var ffmpeg = ToolPaths.Find("ffmpeg") ?? throw new InvalidOperationException("FFmpeg required for integration checks.");
        using (var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } })
        {
            foreach (var arg in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "120", "-c:v", "libx264", "-preset", "ultrafast", "-g", "150", "-pix_fmt", "yuv420p", source })
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "create two-minute integration video: " + await errors);
        }
        await CallAsync(form, "OpenVideoAsync", source);
        var player = Field<MpvClient?>(form, "player") ?? throw new InvalidOperationException("Preview did not open.");
        var bar = Field<RangeBar>(form, "range");
        var copy = Field<CheckBox>(form, "copy");
        SetField(form, "keyframes", new List<Keyframe> { new(0, 0), new(5, 5), new(10, 10) });
        copy.Checked = true;
        bar.Start = 7.25;
        Check(Near(bar.Start, 7.25) && Near((double)Call(form, "ActualStart")!, 5), "copy mode preserves requested boundary and computes export keyframe separately");
        await CallAsync(form, "SeekAsync", 12.5d);
        await CallAsync(form, "SetBoundaryAsync", true);
        Check(Near(bar.Start, 12.5, 0.04), "[ sets start to actual displayed frame");
        await CallAsync(form, "SeekAsync", 18d);
        await CallAsync(form, "SetBoundaryAsync", false);
        Check(Near(bar.End, 18, 0.04), "] sets end to actual displayed frame");
        await CallAsync(form, "HandleShortcutAsync", Keys.OemOpenBrackets, true);
        Check(Near((await player.PropertyAsync("time-pos")).GetDouble(), 12.5, 0.04) &&
            Near(bar.Start, 12.5, 0.04) && Near(bar.End, 18, 0.04) &&
            (await player.PropertyAsync("pause")).GetBoolean(),
            "Shift+[ jumps to the selected start paused without changing boundaries");
        await CallAsync(form, "HandleShortcutAsync", Keys.OemCloseBrackets, true);
        Check(Near((await player.PropertyAsync("time-pos")).GetDouble(), 18, 0.04) &&
            Near(bar.Start, 12.5, 0.04) && Near(bar.End, 18, 0.04) &&
            (await player.PropertyAsync("pause")).GetBoolean(),
            "Shift+] jumps to the selected end paused without changing boundaries");
        await CallAsync(form, "SeekAsync", 13d);
        await CallAsync(form, "SelectCoverAsync");
        var selected = Field<object>(form, "selectedCover");
        var png = (byte[])selected.GetType().GetProperty("Png")!.GetValue(selected)!;
        var coverPosition = (double)selected.GetType().GetProperty("Position")!.GetValue(selected)!;
        await CallAsync(form, "SeekAsync", 17d);
        Check(Near(coverPosition, 13, 0.04) && ReferenceEquals(selected, Field<object>(form, "selectedCover")), "cover stays frozen after moving the playhead");
        await CallAsync(form, "ScreenshotAsync", false);
        var media = Field<MediaInfo>(form, "media");
        var coverPath = OutputNames.Screenshot(media, coverPosition, null);
        Check(File.Exists(coverPath) && SHA256.HashData(File.ReadAllBytes(coverPath)).SequenceEqual(SHA256.HashData(png)), "S exports frozen full-resolution cover");
        copy.Checked = false;
        await CallAsync(form, "SubmitExportAsync", true);
        await CallAsync(form, "SelectCoverAsync");
        await WaitUntilAsync(() => Field<ListBox>(form, "jobs").Items.Cast<object>().Any(j => j.ToString()!.StartsWith("完成")), 15000);
        var spec = (ExportSpec)Field<ListBox>(form, "jobs").Items[0].GetType().GetProperty("Spec")!.GetValue(Field<ListBox>(form, "jobs").Items[0])!;
        var bundled = OutputNames.ClipCover(spec.OutputPath, media, coverPosition, null);
        var output = await Probe.ReadAsync(spec.OutputPath);
        Check(File.Exists(bundled) && SHA256.HashData(File.ReadAllBytes(bundled)).SequenceEqual(SHA256.HashData(png)), "Shift+D exports submission-time cover even when selection changes during export");
        Check(Near(output.Duration, 5.5, 0.15), "trimmed output duration matches selection");
        await CallAsync(form, "ScreenshotAsync", true);
        var currentPosition = (await player.PropertyAsync("time-pos")).GetDouble();
        var currentPath = OutputNames.Screenshot(media, currentPosition, null);
        Check(File.Exists(currentPath) && !SHA256.HashData(File.ReadAllBytes(currentPath)).SequenceEqual(SHA256.HashData(png)), "Shift+S captures current frame without replacing locked cover");
        // Use an asymmetric frame to verify directions, dimensions and real encoded output.
        await CallAsync(form, "SeekAsync", 17d);
        await CallAsync(form, "ScreenshotAsync", true);
        var originalPosition = (await player.PropertyAsync("time-pos")).GetDouble();
        using var original = new Bitmap(OutputNames.Screenshot(media, originalPosition, null));
        var lockedBeforeRotation = Field<object>(form, "selectedCover");
        foreach (var angle in new[] { 90, 180, 270 })
        {
            await CallAsync(form, "RotateAsync", 90);
            Check(Field<int>(form, "rotation") == angle, $"preview rotates to {angle} degrees");
            await CallAsync(form, "ScreenshotAsync", true);
            using var rotated = new Bitmap(OutputNames.Screenshot(media, originalPosition, null, angle));
            using var expected = new Bitmap(original);
            expected.RotateFlip(angle switch { 90 => RotateFlipType.Rotate90FlipNone,
                180 => RotateFlipType.Rotate180FlipNone, _ => RotateFlipType.Rotate270FlipNone });
            Check(rotated.Size == expected.Size, $"{angle} degree screenshot dimensions");
            var difference = 0d;
            var samples = 0;
            for (var y = 0; y < rotated.Height; y += 7)
                for (var x = 0; x < rotated.Width; x += 7)
                {
                    var a = rotated.GetPixel(x, y); var b = expected.GetPixel(x, y);
                    difference += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                    samples += 3;
                }
            // Chroma resampling can differ when RGB conversion happens before versus after rotation.
            Check(difference / samples < 8, $"{angle} degree screenshot direction matches preview filter (mean error {difference / samples:F2})");
            copy.Checked = true;
            bar.Start = 17; bar.End = 18;
            await CallAsync(form, "SubmitExportAsync", false);
            await WaitUntilAsync(() => Field<ListBox>(form, "jobs").Items.Cast<object>().All(job =>
                (string)job.GetType().GetProperty("State")!.GetValue(job)! is "完成" or "失败"), 30000);
            var rotatedJob = Field<ListBox>(form, "jobs").Items.Cast<object>().Last();
            var rotatedSpec = (ExportSpec)rotatedJob.GetType().GetProperty("Spec")!.GetValue(rotatedJob)!;
            Check(!rotatedSpec.Copy && rotatedSpec.Rotation == angle && File.Exists(rotatedSpec.OutputPath),
                $"{angle} degree video exports and passes decoded frame verification");
            await CheckVideoSizeAsync(rotatedSpec.OutputPath, expected.Width, expected.Height);
            Check(ReferenceEquals(lockedBeforeRotation, Field<object>(form, "selectedCover")), "rotation preserves locked cover");
        }
        await CallAsync(form, "RotateAsync", -90);
        Check(Field<int>(form, "rotation") == 180, "counterclockwise rotation subtracts 90 degrees");
        await CallAsync(form, "RotateAsync", 180);
        Check(Field<int>(form, "rotation") == 0, "rotation wraps to original orientation");
        var seeks = new[] { CallAsync(form, "SeekAsync", 30d), CallAsync(form, "SeekAsync", 45d), CallAsync(form, "SeekAsync", 60d) };
        await Task.WhenAll(seeks);
        Check(Near((await player.PropertyAsync("time-pos")).GetDouble(), 60, 0.04), "rapid seeks settle on the final requested position");
        await CallAsync(form, "HandleShortcutAsync", Keys.Right, false);
        Check(Near((await player.PropertyAsync("time-pos")).GetDouble(), 60 + 1d / 30, 0.005), "right shortcut settles on the next actual frame");
        await CallAsync(form, "HandleShortcutAsync", Keys.Left, false);
        Check(Near((await player.PropertyAsync("time-pos")).GetDouble(), 60, 0.005), "left shortcut settles on the previous actual frame");
        bar.End = 60.4;
        bar.Start = 60;
        await CallAsync(form, "PreviewRangeAsync");
        try { await WaitUntilAsync(() => !Field<bool>(form, "previewingRange"), 5000); }
        catch
        {
            Console.WriteLine($"preview timeout: time={await player.PropertyAsync("time-pos")} pause={await player.PropertyAsync("pause")} last={Field<double>(form, "lastPosition")} loading={Field<bool>(form, "loading")} seeking={!Field<Task>(form, "seekPump").IsCompleted} scrubbing={bar.IsScrubbing}");
            throw;
        }
        await Field<Task>(form, "seekPump");
        Check((await player.PropertyAsync("pause")).GetBoolean() && Near((await player.PropertyAsync("time-pos")).GetDouble(), 60.4, 0.08), "R stops playback at selected end");
        form.Size = form.MinimumSize;
        form.PerformLayout();
        foreach (var button in AllControls(form).OfType<Button>().Where(button => !button.Parent!.ClientRectangle.Contains(button.Bounds)))
            Console.Error.WriteLine($"Clipped button: {button.Text}, bounds={button.Bounds}, container={button.Parent!.ClientRectangle}");
        using var screenshot = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, form.Size));
        screenshot.Save(Path.Combine(scratch, "minimum-window.png"));
        Check(AllControls(form).OfType<Button>().All(button => button.Parent!.ClientRectangle.Contains(button.Bounds)), "all buttons fit at minimum window size");
        await CallAsync(form, "OpenVideoAsync", source);
        Check(Field<object?>(form, "selectedCover") is null && Field<PictureBox>(form, "coverPreview").Image is null, "opening another video clears the selected cover");
    }

    private static async Task TestTrayMemoryAsync(MainForm form)
    {
        var scratch = Path.GetFullPath(".scratch/tray-memory");
        Directory.CreateDirectory(scratch);
        var source = Path.Combine(scratch, "1080p.mp4");
        using (var generator = MediaProcess.Start("ffmpeg", new[] { "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30", "-t", "8",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", source }))
        {
            var errors = generator.StandardError.ReadToEndAsync();
            await generator.WaitForExitAsync();
            Check(generator.ExitCode == 0, "create 1080p memory fixture: " + await errors);
        }
        var samples = new List<object>();
        object Sample(string stage, Process? preview = null)
        {
            using var host = Process.GetCurrentProcess();
            host.Refresh();
            preview?.Refresh();
            var sample = new { stage, hostWorkingSetMiB = host.WorkingSet64 / 1048576d,
                hostPrivateMiB = host.PrivateMemorySize64 / 1048576d,
                previewWorkingSetMiB = preview is null ? 0 : preview.WorkingSet64 / 1048576d,
                previewPrivateMiB = preview is null ? 0 : preview.PrivateMemorySize64 / 1048576d,
                managedMiB = GC.GetTotalMemory(false) / 1048576d };
            samples.Add(sample);
            Console.WriteLine(JsonSerializer.Serialize(sample));
            return sample;
        }
        Sample("before-open");
        await CallAsync(form, "OpenVideoAsync", source);
        await CallAsync(form, "SeekAsync", 3d);
        await CallAsync(form, "SelectCoverAsync");
        var selected = Field<object>(form, "selectedCover");
        var coverBytes = (byte[])selected.GetType().GetProperty("Png")!.GetValue(selected)!;
        using (var stream = new MemoryStream(coverBytes))
        using (var image = Image.FromStream(stream))
            Check(image.Width == 1920 && image.Height == 1080, "locked cover retains original resolution");
        var thumbnail = Field<PictureBox>(form, "coverPreview").Image!;
        Check(thumbnail.Width <= 172 && thumbnail.Height <= 100,
            "cover preview retains a small thumbnail instead of a decoded 1080p bitmap");
        var bar = Field<RangeBar>(form, "range");
        bar.Start = 1;
        bar.End = 5;
        await CallAsync(form, "RotateAsync", 90);
        await CallAsync(form, "ScreenshotAsync", true);
        var media = Field<MediaInfo>(form, "media");
        var rotatedPosition = (await Field<MpvClient>(form, "player").PropertyAsync("time-pos")).GetDouble();
        using (var portrait = new Bitmap(OutputNames.Screenshot(media, rotatedPosition, null, 90)))
            Check(portrait.Width == 1080 && portrait.Height == 1920, "1920x1080 screenshot becomes 1080x1920");
        var portraitSpec = new ExportSpec(media, 1, 2, null, null, false, false,
            Path.Combine(scratch, "portrait-" + Guid.NewGuid().ToString("N") + ".mp4"), Rotation: 90);
        await Exporter.RunAsync(portraitSpec, CancellationToken.None);
        await CheckVideoSizeAsync(portraitSpec.OutputPath, 1080, 1920);
        await CallAsync(form, "RotateAsync", -90);
        double? firstIdlePrivate = null;
        const int cycles = 20;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var client = Field<MpvClient>(form, "player");
            var process = Field<Process>(client, "process");
            using var preview = Process.GetProcessById(process.Id);
            Sample($"visible-{cycle}", preview);
            form.Close();
            await WaitUntilAsync(() => Field<MpvClient?>(form, "player") is null &&
                Field<Task?>(form, "releasingPlayer") is null, 15000);
            preview.Refresh();
            Check(!form.Visible && preview.HasExited, $"tray cycle {cycle}: mpv exits and releases its decoder and buffers");
            Check(Field<Task>(form, "keyframeScanTask").IsCompleted,
                $"tray cycle {cycle}: background source scan has stopped");
            await WaitUntilAsync(() => CanOpenExclusively(source), 3000);
            Check(true, $"tray cycle {cycle}: video file can be opened exclusively");
            Check(ReferenceEquals(selected, Field<object>(form, "selectedCover")) && Near(bar.Start, 1) && Near(bar.End, 5),
                $"tray cycle {cycle}: locked cover and trim boundaries survive");
            await Task.Delay(1800); // Include the application's one-time idle heap cleanup.
            Sample($"tray-{cycle}");
            using (var host = Process.GetCurrentProcess())
            {
                host.Refresh();
                var current = host.PrivateMemorySize64 / 1048576d;
                if (cycle == 1) firstIdlePrivate = current; // Allow first-use JIT/native initialization.
                if (cycle == cycles - 1) Check(current - firstIdlePrivate!.Value < 16,
                    "repeated reopen/hide cycles do not accumulate video-sized private memory");
            }
            if (cycle < cycles - 1)
            {
                await Task.WhenAll(CallAsync(form, "ShowFromTrayAsync"), CallAsync(form, "ShowFromTrayAsync"));
                var restored = Field<MpvClient>(form, "player");
                Check(Near((await restored.PropertyAsync("time-pos")).GetDouble(), 3, 0.04) &&
                    (await restored.PropertyAsync("pause")).GetBoolean(),
                    $"tray cycle {cycle}: concurrent reopen restores the original frame paused");
            }
        }
        // Report retained managed objects separately from normal uncollected allocations.
        // Samples above include the application's normal idle cleanup, with no test-side trimming.
        var retainedManagedMiB = GC.GetTotalMemory(true) / 1048576d;
        Check(retainedManagedMiB < 12, "twenty tray cycles retain only small managed editing state after collection");
        Sample("tray-after-retention-audit");
        // Closing while a video is loading must not create a player after the window hides.
        await CallAsync(form, "ShowFromTrayAsync");
        var open = CallAsync(form, "OpenVideoAsync", source);
        form.Close();
        await open;
        await WaitUntilAsync(() => Field<Task?>(form, "releasingPlayer") is null, 15000);
        Check(!form.Visible && Field<MpvClient?>(form, "player") is null,
            "closing during video loading does not resurrect a hidden player");
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(true, "loading/closing race releases the video file");
        File.WriteAllText(Path.Combine(scratch, "results.json"), JsonSerializer.Serialize(samples,
            new JsonSerializerOptions { WriteIndented = true }));
        await CallAsync(form, "ShowFromTrayAsync");
    }

    private static bool CanOpenExclusively(string path)
    {
        try { using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    private static async Task CheckVideoSizeAsync(string path, int width, int height)
    {
        using var probe = MediaProcess.Start("ffprobe", new[] { "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height", "-of", "json", path });
        var output = probe.StandardOutput.ReadToEndAsync();
        var error = probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();
        using var json = JsonDocument.Parse(await output);
        var stream = json.RootElement.GetProperty("streams")[0];
        Check(probe.ExitCode == 0 && stream.GetProperty("width").GetInt32() == width &&
            stream.GetProperty("height").GetInt32() == height, $"encoded video dimensions are {width}x{height}: {await error}");
    }

    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in AllControls(child)) yield return descendant;
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Integration check timed out.");
            await Task.Delay(40);
        }
    }
}
