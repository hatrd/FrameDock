using FrameDock;
using System.Text.Json;

var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/timeline-regression");
Directory.CreateDirectory(root);
var run = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(run);
var failures = new List<string>();
var results = new List<object>();
var checks = new List<object>();

async Task Ffmpeg(params string[] arguments)
{
    using var process = MediaProcess.Start("ffmpeg", new[] { "-hide_banner", "-nostdin", "-v", "error", "-n" }.Concat(arguments));
    var errors = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    await output;
    if (process.ExitCode != 0) throw new Exception(await errors);
}

async Task Case(string name, Func<Task> action)
{
    try { await action(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { checks.Add(new { name, passed = false }); failures.Add(name); Console.WriteLine("FAIL " + name + ": " + error); }
}

async Task Reject(ExportSpec spec, string path)
{
    try { await Exporter.VerifyAsync(spec, path, default); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Invalid output was accepted.");
}

async Task Export(string source, string name, int keyIndex, double end, bool precise = false,
    double preciseStart = 0, bool silent = false, bool burn = false)
{
    var media = await Probe.ReadAsync(source);
    var keys = await Probe.ReadKeyframesAsync(source, default);
    var key = keys[keyIndex];
    var start = precise ? preciseStart : key.Position;
    var audio = silent || media.Audio.Count == 0 ? (int?)null : media.Audio[0].Index;
    var spec = new ExportSpec(media, start, end, audio, burn ? media.Subtitles[0].Index : null,
        !precise, burn, Path.Combine(run, name + Path.GetExtension(source)), precise ? null : key,
        SourceStamp.Capture(source));
    var output = await Exporter.RunAsync(spec, default);
    await Exporter.RunAsync(spec, default); // Reuse must pass exactly the same verification.
    results.Add(new { name, source, start, end, output.Duration, output.Verified });
    Console.WriteLine($"  {start:F6}–{end:F6}, PTS={key.Pts:F6}, DTS={key.Dts:F6}, origin={media.TimelineOrigin:F6}");
}

if (args.Contains("--reported-cut"))
{
    await Case("reported precise boundary with normalized audio", async () => {
        var media = await Probe.ReadAsync(args[1]);
        var spec = new ExportSpec(media, 805.984, 815 + 1d / 3, media.Audio[0].Index, null,
            false, false, Path.Combine(run, "reported-cut.mp4"), NormalizeAudio: true);
        var output = await Exporter.RunAsync(spec, default);
        await Exporter.RunAsync(spec, default);
        if (output.Verified!.VideoFrames != 280) throw new Exception("Expected 280 selected frames.");
    });
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}
var fixture = Path.Combine(run, "long-gop.mp4");
await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=160x96:rate=30:duration=18",
    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=18",
    "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=18",
    "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-g", "250",
    "-keyint_min", "250", "-sc_threshold", "0", "-bf", "3", "-c:a", "aac", fixture);
var mkv = Path.Combine(run, "long-gop.mkv");
await Ffmpeg("-i", fixture, "-map", "0", "-c", "copy", mkv);
var offset = Path.Combine(run, "offset.mp4");
await Ffmpeg("-i", fixture, "-map", "0", "-c", "copy", "-output_ts_offset", "5", offset);
var offsetMkv = Path.Combine(run, "offset.mkv");
await Ffmpeg("-i", fixture, "-map", "0", "-c", "copy", "-output_ts_offset", "5", offsetMkv);
foreach (var source in new[] { fixture, offsetMkv })
{
    await Case("normalized audio " + Path.GetFileName(source), async () => {
        var media = await Probe.ReadAsync(source);
        var normalized = new ExportSpec(media, 6.217, 10.4, media.Audio[1].Index, null,
            false, false, Path.Combine(run, "normalized-" + Path.GetFileName(source)),
            Source: SourceStamp.Capture(source), NormalizeAudio: true);
        var output = await Exporter.RunAsync(normalized, default);
        await Exporter.RunAsync(normalized, default);
        if (output.Audio.Single().Codec != "aac") throw new Exception("Normalized audio must be AAC.");
        using var measurement = MediaProcess.Start("ffmpeg", ["-hide_banner", "-nostdin", "-i", output.Path,
            "-vn", "-af", Loudness.Target + ":print_format=json", "-f", "null", "-"]);
        var stderr = measurement.StandardError.ReadToEndAsync();
        var stdout = measurement.StandardOutput.ReadToEndAsync();
        await measurement.WaitForExitAsync();
        var report = await stderr;
        await stdout;
        if (measurement.ExitCode != 0) throw new Exception(report);
        using var json = JsonDocument.Parse(report[report.LastIndexOf('{')..(report.LastIndexOf('}') + 1)]);
        var loudness = double.Parse(json.RootElement.GetProperty("input_i").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var peak = double.Parse(json.RootElement.GetProperty("input_tp").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(loudness + 14) > 0.5 || peak > -0.5)
            throw new Exception($"Unexpected output loudness/peak: {loudness} LUFS / {peak} dBTP.");
    });
}
await Case("coarse time base precise frame boundary", async () => {
    var coarseTimeBase = Path.Combine(run, "time-base-30.mp4");
    await Ffmpeg("-i", fixture, "-map", "0", "-c", "copy", "-video_track_timescale", "30", coarseTimeBase);
    var media = await Probe.ReadAsync(coarseTimeBase);
    foreach (var normalize in new[] { false, true })
    {
        var spec = new ExportSpec(media, 6.217, 10 + 1d / 3, media.Audio[0].Index, null,
            false, false, Path.Combine(run, $"boundary-{normalize}.mp4"), NormalizeAudio: normalize);
        var output = await Exporter.RunAsync(spec, default);
        await Exporter.RunAsync(spec, default);
        if (output.Verified!.VideoFrames != 123) throw new Exception("Expected 123 selected frames.");
    }
});
if (args.Contains("--loudness"))
{
    await Case("peak-limited normalization preserves the waveform", async () => {
        var transient = Path.Combine(run, "transient.mkv");
        // A quiet tone with one loud transient cannot reach -14 LUFS without compression.
        await Ffmpeg("-i", fixture, "-f", "lavfi", "-i",
            "aevalsrc='0.01*sin(2*PI*440*t)+if(between(t,2,2.01),0.7*sin(2*PI*880*t),0)':s=48000:d=5",
            "-t", "5", "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "pcm_f32le", transient);
        var media = await Probe.ReadAsync(transient);
        var spec = new ExportSpec(media, 0.217, 4.4, media.Audio[0].Index, null, false, false,
            Path.Combine(run, "normalized-transient.mkv"), NormalizeAudio: true);
        var filter = await Loudness.MeasureAsync(spec, default);
        var originalPcm = Path.Combine(run, "transient-original.f32");
        var normalizedPcm = Path.Combine(run, "transient-normalized.f32");
        async Task<float[]> Samples(string path, string processing)
        {
            await Ffmpeg("-ss", MediaProcess.Time(spec.Start), "-i", transient, "-map", "0:a:0",
                "-af", $"atrim=duration={MediaProcess.Time(spec.End - spec.Start)},{processing}",
                "-f", "f32le", path);
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(await File.ReadAllBytesAsync(path)).ToArray();
        }
        var original = await Samples(originalPcm, "anull");
        var normalized = await Samples(normalizedPcm, filter);
        if (original.Length != normalized.Length) throw new Exception("Normalization changed sample count.");
        double energy = 0, product = 0;
        for (var i = 0; i < original.Length; i++)
        {
            energy += (double)original[i] * original[i];
            product += (double)original[i] * normalized[i];
        }
        var gain = product / energy;
        double residual = 0;
        for (var i = 0; i < original.Length; i++) residual += Math.Pow(normalized[i] - gain * original[i], 2);
        if (gain <= 1 || residual / (energy * gain * gain) > 1e-12)
            throw new Exception($"Transients were reshaped: gain={gain}, residual={residual / (energy * gain * gain)}.");
        if (normalized.Max(x => Math.Abs(x)) > Math.Pow(10, Loudness.ProcessingPeak / 20) + 0.001)
            throw new Exception("Normalized transient exceeds processing peak ceiling.");
        var output = await Exporter.RunAsync(spec, default);
        using var measurement = MediaProcess.Start("ffmpeg", ["-hide_banner", "-nostdin", "-i", output.Path,
            "-vn", "-af", Loudness.Target + ":print_format=json", "-f", "null", "-"]);
        var stderr = measurement.StandardError.ReadToEndAsync();
        var stdout = measurement.StandardOutput.ReadToEndAsync();
        await measurement.WaitForExitAsync();
        var report = await stderr;
        await stdout;
        if (measurement.ExitCode != 0) throw new Exception(report);
        using var json = JsonDocument.Parse(report[report.LastIndexOf('{')..(report.LastIndexOf('}') + 1)]);
        var peak = double.Parse(json.RootElement.GetProperty("input_tp").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var integrated = double.Parse(json.RootElement.GetProperty("input_i").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        if (peak > -1 || integrated > -15)
            throw new Exception($"Peak-limited export should remain quieter, with AAC headroom: {integrated} LUFS / {peak} dBTP.");
    });
    await Case("normalization can attenuate an already loud source", async () => {
        var pcm = Path.Combine(run, "attenuated.f32");
        await Ffmpeg("-f", "lavfi", "-i", "aevalsrc=0.9*sin(2*PI*1000*t):s=48000:d=1",
            "-af", Loudness.GainFilter(-4, -0.9), "-f", "f32le", pcm);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(await File.ReadAllBytesAsync(pcm)).ToArray();
        if (Math.Abs(samples.Max(x => Math.Abs(x)) - 0.9 * Math.Pow(10, -10d / 20)) > 0.000001)
            throw new Exception("Already loud audio was not attenuated by 10 dB.");
    });
    await Case("normalized silence remains silent", async () => {
        var silentAudio = Path.Combine(run, "silent-audio.mp4");
        await Ffmpeg("-i", fixture, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", "3", "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", silentAudio);
        var media = await Probe.ReadAsync(silentAudio);
        var spec = new ExportSpec(media, 0.217, 2.4, media.Audio[0].Index, null, false, false,
            Path.Combine(run, "normalized-silence.mp4"), NormalizeAudio: true);
        if (await Loudness.MeasureAsync(spec, default) != "anull") throw new Exception("Silence should bypass gain.");
        await Exporter.RunAsync(spec, default);
    });
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}
var hevc = Path.Combine(run, "hevc.mp4");
await Ffmpeg("-i", fixture, "-t", "10", "-map", "0:v", "-map", "0:a:0", "-c:v", "libx265",
    "-x265-params", "keyint=90:min-keyint=90:scenecut=0:bframes=4:log-level=error", "-c:a", "copy", hevc);
var hevcMkv = Path.Combine(run, "hevc.mkv");
await Ffmpeg("-i", hevc, "-map", "0", "-c", "copy", hevcMkv);
var silent = Path.Combine(run, "silent.mkv");
await Ffmpeg("-i", fixture, "-map", "0:v", "-c", "copy", silent);
var vfr = Path.Combine(run, "vfr.mp4");
await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=160x96:rate=30:duration=8", "-vf",
    "select='if(lt(t,3),not(mod(n,2)),not(mod(n,3)))'", "-fps_mode", "vfr", "-c:v", "libx264",
    "-g", "45", "-sc_threshold", "0", "-bf", "3", vfr);
var captions = Path.Combine(run, "caption.srt");
await File.WriteAllTextAsync(captions, "1\n00:00:01,000 --> 00:00:04,000\nTimeline regression\n");
var subtitleSample = Path.Combine(run, "a,b;[c]' 中文.mkv");
await Ffmpeg("-i", fixture, "-i", captions, "-map", "0:v", "-map", "0:a:0", "-map", "1:s", "-c", "copy", subtitleSample);
var offsetSubtitles = Path.Combine(run, "offset-subtitles.mkv");
await Ffmpeg("-i", subtitleSample, "-map", "0", "-c", "copy", "-output_ts_offset", "5", offsetSubtitles);

foreach (var source in new[] { fixture, mkv, offset, offsetMkv, silent, vfr, hevc, hevcMkv })
{
    var label = Path.GetFileName(source);
    var media = await Probe.ReadAsync(source);
    var keys = await Probe.ReadKeyframesAsync(source, default);
    if (source == fixture && keys[0].Dts >= 0) throw new Exception("Fixture must cover negative initial DTS.");
    await Case(label + " first GOP", () => Export(source, label + "-zero", 0, Math.Min(12.841, media.Duration)));
    await Case(label + " middle GOP", () => Export(source, label + "-middle", 1, Math.Min(keys[1].Position + 2.5, media.Duration)));
    await Case(label + " mute", () => Export(source, label + "-mute", 0, 2, silent: true));
    await Case(label + " short", () => Export(source, label + "-short", 0, keys[0].Position + 0.05));
    await Case(label + " precise", () => Export(source, label + "-precise", 0, 3.4, precise: true, preciseStart: 1.217));
    await Case(label + " precise with input seek", () => Export(source, label + "-precise-middle", 0,
        Math.Min(7.6, media.Duration), precise: true, preciseStart: 6.217));
    await Case(label + " mpv position and seek", async () => {
        await using var player = await MpvClient.StartAsync(IntPtr.Zero, headless: true);
        var position = double.NaN;
        player.PositionChanged += value => position = value;
        await player.LoadAsync(source, media.TimelineOrigin);
        for (var attempt = 0; attempt < 100 && !double.IsFinite(position); attempt++) await Task.Delay(50);
        if (!double.IsFinite(position)) throw new Exception("Preview did not report its first frame.");
        if (Math.Abs(position - keys[0].Position) > 0.05) throw new Exception($"Preview first frame {position} differs from key {keys[0].Position}.");
        await player.SeekAsync(keys[1].Position);
        for (var attempt = 0; attempt < 100 && Math.Abs(position - keys[1].Position) > 0.05; attempt++) await Task.Delay(50);
        if (Math.Abs(position - keys[1].Position) > 0.05) throw new Exception($"Preview seek {position} differs from key {keys[1].Position}.");
        if (!(await player.PropertyAsync("pause")).GetBoolean()) throw new Exception("Preview started playing without a request.");
        await player.SetRangeLoopAsync(1, 1.6);
        await player.SeekAsync(1);
        await player.PauseAsync(false);
        var wraps = 0;
        var previous = 1.0;
        for (var attempt = 0; attempt < 80 && wraps < 2; attempt++)
        {
            await Task.Delay(50);
            var current = (await player.PropertyAsync("time-pos")).GetDouble();
            if (current < 0.95 || current > 1.65) throw new Exception($"Loop escaped selection: {current}.");
            if (current < previous - 0.2) wraps++;
            previous = current;
        }
        if (wraps < 2) throw new Exception("Selection did not loop twice.");
        await player.SetRangeLoopAsync(2, 2.6);
        await player.SeekAsync(2);
        await Task.Delay(900);
        var updated = (await player.PropertyAsync("time-pos")).GetDouble();
        if (updated < 1.95 || updated > 2.65) throw new Exception($"Updated loop escaped selection: {updated}.");
        await player.PauseAsync(true);
        await player.SetRangeLoopAsync(null, null);
        await player.SeekAsync(2.5);
        await player.PauseAsync(false);
        await Task.Delay(400);
        if ((await player.PropertyAsync("time-pos")).GetDouble() <= 2.6)
            throw new Exception("Playback still loops after disabling selection loop.");
        await player.PauseAsync(true);
    });
}

var info = await Probe.ReadAsync(fixture);
var spec = new ExportSpec(info, 0, 12.841, info.Audio[0].Index, null, true, false, Path.Combine(run, "broken.mp4"));
await Ffmpeg("-i", fixture, "-ss", "0", "-t", "12.841", "-map", "0:v:0", "-map", "0:1", "-c", "copy", spec.OutputPath);
await Case("missing initial GOP rejected", () => Reject(spec, spec.OutputPath));
await Case("existing invalid file preserved", async () => {
    var stamp = SourceStamp.Capture(spec.OutputPath);
    try { await Exporter.RunAsync(spec, default); }
    catch (InvalidOperationException) {
        if (SourceStamp.Capture(spec.OutputPath) != stamp) throw new Exception("Existing file was changed.");
        return;
    }
    throw new Exception("Existing invalid output was accepted.");
});
var wrongAudio = Path.Combine(run, "wrong-audio.mp4");
await Ffmpeg("-i", fixture, "-t", "12.841", "-map", "0:v:0", "-map", "0:2", "-c", "copy", wrongAudio);
await Case("wrong audio of same codec rejected", () => Reject(spec, wrongAudio));
var late = Path.Combine(run, "late.mp4");
await Ffmpeg("-i", fixture, "-t", "12.841", "-map", "0:v:0", "-map", "0:1", "-c", "copy", "-output_ts_offset", "1", late);
await Case("shifted timeline rejected", () => Reject(spec, late));

var real = args.Length > 1 ? args[1] : @"C:\Users\R\Videos\2026-09-25 09-40-23.mp4";
if (File.Exists(real))
{
    await Case("user MP4 zero", () => Export(real, "real-zero", 0, 12.841));
    await Case("user MP4 middle", () => Export(real, "real-middle", 1, 12.841));
    await Case("user MP4 precise", () => Export(real, "real-precise", 0, 3.4, precise: true, preciseStart: 1.217));
    var broken = @"C:\Users\R\Videos\2026-09-25 09-40-23__000000_000-000012_841__a1_suboff_copy_33dac5c8.mp4";
    if (File.Exists(broken))
    {
        var media = await Probe.ReadAsync(real);
        await Case("user existing bad output rejected", () => Reject(new ExportSpec(media, 0, 12.841,
            media.Audio[0].Index, null, true, false, broken), broken));
    }
}
else Console.WriteLine("SKIP user MP4 (not found): " + real);

await Case("subtitle burn / escaped filename", () => Export(subtitleSample, "subtitle-burn", 0, 3.4,
    precise: true, preciseStart: 1.2, burn: true));
await Case("subtitle burn / nonzero origin", () => Export(offsetSubtitles, "offset-subtitle-burn", 0, 3.4,
    precise: true, preciseStart: 1.2, burn: true));
await Case("cancelled export leaves no output", async () => {
    using var cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(50);
    var cancelled = spec with { Copy = false, OutputPath = Path.Combine(run, "cancelled.mp4") };
    try { await Exporter.RunAsync(cancelled, cancellation.Token); }
    catch (OperationCanceledException) {
        if (File.Exists(cancelled.OutputPath) || Directory.GetFiles(run, ".cancelled.partial-*").Length > 0)
            throw new Exception("Cancelled task left an output or partial file.");
        return;
    }
    throw new Exception("Cancelled task succeeded.");
});
await File.WriteAllTextAsync(Path.Combine(run, "results.json"), JsonSerializer.Serialize(new { checks, failures, results },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Artifacts: " + run);
Console.WriteLine($"Failures: {failures.Count}");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
