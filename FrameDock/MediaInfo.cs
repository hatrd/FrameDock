using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FrameDock;

internal sealed record MediaTrack(int Index, string Codec, string Label, bool IsDefault);
internal sealed record MediaInfo(string Path, double Duration, string VideoCodec,
    IReadOnlyList<MediaTrack> Audio, IReadOnlyList<MediaTrack> Subtitles);
internal sealed record Keyframe(double Pts, double Dts);

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
        var exe = ToolPaths.Find("ffprobe") ?? throw new InvalidOperationException("找不到 ffprobe。");
        using var process = new Process { StartInfo = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new InvalidOperationException($"无法读取视频：{await error}");
        using var json = JsonDocument.Parse(await output);
        var root = json.RootElement;
        var duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var audio = new List<MediaTrack>();
        var subtitles = new List<MediaTrack>();
        string? codec = null;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var kind = stream.GetProperty("codec_type").GetString();
            var currentCodec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "unknown" : "unknown";
            if (kind == "video" && codec is null) { codec = currentCodec; continue; }
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
        if (codec is null) throw new InvalidOperationException("文件没有可播放的视频轨。");
        return new MediaInfo(path, duration, codec, audio, subtitles);
    }

    public static async Task<List<Keyframe>> ReadKeyframesAsync(string path, CancellationToken cancellation)
    {
        var exe = ToolPaths.Find("ffprobe") ?? throw new InvalidOperationException("找不到 ffprobe。");
        using var process = new Process { StartInfo = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_packets",
                     "-show_entries", "packet=pts_time,dts_time,flags", "-of", "csv=p=0", path })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        var keyframes = new List<Keyframe>();
        while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
        {
            var parts = line.Split(',', 3);
            if (parts.Length == 3 && parts[2].Contains('K') &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
            {
                var dts = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : time;
                keyframes.Add(new Keyframe(Math.Max(0, time), Math.Max(0, dts)));
            }
        }
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new InvalidOperationException($"读取关键帧失败：{await errors}");
        keyframes.Sort((a, b) => a.Pts.CompareTo(b.Pts));
        if (keyframes.Count == 0) keyframes.Add(new Keyframe(0, 0));
        return keyframes;
    }
}
