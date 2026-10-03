using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FrameDock;

internal sealed record MediaTrack(int Index, string Codec, string Label, bool IsDefault);
internal sealed record MediaInfo(string Path, double Duration, string VideoCodec,
    IReadOnlyList<MediaTrack> Audio, IReadOnlyList<MediaTrack> Subtitles,
    double TimelineOrigin = 0, string VideoTimeBase = "1/1000", VerifiedTimeline? Verified = null)
{
    public bool HasVideo => VideoCodec.Length > 0;
}
// Pts/Dts are original source timestamps. Position is relative to the player's origin.
internal sealed record Keyframe(double Pts, double Dts, double TimelineOrigin = 0,
    long? RawPts = null, long? RawDts = null, string TimeBase = "1/1000")
{
    public double Position => Pts - TimelineOrigin;
}

internal static class ToolPaths
{
    public static string? Find(string name)
    {
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, name + ".exe");
        if (File.Exists(bundled)) return bundled;
        if (name == "mpv")
        {
            var configured = Environment.GetEnvironmentVariable("FRAMEDOCK_MPV");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var reuse = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Tools", "mpv", "player", "mpv.exe");
            if (File.Exists(reuse)) return reuse;
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = System.IO.Path.Combine(folder.Trim('"'), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        if (name is "ffmpeg" or "ffprobe")
        {
            var packages = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var package in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg_*"))
                {
                    try
                    {
                        var exe = Directory.EnumerateFiles(package, name + ".exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (exe is not null) return exe;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        return null;
    }

    public static async Task EnsureFfmpegAsync()
    {
        if (Find("ffmpeg") is not null && Find("ffprobe") is not null) return;
        using var process = new Process { StartInfo = new ProcessStartInfo("winget") {
            UseShellExecute = false, CreateNoWindow = true
        } };
        foreach (var arg in new[] { "install", "-e", "--id", "Gyan.FFmpeg", "--accept-package-agreements", "--accept-source-agreements" })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("无法启动 winget 安装 FFmpeg。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"winget 安装 FFmpeg 失败，退出码 {process.ExitCode}。");
        if (Find("ffmpeg") is null || Find("ffprobe") is null)
            throw new InvalidOperationException("FFmpeg 已安装，但当前进程还找不到它。请重新启动 FrameDock。");
    }
}

internal static class Probe
{
    public static async Task<MediaInfo> ReadAsync(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var exe = ToolPaths.Find("ffprobe") ?? throw new InvalidOperationException("找不到 ffprobe。");
        using var process = new Process { StartInfo = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var registration = MediaProcess.CancelWith(process, cancellation);
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        finally
        {
            MediaProcess.Kill(process);
            await process.WaitForExitAsync();
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"无法读取视频：{await error}");
        using var json = JsonDocument.Parse(await output);
        var root = json.RootElement;
        var duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var origin = root.GetProperty("format").TryGetProperty("start_time", out var originValue) &&
            double.TryParse(originValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOrigin)
            ? parsedOrigin : 0;
        // Matroska's segment duration is measured from timestamp zero, even with a nonzero first cluster.
        var formatName = root.GetProperty("format").GetProperty("format_name").GetString() ?? "";
        if (formatName.Contains("matroska") || formatName.Contains("webm")) duration -= origin;
        var timeBase = "1/1000";
        var audio = new List<MediaTrack>();
        var subtitles = new List<MediaTrack>();
        string? codec = null;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var kind = stream.GetProperty("codec_type").GetString();
            var currentCodec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "unknown" : "unknown";
            var attachedPicture = stream.TryGetProperty("disposition", out var pic) &&
                pic.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1;
            if (kind == "video" && codec is null && !attachedPicture)
            {
                codec = currentCodec;
                timeBase = stream.GetProperty("time_base").GetString()!;
                continue;
            }
            if (kind is not ("audio" or "subtitle")) continue;
            var index = stream.GetProperty("index").GetInt32();
            var isDefault = stream.TryGetProperty("disposition", out var disposition) &&
                disposition.TryGetProperty("default", out var flag) && flag.GetInt32() == 1;
            var language = stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var lang)
                ? lang.GetString() : null;
            var title = stream.TryGetProperty("tags", out tags) && tags.TryGetProperty("title", out var t)
                ? t.GetString() : null;
            var label = $"{index}: {language ?? "und"} {title} ({currentCodec})";
            var track = new MediaTrack(index, currentCodec, label, isDefault);
            (kind == "audio" ? audio : subtitles).Add(track);
        }
        if (codec is null && audio.Count == 0) throw new InvalidOperationException("文件没有可播放的音频或视频轨。");
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidOperationException("媒体时长无效。");
        return new MediaInfo(path, duration, codec ?? "", audio, subtitles, origin, timeBase);
    }

    public static async Task<List<Keyframe>> ReadKeyframesAsync(string path, CancellationToken cancellation)
    {
        var media = await ReadAsync(path, cancellation);
        var keyframes = new List<Keyframe>();
        await MediaProcess.ProbeLinesAsync(path, "v:0", "-show_packets",
            "packet=pts,dts,pts_time,dts_time,flags", null, fields =>
        {
            if (fields.GetValueOrDefault("flags", "").Contains('K') &&
                MediaProcess.Number(fields, "pts_time") is double pts)
            {
                var rawPts = MediaProcess.Integer(fields, "pts");
                var rawDts = MediaProcess.Integer(fields, "dts");
                var parts = media.VideoTimeBase.Split('/');
                var tick = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
                keyframes.Add(new Keyframe(rawPts is long p ? p * tick : pts,
                    rawDts is long d ? d * tick : MediaProcess.Number(fields, "dts_time") ?? pts,
                    media.TimelineOrigin, rawPts, rawDts, media.VideoTimeBase));
            }
        }, cancellation);
        keyframes.Sort((a, b) => a.Pts.CompareTo(b.Pts));
        if (keyframes.Count == 0) throw new InvalidOperationException("找不到可验证的关键帧，请使用精确模式。");
        return keyframes;
    }
}
