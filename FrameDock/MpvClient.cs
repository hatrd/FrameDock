using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FrameDock;

internal sealed class MpvClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly SemaphoreSlim seekLock = new(1, 1);
    private sealed class SeekOperation
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; set; }
        public double? PreviousPosition { get; init; }
    }
    private volatile SeekOperation? seekOperation;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> requests = new();
    private readonly CancellationTokenSource lifetime = new();
    private int nextRequest;
    private double timelineOrigin;

    public event Action<double>? PositionChanged;
    public event Action<bool>? PauseChanged;

    private MpvClient(Process process, NamedPipeClientStream pipe)
    {
        this.process = process;
        this.pipe = pipe;
        reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _ = ReadLoopAsync();
    }

    public static async Task<MpvClient> StartAsync(IntPtr hostWindow, bool headless = false)
    {
        var exe = ToolPaths.Find("mpv") ?? throw new InvalidOperationException("找不到 mpv.exe，请使用发布包或设置 FRAMEDOCK_MPV。");
        var pipeName = "framedock-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--no-config", "--no-osc", "--no-input-default-bindings", "--input-vo-keyboard=no",
                     "--idle=yes", "--pause=yes", "--force-window=" + (headless ? "no" : "yes"),
                     "--rebase-start-time=no", "--keep-open=yes", "--terminal=no", "--sub-auto=no",
                     "--wid=" + unchecked((uint)hostWindow.ToInt64()).ToString(CultureInfo.InvariantCulture),
                     "--input-ipc-server=" + pipeName })
            start.ArgumentList.Add(arg);
        if (headless)
        {
            start.ArgumentList.Add("--vo=null");
            start.ArgumentList.Add("--ao=null");
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 mpv。");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(8000);
            var client = new MpvClient(process, pipe);
            await client.CommandAsync("observe_property", 1, "time-pos");
            await client.CommandAsync("observe_property", 2, "pause");
            return client;
        }
        catch
        {
            pipe.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested && await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("event", out var playbackEvent) && seekOperation is { } operation)
                {
                    var eventName = playbackEvent.GetString();
                    if (eventName is "seek" or "file-loaded") operation.Started = true;
                    if (eventName == "end-file" && root.TryGetProperty("reason", out var reason) && reason.GetString() == "error")
                        operation.Completion.TrySetException(new InvalidOperationException("mpv 无法读取视频。"));
                    if (operation.Started && eventName is "playback-restart" or "end-file")
                        operation.Completion.TrySetResult();
                }
                if (root.TryGetProperty("request_id", out var id) && id.TryGetInt32(out var requestId) &&
                    requests.TryRemove(requestId, out var pending))
                    pending.TrySetResult(root.Clone());
                if (root.TryGetProperty("event", out var kind) && kind.GetString() == "property-change" &&
                    root.TryGetProperty("name", out var name) && root.TryGetProperty("data", out var value))
                {
                    if (name.GetString() == "time-pos" && value.ValueKind == JsonValueKind.Number)
                    {
                        if (seekOperation is { PreviousPosition: double previous } step && Math.Abs(value.GetDouble() - previous) > 0.000001)
                            step.Completion.TrySetResult();
                        PositionChanged?.Invoke(value.GetDouble() - timelineOrigin);
                    }
                    if (name.GetString() == "pause" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        PauseChanged?.Invoke(value.GetBoolean());
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or JsonException) { }
        finally
        {
            foreach (var pending in requests.Values) pending.TrySetException(new IOException("mpv 连接已关闭。"));
            requests.Clear();
            seekOperation?.Completion.TrySetException(new IOException("mpv 连接已关闭。"));
        }
    }

    public async Task<JsonElement> CommandAsync(params object?[] command)
    {
        var id = Interlocked.Increment(ref nextRequest);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id] = pending;
        await writeLock.WaitAsync(lifetime.Token);
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = id })); }
        catch { requests.TryRemove(id, out _); throw; }
        finally { writeLock.Release(); }
        var reply = await pending.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (reply.TryGetProperty("error", out var error) && error.GetString() != "success")
            throw new InvalidOperationException("mpv: " + error.GetString());
        return reply.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    public async Task LoadAsync(string path, double origin = 0)
    {
        timelineOrigin = origin;
        await RestartPlaybackAsync("loadfile", path, "replace");
        PositionChanged?.Invoke((await PropertyAsync("time-pos")).GetDouble());
    }
    public Task PauseAsync(bool pause) => CommandAsync("set_property", "pause", pause);
    public Task SeekAsync(double seconds) => RestartPlaybackAsync("seek", seconds + timelineOrigin, "absolute+exact");
    public Task JumpAsync(double seconds) => RestartPlaybackAsync("seek", seconds, "relative+exact");
    public async Task StepAsync(bool backward)
    {
        if (backward)
        {
            if ((await PropertyAsync("time-pos")).GetDouble() <= 0.000001) return;
            await RestartPlaybackAsync("frame-back-step");
            return;
        }
        await seekLock.WaitAsync(lifetime.Token);
        try
        {
            if ((await PropertyAsync("eof-reached")).GetBoolean()) return;
            var previous = (await CommandAsync("get_property", "time-pos")).GetDouble();
            var operation = new SeekOperation { PreviousPosition = previous };
            seekOperation = operation;
            await CommandAsync("frame-step");
            await operation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
        }
        finally { seekOperation = null; seekLock.Release(); }
    }
    public Task StopAsync() => CommandAsync("stop");
    public async Task<JsonElement> PropertyAsync(string name)
    {
        var value = await CommandAsync("get_property", name);
        return name == "time-pos" && value.ValueKind == JsonValueKind.Number
            ? JsonSerializer.SerializeToElement(value.GetDouble() - timelineOrigin) : value;
    }

    private async Task RestartPlaybackAsync(params object?[] command)
    {
        await seekLock.WaitAsync(lifetime.Token);
        var operation = new SeekOperation();
        seekOperation = operation;
        try
        {
            await CommandAsync(command);
            // A command reply acknowledges the request; playback-restart means the new frame is ready.
            await operation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
        }
        finally { seekOperation = null; seekLock.Release(); }
    }

    public async Task SetTrackAsync(string kind, int? ffIndex)
    {
        if (ffIndex is null) { await CommandAsync("set_property", kind, "no"); return; }
        var tracks = await PropertyAsync("track-list");
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.TryGetProperty("ff-index", out var index) && index.GetInt32() == ffIndex.Value &&
                track.TryGetProperty("id", out var id))
            {
                await CommandAsync("set_property", kind, id.GetInt32());
                return;
            }
        }
        throw new InvalidOperationException("mpv 找不到所选轨道。");
    }

    public async Task ScreenshotAsync(string path, bool withSubtitles)
    {
        await CommandAsync("screenshot-to-file", path, withSubtitles ? "subtitles" : "video");
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        pipe.Dispose();
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process.Dispose();
        lifetime.Dispose();
        writeLock.Dispose();
    }
}
