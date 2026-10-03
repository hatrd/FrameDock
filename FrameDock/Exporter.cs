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
    int? SubtitleIndex, bool Copy, bool BurnSubtitle, string OutputPath, Keyframe? CopyKeyframe = null,
    SourceStamp? Source = null, int Rotation = 0, bool NormalizeAudio = false, bool SmoothLoop = false);

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

    public static string Screenshot(MediaInfo media, double position, int? subtitleIndex, int rotation = 0)
    {
        var subtitle = subtitleIndex is null ? "suboff" : $"sub{subtitleIndex}";
        var rotationKey = rotation == 0 ? "" : $"|rot{rotation}";
        var key = $"{SourceState(media.Path)}|{position:R}|{subtitle}{rotationKey}";
        return Path.Combine(Path.GetDirectoryName(media.Path)!,
            $"{Safe(Path.GetFileNameWithoutExtension(media.Path))}__{Time(position)}__{subtitle}_{Id(key)}.png");
    }

    public static string Clip(MediaInfo media, double start, double end, int? audio, int? subtitle,
        bool copy, bool burnSubtitle, string extension, int rotation = 0, bool normalizeAudio = false, bool smoothLoop = false)
    {
        var mode = (copy ? "copy" : "precise") + (normalizeAudio ? "_loud14" : "") + (smoothLoop ? "_smooth" : "");
        var a = audio is null ? "mute" : $"a{audio}";
        var s = burnSubtitle ? $"burn{subtitle?.ToString(CultureInfo.InvariantCulture) ?? "none"}" : "suboff";
        var rotationKey = rotation == 0 ? "" : $"|rot{rotation}";
        var key = $"{SourceState(media.Path)}|{start:R}|{end:R}|{a}|{s}|{mode}|{extension}{rotationKey}";
        return Path.Combine(Path.GetDirectoryName(media.Path)!,
            $"{Safe(Path.GetFileNameWithoutExtension(media.Path))}__{Time(start)}-{Time(end)}__{a}_{s}_{mode}_{Id(key)}{extension}");
    }

    public static string ClipCover(string clipPath, MediaInfo media, double position, int? subtitleIndex, int rotation = 0)
    {
        var rotationKey = rotation == 0 ? "" : $"|rot{rotation}";
        var key = $"{SourceState(media.Path)}|{position:R}|{subtitleIndex}{rotationKey}";
        return Path.Combine(Path.GetDirectoryName(clipPath)!,
            $"{Path.GetFileNameWithoutExtension(clipPath)}__cover_{Time(position)}_{Id(key)}.png");
    }
}

internal static class Exporter
{
    internal static string RotationFilter(int rotation) => rotation switch
    {
        0 => "",
        90 => "transpose=clock",
        180 => "hflip,vflip",
        270 => "transpose=cclock",
        _ => throw new InvalidOperationException("旋转角度必须是 0、90、180 或 270 度。")
    };

    internal static string? ReferenceFilter(ExportSpec spec)
    {
        var filters = new List<string>();
        if (spec.Rotation != 0) filters.Add(RotationFilter(spec.Rotation));
        if (spec.BurnSubtitle) filters.Add(SubtitleFilter(spec));
        return filters.Count == 0 ? null : string.Join(',', filters);
    }

    public static string ExtensionFor(MediaInfo media, int? audioIndex)
    {
        var extension = Path.GetExtension(media.Path).ToLowerInvariant();
        if (extension != ".mp4") return extension == ".mkv" ? ".mkv" : ".mkv";
        var codec = media.Audio.FirstOrDefault(a => a.Index == audioIndex)?.Codec;
        return codec is null or "aac" or "mp3" or "alac" or "ac3" or "eac3" ? ".mp4" : ".mkv";
    }

