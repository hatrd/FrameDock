using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FrameDock;

internal sealed record SourceStamp(long Length, long LastWriteTicks)
{
    public static SourceStamp Capture(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("源视频不存在。", path);
        return new SourceStamp(file.Length, file.LastWriteTimeUtc.Ticks);
    }
}

internal sealed record ExportSpec(MediaInfo Media, double Start, double End, int? AudioIndex,
    int? SubtitleIndex, bool Copy, bool BurnSubtitle, string OutputPath, double? CopyPacketStart = null,
    SourceStamp? Source = null);

internal static class OutputNames
{
    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    private static string Time(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}{span.Minutes:00}{span.Seconds:00}_{span.Milliseconds:000}";
    }

    private static string Id(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string SourceState(string source)
    {
        var stamp = SourceStamp.Capture(source);
        return $"{Path.GetFullPath(source)}|{stamp.Length}|{stamp.LastWriteTicks}";
    }

    public static string Screenshot(MediaInfo media, double position, int? subtitleIndex)
    {
        var subtitle = subtitleIndex is null ? "suboff" : $"sub{subtitleIndex}";
        var key = $"{SourceState(media.Path)}|{position:R}|{subtitle}";
        return Path.Combine(Path.GetDirectoryName(media.Path)!,
            $"{Safe(Path.GetFileNameWithoutExtension(media.Path))}__{Time(position)}__{subtitle}_{Id(key)}.png");
    }

    public static string Clip(MediaInfo media, double start, double end, int? audio, int? subtitle,
        bool copy, bool burnSubtitle, string extension)
    {
        var mode = copy ? "copy" : "precise";
        var a = audio is null ? "mute" : $"a{audio}";
        var s = burnSubtitle ? $"burn{subtitle?.ToString(CultureInfo.InvariantCulture) ?? "none"}" : "suboff";
        var key = $"{SourceState(media.Path)}|{start:R}|{end:R}|{a}|{s}|{mode}|{extension}";
        return Path.Combine(Path.GetDirectoryName(media.Path)!,
            $"{Safe(Path.GetFileNameWithoutExtension(media.Path))}__{Time(start)}-{Time(end)}__{a}_{s}_{mode}_{Id(key)}{extension}");
    }

    public static string ClipCover(string clipPath, MediaInfo media, double position, int? subtitleIndex)
    {
        var key = $"{SourceState(media.Path)}|{position:R}|{subtitleIndex}";
        return Path.Combine(Path.GetDirectoryName(clipPath)!,
            $"{Path.GetFileNameWithoutExtension(clipPath)}__cover_{Time(position)}_{Id(key)}.png");
    }
}

internal static class Exporter
{
    public static string ExtensionFor(MediaInfo media, int? audioIndex)
    {
        var extension = Path.GetExtension(media.Path).ToLowerInvariant();
        if (extension != ".mp4") return extension == ".mkv" ? ".mkv" : ".mkv";
        var codec = media.Audio.FirstOrDefault(a => a.Index == audioIndex)?.Codec;
        return codec is null or "aac" or "mp3" or "alac" or "ac3" or "eac3" ? ".mp4" : ".mkv";
    }

