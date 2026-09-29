using System.Diagnostics;
using System.Globalization;

namespace FrameDock;

internal static class MediaProcess
{
    public static Process Start(string tool, IEnumerable<string> arguments)
    {
        var exe = ToolPaths.Find(tool) ?? throw new InvalidOperationException($"找不到 {tool}。");
        var process = new Process { StartInfo = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        return process;
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public static CancellationTokenRegistration CancelWith(Process process, CancellationToken cancellation) =>
        cancellation.Register(() => Kill(process));

    public static string Time(double value) => value.ToString("F9", CultureInfo.InvariantCulture);
    public static double? Number(Dictionary<string, string> fields, string name) =>
        double.TryParse(fields.GetValueOrDefault(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) ? value : null;
    public static long? Integer(Dictionary<string, string> fields, string name) =>
        long.TryParse(fields.GetValueOrDefault(name), out var value) ? value : null;

    public static async Task ProbeLinesAsync(string path, string stream, string mode, string entries,
        string? interval, Action<Dictionary<string, string>> receive, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var args = new List<string> { "-v", "error", "-select_streams", stream, mode,
            "-show_entries", entries, "-of", "compact=p=0:nk=0" };
        if (entries.Contains("data_hash")) args.AddRange(["-show_data_hash", "sha256"]);
        if (interval is not null) args.AddRange(["-read_intervals", interval]);
        args.Add(path);
        using var process = Start("ffprobe", args);
        using var registration = CancelWith(process, cancellation);
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                var fields = new Dictionary<string, string>();
                foreach (var part in line.Split('|'))
                {
                    var separator = part.IndexOf('=');
                    if (separator > 0) fields[part[..separator]] = part[(separator + 1)..];
                }
                receive(fields);
            }
            await process.WaitForExitAsync(cancellation);
            var error = await errors;
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("时间轴检查失败：" + error[^Math.Min(error.Length, 1600)..]);
        }
        finally { Kill(process); }
    }
}

internal sealed record MediaPacket(double Pts, double? Dts, double Duration, bool Key, string Hash);
internal sealed record VideoFrame(double Pts, double Duration);
internal sealed record VerifiedTimeline(double FirstVideoSourcePosition, double LastVideoSourcePosition,
    double VideoStart, double VideoEnd, double? AudioStart, double? AudioEnd, int VideoFrames, int VideoPackets);

internal static class TimelineVerification
{
    private const double TimestampTolerance = 0.003; // MP4/MKV time-base rounding, not a seek adjustment.

    private static async Task<List<MediaPacket>> Packets(string path, string stream, string? interval,
        CancellationToken cancellation, bool allowMissingPts = false)
    {
        var packets = new List<MediaPacket>();
        await MediaProcess.ProbeLinesAsync(path, stream, "-show_packets",
            "packet=pts_time,dts_time,duration_time,flags,data_hash", interval, fields =>
        {
            if (fields.Count == 0) return;
            var pts = MediaProcess.Number(fields, "pts_time");
            if (pts is null && !allowMissingPts)
                throw new InvalidOperationException("编码包缺少可验证的显示时间，请使用其他输入或模式。");
            packets.Add(new MediaPacket(pts ?? double.NaN, MediaProcess.Number(fields, "dts_time"),
                MediaProcess.Number(fields, "duration_time") ?? 0,
                fields.GetValueOrDefault("flags", "").Contains('K'), fields.GetValueOrDefault("data_hash", "")));
        }, cancellation);
        return packets;
    }

    private static async Task<List<VideoFrame>> Frames(string path, string? interval, CancellationToken cancellation)
    {
        var frames = new List<VideoFrame>();
        await MediaProcess.ProbeLinesAsync(path, "v:0", "-show_frames",
            "frame=best_effort_timestamp_time,duration_time", interval, fields =>
        {
            if (MediaProcess.Number(fields, "best_effort_timestamp_time") is double pts)
                frames.Add(new VideoFrame(pts, MediaProcess.Number(fields, "duration_time") ?? 0));
        }, cancellation);
        return frames;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("成品时间轴验证失败：" + reason);
    }

