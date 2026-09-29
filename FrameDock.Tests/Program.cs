using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using FrameDock;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new MainForm();
        form.Shown += async (_, _) => {
            try
            {
                TestTimeline(Field<RangeBar>(form, "range"));
                await TestWorkflowAsync(form);
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
        await WaitUntilAsync(() => Field<ListBox>(form, "jobs").Items.Cast<object>().Any(j => j.ToString()!.StartsWith("完成")), 15000);
        var spec = (ExportSpec)Field<ListBox>(form, "jobs").Items[0].GetType().GetProperty("Spec")!.GetValue(Field<ListBox>(form, "jobs").Items[0])!;
        var bundled = OutputNames.ClipCover(spec.OutputPath, media, coverPosition, null);
        var output = await Probe.ReadAsync(spec.OutputPath);
        Check(File.Exists(bundled) && SHA256.HashData(File.ReadAllBytes(bundled)).SequenceEqual(SHA256.HashData(png)), "Shift+D exports the selected cover alongside clip");
        Check(Near(output.Duration, 5.5, 0.15), "trimmed output duration matches selection");
        await CallAsync(form, "ScreenshotAsync", true);
        var currentPosition = (await player.PropertyAsync("time-pos")).GetDouble();
        var currentPath = OutputNames.Screenshot(media, currentPosition, null);
        Check(File.Exists(currentPath) && !SHA256.HashData(File.ReadAllBytes(currentPath)).SequenceEqual(SHA256.HashData(png)), "Shift+S captures current frame without replacing locked cover");
        var seeks = new[] { CallAsync(form, "SeekAsync", 30d), CallAsync(form, "SeekAsync", 45d), CallAsync(form, "SeekAsync", 60d) };
        await Task.WhenAll(seeks);
        Check(Near((await player.PropertyAsync("time-pos")).GetDouble(), 60, 0.04), "rapid seeks settle on the final requested position");
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
        Check(AllControls(form).OfType<Button>().All(button => button.Parent!.ClientRectangle.Contains(button.Bounds)), "all buttons fit at minimum window size");
        using var screenshot = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, form.Size));
        screenshot.Save(Path.Combine(scratch, "minimum-window.png"));
        await CallAsync(form, "OpenVideoAsync", source);
        Check(Field<object?>(form, "selectedCover") is null && Field<PictureBox>(form, "coverPreview").Image is null, "opening another video clears the selected cover");
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