    public static async Task<MediaInfo> VerifyAsync(ExportSpec spec, string path, CancellationToken cancellation)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("导出文件不存在或为空。");
        var output = await Probe.ReadAsync(path, cancellation);
        var desiredDuration = spec.End - spec.Start;
        var tolerance = spec.Copy ? Math.Max(0.5, Math.Min(2, desiredDuration * 0.02)) : 0.3;
        if (output.Duration <= 0 || Math.Abs(output.Duration - desiredDuration) > tolerance)
            throw new InvalidOperationException($"导出时长异常：预计 {desiredDuration:F3} 秒，实际 {output.Duration:F3} 秒。");
        var expectedVideo = spec.Copy ? spec.Media.VideoCodec : spec.Media.VideoCodec == "hevc" ? "hevc" : "h264";
        if (output.VideoCodec != expectedVideo)
            throw new InvalidOperationException($"导出视频编码异常：{output.VideoCodec}。");
        var expectedAudio = spec.Media.Audio.FirstOrDefault(a => a.Index == spec.AudioIndex)?.Codec;
        if (expectedAudio is null ? output.Audio.Count != 0 : output.Audio.Count != 1 || output.Audio[0].Codec != expectedAudio)
            throw new InvalidOperationException("导出音轨与所选音轨不一致。");
        if (output.Subtitles.Count != 0)
            throw new InvalidOperationException("导出文件含有未预期的独立字幕轨。");
        return output;
    }

    public static async Task<MediaInfo> RunAsync(ExportSpec spec, CancellationToken cancellation)
    {
        if (spec.Source is not null && SourceStamp.Capture(spec.Media.Path) != spec.Source)
            throw new InvalidOperationException("源视频在任务排队期间发生变化，已取消导出。请重新提交。");
        var exe = ToolPaths.Find("ffmpeg") ?? throw new InvalidOperationException("找不到 ffmpeg。");
        var extension = Path.GetExtension(spec.OutputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(spec.OutputPath)!,
            "." + Path.GetFileNameWithoutExtension(spec.OutputPath) + ".partial-" + Guid.NewGuid().ToString("N") + extension);
        if (File.Exists(spec.OutputPath)) return await VerifyAsync(spec, spec.OutputPath, cancellation);
        try
        {
            var start = new ProcessStartInfo(exe) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            void Add(params string[] args) { foreach (var arg in args) start.ArgumentList.Add(arg); }
            var seek = spec.Copy ? spec.CopyPacketStart ?? spec.Start : spec.Start;
            var coarse = spec.BurnSubtitle ? 0 : Math.Max(0, seek - 5);
            Add("-hide_banner", "-nostdin", "-y", "-v", "warning");
            if (coarse > 0) Add("-ss", coarse.ToString("F6", CultureInfo.InvariantCulture));
            Add("-i", spec.Media.Path, "-ss", (seek - coarse).ToString("F6", CultureInfo.InvariantCulture),
                "-t", (spec.End - seek).ToString("F6", CultureInfo.InvariantCulture), "-map", "0:v:0");
            if (spec.AudioIndex is int audio) Add("-map", $"0:{audio}");
            else Add("-an");
            Add("-sn", "-map_chapters", "-1");
            if (spec.Copy) Add("-c", "copy");
            else
            {
                if (spec.BurnSubtitle)
                {
                    var ordinal = spec.Media.Subtitles.ToList().FindIndex(s => s.Index == spec.SubtitleIndex);
                    if (ordinal < 0) throw new InvalidOperationException("找不到要画入的字幕轨。");
                    var path = spec.Media.Path.Replace('\\', '/')
                        .Replace(":", "\\\\:")
                        .Replace("'", "\\\\\\'")
                        .Replace(",", "\\,")
                        .Replace(";", "\\;")
                        .Replace("[", "\\[")
                        .Replace("]", "\\]");
                    Add("-vf", $"subtitles=filename={path}:si={ordinal}");
                }
                var codec = spec.Media.VideoCodec == "hevc" ? "libx265" : "libx264";
                Add("-c:v", codec, "-preset", "fast", "-crf", codec == "libx265" ? "20" : "18");
                if (spec.AudioIndex is not null) Add("-c:a", "copy");
            }
            Add(temporary);
            using var process = new Process { StartInfo = start };
            process.Start();
            using var registration = cancellation.Register(() => {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0)
            {
                var error = await stderr;
                throw new InvalidOperationException("FFmpeg 导出失败：" + error[^Math.Min(error.Length, 1600)..]);
            }
            await stdout;
            var output = await VerifyAsync(spec, temporary, cancellation);
            if (spec.Source is not null && SourceStamp.Capture(spec.Media.Path) != spec.Source)
                throw new InvalidOperationException("源视频在导出期间发生变化，已取消导出。请重新提交。");
            File.Move(temporary, spec.OutputPath);
            return output;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