    public static async Task<MediaInfo> VerifyAsync(ExportSpec spec, string path, CancellationToken cancellation)
    {
        spec = await PrepareAsync(spec, cancellation);
        if (!spec.Media.HasVideo) return await AudioEditing.VerifyAsync(spec, path, cancellation);
        var sourceStamp = SourceStamp.Capture(spec.Media.Path);
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
        var expectedAudio = spec.NormalizeAudio && spec.AudioIndex is not null ? "aac" : spec.Media.Audio.FirstOrDefault(a => a.Index == spec.AudioIndex)?.Codec;
        if (expectedAudio is null ? output.Audio.Count != 0 : output.Audio.Count != 1 || output.Audio[0].Codec != expectedAudio)
            throw new InvalidOperationException("导出音轨与所选音轨不一致。");
        if (output.Subtitles.Count != 0)
            throw new InvalidOperationException("导出文件含有未预期的独立字幕轨。");
        var timeline = await TimelineVerification.VerifyAsync(spec, output, cancellation);
        if (SourceStamp.Capture(spec.Media.Path) != sourceStamp)
            throw new InvalidOperationException("源视频在成品验证期间发生变化，请重新提交。");
        return output with { Verified = timeline };
    }

    internal static string SubtitlesPath(string path) => path.Replace('\\', '/')
        .Replace(":", "\\\\:").Replace("'", "\\\\\\'").Replace(",", "\\,")
        .Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");

    internal static string SubtitleFilter(ExportSpec spec)
    {
        var ordinal = spec.Media.Subtitles.ToList().FindIndex(s => s.Index == spec.SubtitleIndex);
        if (ordinal < 0) throw new InvalidOperationException("找不到要画入的字幕轨。");
        return $"subtitles=filename={SubtitlesPath(spec.Media.Path)}:si={ordinal}";
    }

    private static async Task<ExportSpec> PrepareAsync(ExportSpec spec, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (spec.Source is not null && SourceStamp.Capture(spec.Media.Path) != spec.Source)
            throw new InvalidOperationException("源视频在任务排队期间发生变化，已取消导出。请重新提交。");
        if (!double.IsFinite(spec.Start) || !double.IsFinite(spec.End) || spec.Start < 0 ||
            spec.End <= spec.Start || spec.End > spec.Media.Duration + 0.001)
            throw new InvalidOperationException("截取范围无效。");
        if (spec.Copy && spec.NormalizeAudio) throw new InvalidOperationException("响度标准化需要精确模式。");
        if (spec.NormalizeAudio && spec.AudioIndex is null) throw new InvalidOperationException("响度标准化需要所选音轨。");
        if (spec.Copy && spec.Rotation != 0) throw new InvalidOperationException("旋转视频需要重新编码。请使用精确模式。");
        _ = RotationFilter(spec.Rotation);
        if (spec.Copy && spec.BurnSubtitle) throw new InvalidOperationException("纯复制无法画入字幕。");
        if (spec.AudioIndex is not null && !spec.Media.Audio.Any(a => a.Index == spec.AudioIndex))
            throw new InvalidOperationException("找不到所选音轨。");
        if (spec.Copy && spec.Media.HasVideo)
        {
            var key = spec.CopyKeyframe ?? (await Probe.ReadKeyframesAsync(spec.Media.Path, cancellation))
                .LastOrDefault(k => k.Position <= spec.Start + 0.000001);
            if (key is null || Math.Abs(key.Position - spec.Start) > 0.000001)
                throw new InvalidOperationException("纯复制起点不是可验证的关键帧，请重新选择范围。");
            spec = spec with { CopyKeyframe = key };
        }
        return spec;
    }

