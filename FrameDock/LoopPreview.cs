namespace FrameDock;

internal sealed record LoopPreview(string Path, double Start, double Duration, int Frames, long Samples, bool HasAudio) : IDisposable
{
    public string Graph
    {
        get
        {
            var parts = new List<string>();
            // Both clocks advance continuously. No seek, decoder flush or audio-device restart at the seam.
            if (Frames > 0) parts.Add($"[vid1]loop=loop=-1:size={Frames}:start=0,setpts=N*{Samples}/({Frames}*48000*TB)[vo]");
            if (HasAudio) parts.Add($"[aid1]aloop=loop=-1:size={Samples}:start=0,asetpts=N/SR/TB[ao]");
            return string.Join(';', parts);
        }
    }

    public static async Task<LoopPreview> CreateAsync(MediaInfo media, double start, double end,
        int? audio, int? subtitle, int rotation, bool smooth, CancellationToken cancellation)
    {
        var samples = (long)Math.Round((end - start) * AudioEditing.SampleRate);
        if (start < 0 || end > media.Duration + 0.001 || samples < 1)
            throw new InvalidOperationException("循环范围无效。");
        // An infinite filter retains one decoded cycle. Bound audio and video memory explicitly.
        if (end - start > 600)
            throw new InvalidOperationException("无缝循环最多缓冲 10 分钟，请缩短选区；普通播放和导出不受此限制。");
        if (!media.HasVideo && audio is null) throw new InvalidOperationException("请选择试听音轨。");
        var duration = samples / (double)AudioEditing.SampleRate;
        var frames = media.HasVideo ? Math.Max(1, (int)Math.Round(duration * 30)) : 0;
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "framedock-loop-" + Guid.NewGuid().ToString("N") + ".nut");
        try
        {
            var args = new List<string> { "-v", "error", "-nostdin", "-y", "-ss", MediaProcess.Time(start), "-i", media.Path };
            if (media.HasVideo)
            {
                // Fit retained YUV frames inside 128 MiB. Only loop preview resolution is reduced.
                var side = Math.Clamp((int)Math.Sqrt(128d * 1024 * 1024 / (frames * 1.5)), 64, 1280) / 2 * 2;
                var filters = new List<string>();
                if (subtitle is not null)
                {
                    filters.Add($"setpts=PTS+{MediaProcess.Time(start + media.TimelineOrigin)}/TB");
                    filters.Add(Exporter.SubtitleFilter(new ExportSpec(media, start, end, audio, subtitle, false, true, path)));
                    filters.Add($"setpts=PTS-{MediaProcess.Time(start + media.TimelineOrigin)}/TB");
                }
                if (rotation != 0) filters.Add(Exporter.RotationFilter(rotation));
                filters.Add($"scale={side}:{side}:force_original_aspect_ratio=decrease:force_divisible_by=2");
                filters.Add($"fps={frames * (long)AudioEditing.SampleRate}/{samples}");
                filters.Add($"tpad=stop_mode=clone:stop_duration={MediaProcess.Time(duration)}");
                filters.Add($"trim=end_frame={frames},setpts=PTS-STARTPTS");
                args.AddRange(["-map", "0:v:0", "-vf", string.Join(',', filters), "-c:v", "rawvideo", "-pix_fmt", "yuv420p"]);
            }
            else args.Add("-vn");
            if (audio is int index)
                args.AddRange(["-map", $"0:{index}", "-af", AudioEditing.TrimFilter(duration, smooth), "-ac", "2", "-c:a", "pcm_s16le"]);
            else args.Add("-an");
            args.AddRange(["-sn", "-map_chapters", "-1", "-t", MediaProcess.Time(duration), "-f", "nut", path]);
            await AudioEditing.RunFfmpegAsync(args, cancellation);
            return new LoopPreview(path, start, duration, frames, samples, audio is not null);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
}