    // Packet contents must be a contiguous source sequence, with a single timestamp translation.
    private static int MatchPackets(List<MediaPacket> source, List<MediaPacket> output, double? shift,
        double desiredStart, double tolerance, bool video)
    {
        Require(output.Count > 0, video ? "没有视频包。" : "所选音轨没有音频包。");
        var first = output[0];
        Require(double.IsFinite(first.Pts), "首编码包没有显示时间。");
        var index = source.FindIndex(p => p.Hash == first.Hash && p.Hash.Length > 0 &&
            Math.Abs(p.Pts - desiredStart) <= tolerance &&
            (shift is null || Math.Abs(first.Pts - p.Pts - shift.Value) <= TimestampTolerance));
        Require(index >= 0, video ? "起始关键帧与所选源范围不一致。" : "音轨起点与源范围不一致。");
        Require(!video || first.Key, "首视频包不是关键帧。");
        Require(index + output.Count <= source.Count, "成品包超出可验证的源范围。");
        var translation = shift ?? first.Pts - source[index].Pts;
        for (var i = 0; i < output.Count; i++)
        {
            var original = source[index + i];
            var actual = output[i];
            Require(original.Hash == actual.Hash && actual.Hash.Length > 0, "复制内容丢包或与源轨道不一致。");
            if (double.IsFinite(actual.Pts))
                Require(Math.Abs(actual.Pts - original.Pts - translation) <= TimestampTolerance,
                    "复制包的显示时间不连续或音视频错位。");
            else
                // Matroska can omit negative PTS for HEVC leading pictures. Accept only hash-proven,
                // pre-start source packets; every displayed frame still has to pass the decode checks.
                Require(video && !actual.Key && original.Pts < desiredStart && original.Pts + translation < 0,
                    "编码包缺少无法解释的显示时间。");
            if (original.Dts is double dts && actual.Dts is double actualDts)
                Require(Math.Abs(actualDts - dts - translation) <= TimestampTolerance, "复制包的解码时间异常。");
        }
        return index;
    }

