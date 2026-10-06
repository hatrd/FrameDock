using System.Globalization;
using System.Text.Json;

namespace FrameDock;

internal static class Loudness
{
    internal const string Target = "loudnorm=I=-14:TP=-1:LRA=11";
    internal const string AlgorithmVersion = "linear-gain-v2";
    internal const double IntegratedTarget = -14;
    internal const double ProcessingPeak = -2;

    internal static string GainFilter(double integrated, double peak)
    {
        if (!double.IsFinite(integrated) || !double.IsFinite(peak))
            throw new InvalidOperationException("响度测量结果无效，请重新导出。");
        // loudnorm's linear=true is conditional: peak/LRA constraints silently enable dynamic
        // compression. Apply one fixed gain instead, accepting lower LUFS to preserve transients.
        // Leave 1 dB below the advertised -1 dBTP ceiling for AAC reconstruction overshoot.
        var gain = Math.Min(IntegratedTarget - integrated, ProcessingPeak - peak);
        return $"volume={gain.ToString("R", CultureInfo.InvariantCulture)}dB:precision=double";
    }

    public static async Task<string> MeasureAsync(ExportSpec spec, CancellationToken cancellation)
    {
        using var process = MediaProcess.Start("ffmpeg", ["-hide_banner", "-nostdin", "-v", "info",
            "-ss", MediaProcess.Time(spec.Start), "-i", spec.Media.Path,
            "-map", $"0:{spec.AudioIndex}",
            "-vn", "-sn", "-af", $"atrim=duration={MediaProcess.Time(spec.End - spec.Start)},{Target}:print_format=json", "-f", "null", "-"]);
        using var registration = MediaProcess.CancelWith(process, cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        finally { MediaProcess.Kill(process); await process.WaitForExitAsync(); }
        var report = await stderr;
        await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("响度测量失败：" + report[^Math.Min(report.Length, 1600)..]);
        var begin = report.LastIndexOf('{');
        var end = report.LastIndexOf('}');
        if (begin < 0 || end < begin) throw new InvalidOperationException("FFmpeg 未返回响度测量结果，请检查 FFmpeg 版本。");
        using var json = JsonDocument.Parse(report[begin..(end + 1)]);
        double? Value(string key)
        {
            var raw = json.RootElement.GetProperty(key).GetString();
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? number : null;
        }
        var integrated = Value("input_i");
        // Silence has no finite integrated loudness; preserve it rather than amplifying noise.
        if (integrated is null) return "anull";
        var peak = Value("input_tp");
        if (peak is null)
            throw new InvalidOperationException("响度测量结果无效，请重新导出。");
        return GainFilter(integrated.Value, peak.Value);
    }
}
