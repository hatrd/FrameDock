namespace FrameDock;

internal sealed record AudioEnvelope(float[] Peaks, float[] Energy, double Step);
internal sealed record BeatEstimate(double Bpm, double Offset, double Confidence);

internal static class AudioEditing
{
    public const int SampleRate = 48000;

    // Stream a reduced mono signal; never retain a whole decoded song in the UI.
    public static async Task<AudioEnvelope> AnalyzeAsync(MediaInfo media, int index, CancellationToken cancellation)
    {
        const int rate = 8000;
        var bucketSize = Math.Max(80, (int)Math.Ceiling(media.Duration * rate / 600_000));
        var peaks = new List<float>();
        var energy = new List<float>();
        using var process = MediaProcess.Start("ffmpeg", ["-v", "error", "-nostdin", "-i", media.Path,
            "-map", $"0:{index}", "-vn", "-sn", "-ac", "1", "-ar", rate.ToString(), "-f", "f32le", "-"]);
        using var registration = MediaProcess.CancelWith(process, cancellation);
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        var buffer = new byte[32768];
        var carry = 0;
        var count = 0;
        var peak = 0f;
        var sum = 0d;
        try
        {
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry), cancellation)) > 0)
            {
                var available = read + carry;
                var bytes = available / 4 * 4;
                for (var i = 0; i < bytes; i += 4)
                {
                    var value = BitConverter.ToSingle(buffer, i);
                    peak = Math.Max(peak, Math.Abs(value));
                    sum += value * value;
                    if (++count == bucketSize) Flush();
                }
                carry = available - bytes;
                Array.Copy(buffer, bytes, buffer, 0, carry);
            }
            if (count > 0) Flush();
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0) throw new InvalidOperationException("波形分析失败：" + await errors);
            return new AudioEnvelope(peaks.ToArray(), energy.ToArray(), bucketSize / (double)rate);
        }
        finally { MediaProcess.Kill(process); await process.WaitForExitAsync(); }

        void Flush()
        {
            peaks.Add(peak); energy.Add((float)Math.Sqrt(sum / count));
            count = 0; peak = 0; sum = 0;
        }
    }

    public static BeatEstimate? Detect(AudioEnvelope envelope)
    {
        var energy = envelope.Energy;
        // Tempo from positive energy changes, with normalized autocorrelation.
        var length = Math.Min(energy.Length, (int)(180 / envelope.Step));
        if (length * envelope.Step < 4) return null;
        var onset = new double[length];
        for (var i = 1; i < length; i++) onset[i] = Math.Max(0, energy[i] - energy[i - 1]);
        var best = 0d;
        var bestLag = 0;
        for (var lag = Math.Max(1, (int)(60 / 220d / envelope.Step)); lag <= 60 / 55d / envelope.Step; lag++)
        {
            double sum = 0, a = 0, b = 0;
            for (var i = lag; i < length; i++)
            {
                sum += onset[i] * onset[i - lag];
                a += onset[i] * onset[i]; b += onset[i - lag] * onset[i - lag];
            }
            var score = sum / Math.Sqrt(Math.Max(1e-20, a * b));
            // Favor the faster member of equally strong half/double-tempo candidates.
            if (score > best + 0.015) { best = score; bestLag = lag; }
        }
        if (bestLag == 0 || best < 0.12) return null;
        var phase = 0;
        var phaseScore = 0d;
        for (var offset = 0; offset < bestLag; offset++)
        {
            var score = 0d;
            for (var i = offset; i < length; i += bestLag) score += onset[i];
            if (score > phaseScore) { phaseScore = score; phase = offset; }
        }
        return new BeatEstimate(60 / (bestLag * envelope.Step), phase * envelope.Step, best);
    }

    public static double Snap(double position, double bpm, double offset, double duration) =>
        bpm <= 0 ? Math.Clamp(position, 0, duration) :
        Math.Clamp(offset + Math.Round((position - offset) * bpm / 60) * 60 / bpm, 0, duration);

    public static string TrimFilter(double duration, bool smooth)
    {
        var samples = Math.Max(1, (long)Math.Round(duration * SampleRate));
        var filter = $"aresample={SampleRate},apad,atrim=end_sample={samples},asetpts=N/SR/TB";
        if (smooth)
        {
            // Two milliseconds at each end remove sample discontinuities without changing beat length.
            var fade = Math.Min(96, samples / 2);
            if (fade > 0) filter += $",afade=t=in:ss=0:ns={fade},afade=t=out:ss={samples - fade}:ns={fade}";
        }
        return filter;
    }

    public static async Task RunFfmpegAsync(IEnumerable<string> args, CancellationToken cancellation)
    {
        using var process = MediaProcess.Start("ffmpeg", args);
        using var registration = MediaProcess.CancelWith(process, cancellation);
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        try
        {
            await process.WaitForExitAsync(cancellation);
            await output;
            var error = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("音频/循环处理失败：" + error[^Math.Min(error.Length, 1600)..]);
        }
        finally { MediaProcess.Kill(process); await process.WaitForExitAsync(); }
    }

    public static async Task<MediaInfo> ExportAsync(ExportSpec spec, CancellationToken cancellation)
    {
        if (spec.AudioIndex is null) throw new InvalidOperationException("请选择要导出的音轨。");
        if (spec.Source is not null && spec.Source != SourceStamp.Capture(spec.Media.Path))
            throw new InvalidOperationException("源音频发生变化，请重新打开。");
        var temporary = spec.OutputPath + ".partial-" + Guid.NewGuid().ToString("N") + ".wav";
        try
        {
            var filter = spec.NormalizeAudio ? await Loudness.MeasureAsync(spec, cancellation) + "," : "";
            filter += TrimFilter(spec.End - spec.Start, spec.SmoothLoop);
            await RunFfmpegAsync(["-v", "error", "-nostdin", "-y", "-ss", MediaProcess.Time(spec.Start),
                "-i", spec.Media.Path, "-map", $"0:{spec.AudioIndex}", "-vn", "-sn", "-af", filter,
                "-c:a", "pcm_s24le", temporary], cancellation);
            var result = await VerifyAsync(spec, temporary, cancellation);
            if (spec.Source is not null && spec.Source != SourceStamp.Capture(spec.Media.Path))
                throw new InvalidOperationException("源音频在导出期间发生变化，请重新提交。");
            File.Move(temporary, spec.OutputPath);
            return result with { Path = spec.OutputPath };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<MediaInfo> VerifyAsync(ExportSpec spec, string path, CancellationToken cancellation)
    {
        var result = await Probe.ReadAsync(path, cancellation);
        if (result.HasVideo || result.Audio.Count != 1 || result.Audio[0].Codec != "pcm_s24le" ||
            Math.Abs(result.Duration - (spec.End - spec.Start)) > 2d / SampleRate)
            throw new InvalidOperationException("音频成品的编码或采样时长不符合选区，请重新导出。");
        return result;
    }
}