    public static async Task<MediaInfo> RunAsync(ExportSpec spec, CancellationToken cancellation)
    {
        spec = await PrepareAsync(spec, cancellation);
        if (!spec.Media.HasVideo)
        {
            if (File.Exists(spec.OutputPath)) return await AudioEditing.VerifyAsync(spec, spec.OutputPath, cancellation);
            return await AudioEditing.ExportAsync(spec, cancellation);
        }
        var exe = ToolPaths.Find("ffmpeg") ?? throw new InvalidOperationException("找不到 ffmpeg。");
        var extension = Path.GetExtension(spec.OutputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(spec.OutputPath)!,
            "." + Path.GetFileNameWithoutExtension(spec.OutputPath) + ".partial-" + Guid.NewGuid().ToString("N") + extension);
        if (File.Exists(spec.OutputPath))
        {
            try { return await VerifyAsync(spec, spec.OutputPath, cancellation); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException("既有文件未通过验证，已保留原文件。请重新生成：" + error.Message, error);
            }
        }
        try
        {
            var audioFilter = spec.NormalizeAudio ? await Loudness.MeasureAsync(spec, cancellation) : null;
            var start = new ProcessStartInfo(exe) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            void Add(params string[] args) { foreach (var arg in args) start.ArgumentList.Add(arg); }
            var packetOffset = spec.Copy && spec.Start > 0 && spec.CopyKeyframe is { } selectedKey
                // Round outward to FFmpeg's microsecond seek precision, never past the packet DTS.
                ? Math.Floor((selectedKey.Dts - selectedKey.Pts) * 1_000_000) / 1_000_000 : 0;
            var coarse = 0d;
            Add("-hide_banner", "-nostdin", "-y", "-v", "warning");
            if (spec.Copy)
            {
                // Input seeking preserves the selected keyframe's negative/reordered DTS.
                // At the beginning, omit seeking altogether; output -ss 0 can discard the first GOP.
                if (spec.Start > 0) Add("-ss", MediaProcess.Time(spec.Start));
                Add("-i", spec.Media.Path);
                // Some demuxers seek to the previous GOP. Gate on the selected packet's DTS,
                // then translate back to its PTS origin. This also retains decoding pre-roll.
                if (spec.Start > 0 && spec.CopyKeyframe is not null)
                    Add("-ss", MediaProcess.Time(packetOffset));
            }
            else
            {
                // Keep subtitle evaluation on the source timeline; seek after filtering/decoding.
                // Preserve source timestamps: input seeking can round its offset to the video time base.
                // A 1/30 time base otherwise shifts fractional seeks by up to half a frame.
                Add("-copyts", "-start_at_zero");
                coarse = spec.BurnSubtitle ? 0 : Math.Max(0, spec.Start - 5);
                if (coarse > 0) Add("-ss", MediaProcess.Time(coarse));
                Add("-i", spec.Media.Path);
                if (spec.Start > 0) Add("-ss", MediaProcess.Time(spec.Start));
            }
            Add("-t", MediaProcess.Time(spec.End - spec.Start - packetOffset), "-map", "0:v:0");
            if (spec.AudioIndex is int audio) Add("-map", $"0:{audio}");
            else Add("-an");
            Add("-sn", "-map_chapters", "-1");
            if (spec.Copy)
            {
                Add("-c", "copy");
                if (packetOffset != 0) Add("-output_ts_offset", MediaProcess.Time(packetOffset), "-avoid_negative_ts", "disabled");
            }
            else
            {
                var filters = new List<string>();
                if (spec.Rotation != 0) filters.Add(RotationFilter(spec.Rotation));
                if (spec.BurnSubtitle)
                {
                    if (spec.Media.TimelineOrigin != 0) filters.Add($"setpts=PTS+{MediaProcess.Time(spec.Media.TimelineOrigin)}/TB");
                    filters.Add(SubtitleFilter(spec));
                    if (spec.Media.TimelineOrigin != 0) filters.Add($"setpts=PTS-{MediaProcess.Time(spec.Media.TimelineOrigin)}/TB");
                }
                filters.Add("settb=AVTB");
                // Use the same one-microsecond boundary tolerance as timeline verification.
                // AVTB rounds timestamps to microseconds; exclude a frame exactly at End.
                filters.Add($"trim=start={MediaProcess.Time(spec.Start - 0.000001)}:end={MediaProcess.Time(spec.End - 0.000001)}");
                Add("-vf", string.Join(',', filters));
                var codec = spec.Media.VideoCodec == "hevc" ? "libx265" : "libx264";
                Add("-c:v", codec, "-preset", "fast", "-crf", codec == "libx265" ? "20" : "18");
                Add("-fps_mode", "passthrough", "-enc_time_base:v", "filter");
                if (audioFilter is not null)
                    Add("-af", $"atrim=start={MediaProcess.Time(spec.Start)}:end={MediaProcess.Time(spec.End)},{audioFilter}", "-c:a", "aac", "-b:a", "192k", "-ar", "48000");
                else if (spec.AudioIndex is not null) Add("-c:a", "copy");
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
            return output with { Path = spec.OutputPath };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
