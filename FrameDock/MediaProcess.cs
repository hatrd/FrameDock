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
        finally
        {
            Kill(process);
            // Cancellation is complete only after the process has released its source file.
            await process.WaitForExitAsync();
        }
    }
}