    public static async Task<VerifiedTimeline> VerifyAsync(ExportSpec spec, MediaInfo output, CancellationToken cancellation)
    {
        var absoluteStart = spec.Media.TimelineOrigin + spec.Start;
        var absoluteEnd = spec.Media.TimelineOrigin + spec.End;
        // ffprobe seeks to an earlier keyframe; filter using absolute PTS after reading.
        var interval = $"{MediaProcess.Time(spec.Media.TimelineOrigin + Math.Max(0, spec.Start - 1) - 0.5)}%{MediaProcess.Time(absoluteEnd + 2)}";
        var sourceVideo = await Packets(spec.Media.Path, "v:0", interval, cancellation);
        var outputVideo = await Packets(output.Path, "v:0", null, cancellation, allowMissingPts: spec.Copy);
        Require(sourceVideo.Count > 0 && outputVideo.Count > 0, "视频包为空。");
        var sourceFrames = await Frames(spec.Media.Path, interval, cancellation);
        var outputFrames = await Frames(output.Path, null, cancellation);
        Require(outputFrames.Count > 0, "无法解码首画面。");
        var selected = sourceFrames.Where(f => f.Pts >= absoluteStart - 0.000001 && f.Pts < absoluteEnd - 0.000001).ToList();
        Require(selected.Count > 0, "所选范围内没有实际画面。");
        var shift = outputFrames[0].Pts - selected[0].Pts;
        Require(Math.Abs(shift + absoluteStart) <= 0.1, "视频起点偏离所选范围超过 100 毫秒。");

        if (spec.Copy)
        {
            var index = MatchPackets(sourceVideo, outputVideo, null, selected[0].Pts, TimestampTolerance, true);
            shift = outputVideo[0].Pts - sourceVideo[index].Pts;
            Require(Math.Abs(outputFrames[0].Pts - selected[0].Pts - shift) <= TimestampTolerance,
                "首个可解码画面与源关键帧不一致。");
            Require(outputFrames.Count >= selected.Count, "视频没有覆盖所选范围的末尾。");
            for (var i = 0; i < selected.Count; i++)
                Require(Math.Abs(outputFrames[i].Pts - selected[i].Pts - shift) <= TimestampTolerance,
                    "解码画面缺失或时间与源视频不一致。");
            // A cut in a reordered GOP can retain a later reference frame without its trailing B frames.
            // Every requested frame is required; extra reference frames must still belong to the source.
            foreach (var extra in outputFrames.Skip(selected.Count))
                Require(sourceFrames.Any(f => Math.Abs(extra.Pts - f.Pts - shift) <= TimestampTolerance),
                    "末尾参考画面与源视频不一致。");
            var boundary = sourceVideo.Max(p => Math.Max(0, p.Pts - (p.Dts ?? p.Pts)) + p.Duration);
            Require(outputFrames[^1].Pts - shift <= absoluteEnd + boundary + TimestampTolerance,
                "视频末尾超出编码包边界。");
        }
        else
        {
            Require(outputFrames.Count == selected.Count, $"精确模式画面数不一致（预计 {selected.Count}，实际 {outputFrames.Count}）。");
            for (var i = 0; i < selected.Count; i++)
                Require(Math.Abs(outputFrames[i].Pts - selected[i].Pts - shift) <= TimestampTolerance,
                    "精确模式的实际选帧时间不一致。");
        }

        // Check decoded content as well as packet/timestamp metadata. Lossy encodes allow small pixel differences.
        foreach (var pair in new[] { (selected[0], outputFrames[0]),
                     (spec.Copy ? sourceFrames.First(f => Math.Abs(f.Pts - (outputFrames[^1].Pts - shift)) <= TimestampTolerance) : selected[^1], outputFrames[^1]) })
        {
            var reference = await Sample(spec.Media, pair.Item1.Pts, spec.BurnSubtitle ? Exporter.SubtitleFilter(spec) : null, cancellation);
            var actual = await Sample(output, pair.Item2.Pts, null, cancellation);
            var difference = reference.Zip(actual, (a, b) => Math.Abs(a - b)).Average();
            Require(spec.Copy ? reference.SequenceEqual(actual) : difference <= 8,
                "首尾解码画面与源范围不对应。");
        }

        double? audioStart = null;
        double? audioEnd = null;
        if (spec.AudioIndex is int audio)
        {
            var sourceAudio = await Packets(spec.Media.Path, audio.ToString(CultureInfo.InvariantCulture), interval, cancellation);
            var outputAudio = await Packets(output.Path, output.Audio[0].Index.ToString(CultureInfo.InvariantCulture), null, cancellation);
            Require(sourceAudio.Count > 0, "源音轨没有可验证的音频包。");
            var packetDuration = sourceAudio.Max(p => p.Duration);
            // Priming/pre-roll is permitted by a packet; the A/V translation must remain the same.
            var preroll = spec.CopyKeyframe is { } key ? Math.Max(0, key.Pts - key.Dts) : 0;
            MatchPackets(sourceAudio, outputAudio, shift, absoluteStart, packetDuration + preroll + TimestampTolerance, false);
            audioStart = outputAudio.Min(p => p.Pts);
            audioEnd = outputAudio.Max(p => p.Pts + p.Duration);
            var selectedAudio = sourceAudio.Where(p => p.Pts + p.Duration > absoluteStart && p.Pts < absoluteEnd).ToList();
            if (selectedAudio.Count > 0)
            {
                Require(Math.Abs(outputAudio.Min(p => p.Pts) - shift - selectedAudio[0].Pts) <= packetDuration + preroll + TimestampTolerance,
                    "音频缺少起始包。");
                Require(Math.Abs(outputAudio.Max(p => p.Pts + p.Duration) - shift - selectedAudio.Max(p => p.Pts + p.Duration)) <= packetDuration + TimestampTolerance,
                    "音频没有覆盖所选范围或末尾超出包边界。");
            }
        }
        return new VerifiedTimeline(outputFrames[0].Pts - shift - spec.Media.TimelineOrigin,
            outputFrames[^1].Pts - shift - spec.Media.TimelineOrigin,
            outputFrames[0].Pts, outputFrames[^1].Pts + outputFrames[^1].Duration,
            audioStart, audioEnd, outputFrames.Count, outputVideo.Count);
    }

    private static async Task<byte[]> Sample(MediaInfo media, double absolutePts, string? subtitleFilter,
        CancellationToken cancellation)
    {
        // Select by the original PTS rather than an estimated frame number or nominal frame rate.
        var filters = (subtitleFilter is null ? "" : subtitleFilter + ",") +
            $"select=gte(t\\,{MediaProcess.Time(absolutePts - 0.000001)}),scale=64:64,format=gray";
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-copyts" };
        if (subtitleFilter is null && absolutePts - media.TimelineOrigin > 1)
            args.AddRange(["-ss", MediaProcess.Time(absolutePts - media.TimelineOrigin - 1)]);
        args.AddRange(["-i", media.Path, "-map", "0:v:0", "-an", "-sn", "-vf", filters,
            "-frames:v", "1", "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"]);
        using var process = MediaProcess.Start("ffmpeg", args);
        using var registration = MediaProcess.CancelWith(process, cancellation);
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            var pixels = new byte[4096];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(pixels, cancellation);
            await process.WaitForExitAsync(cancellation);
            var error = await errors;
            Require(process.ExitCode == 0 && string.IsNullOrWhiteSpace(error), "无法验证解码画面：" + error);
            return pixels;
        }
        catch (EndOfStreamException) { throw new InvalidOperationException("成品时间轴验证失败：未能读取预期画面。"); }
        finally { MediaProcess.Kill(process); }
    }
}
