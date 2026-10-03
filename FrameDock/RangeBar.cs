using System.Globalization;

namespace FrameDock;

internal sealed class RangeBar : Control
{
    private enum DragTarget { None, Play, Start, End, Pan, Overview }
    private const int PaddingX = 18;
    private const double MinimumRange = 0.001;
    private DragTarget dragging;
    private double duration = 1;
    private double position;
    private double start;
    private double end = 1;
    private double viewStart;
    private double viewLength = 1;
    private int lastMouseX;
    private int dragOffsetX;
    private readonly System.Windows.Forms.Timer edgeScroll = new() { Interval = 40 };

    public event Action<double>? SeekRequested;
    public event Action? RangeChanged;
    public double ViewStart => viewStart;
    public double ViewLength => viewLength;
    public bool IsScrubbing => dragging == DragTarget.Play;
    public AudioEnvelope? Waveform { get; set; }
    public bool ShowWaveform { get; set; }
    public bool SnapToBeat { get; set; }
    public double Bpm { get; set; }
    public double BeatOffset { get; set; }
    public double Snap(double time) => SnapToBeat ? AudioEditing.Snap(time, Bpm, BeatOffset, duration) : time;

    public double Duration
    {
        get => duration;
        set
        {
            duration = double.IsFinite(value) ? Math.Max(MinimumRange, value) : 1;
            start = position = viewStart = 0;
            end = viewLength = duration;
            Invalidate();
            RangeChanged?.Invoke();
        }
    }
    public double Position { get => position; set { position = Math.Clamp(value, 0, duration); Invalidate(); } }
    public double Start
    {
        get => start;
        set { start = Math.Clamp(Snap(value), 0, Math.Max(0, end - MinimumRange)); Invalidate(); RangeChanged?.Invoke(); }
    }
    public double End
    {
        get => end;
        set { end = Math.Clamp(Snap(value), Math.Min(duration, start + MinimumRange), duration); Invalidate(); RangeChanged?.Invoke(); }
    }

    public RangeBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Height = 90;
        Cursor = Cursors.Hand;
        BackColor = StudioTheme.Canvas;
        edgeScroll.Tick += (_, _) => {
            if (dragging is not (DragTarget.Start or DragTarget.End or DragTarget.Play)) return;
            var direction = lastMouseX <= PaddingX ? -1 : lastMouseX >= Width - PaddingX ? 1 : 0;
            if (direction == 0) return;
            SetView(viewStart + direction * viewLength * 0.04, viewLength);
            MoveHandle(lastMouseX);
        };
    }

    private int TrackWidth => Math.Max(1, Width - 2 * PaddingX);
    private const int Center = 40;
    private int OverviewY => Height - 14;
    private int X(double seconds) => PaddingX + (int)Math.Round(TrackWidth * (seconds - viewStart) / viewLength);
    private int OverviewX(double seconds) => PaddingX + (int)Math.Round(TrackWidth * seconds / duration);
    private double Time(int x) => Math.Clamp(viewStart + (double)(x - PaddingX) / TrackWidth * viewLength, 0, duration);
    private bool IsTimeVisible(double time) => time >= viewStart && time <= viewStart + viewLength;
    private Rectangle StartGrip => new(X(start) - 3, Center - 22, 23, 23);
    private Rectangle EndGrip => new(X(end) - 20, Center - 22, 23, 23);

    public void ResetView() => SetView(0, duration);
    public void FitSelection()
    {
        var margin = Math.Max(0.05, (end - start) * 0.1);
        SetView(start - margin, end - start + margin * 2);
    }
    public void ResetRange()
    {
        start = 0;
        end = duration;
        RangeChanged?.Invoke();
        ResetView();
    }
    public void EnsureVisible(double time)
    {
        if (!IsTimeVisible(time)) SetView(time - viewLength / 2, viewLength);
    }
    private void SetView(double offset, double length)
    {
        viewLength = Math.Clamp(length, Math.Min(0.5, duration), duration);
        viewStart = Math.Clamp(offset, 0, Math.Max(0, duration - viewLength));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        using var baseBrush = new SolidBrush(StudioTheme.Border);
        using var selectedBrush = new SolidBrush(StudioTheme.Accent);
        using var startBrush = new SolidBrush(StudioTheme.Start);
        using var endBrush = new SolidBrush(StudioTheme.End);
        using var playPen = new Pen(Color.White, 2);
        using var gridPen = new Pen(StudioTheme.Raised);
        using var overviewPen = new Pen(StudioTheme.Muted);
        using var wash = new SolidBrush(StudioTheme.Selection);
        var rulerStep = TickStep(viewLength / Math.Max(2, TrackWidth / 100));
        for (var t = Math.Ceiling(viewStart / rulerStep) * rulerStep; t <= viewStart + viewLength; t += rulerStep)
        {
            var x = X(t);
            g.DrawLine(gridPen, x, 16, x, Center + 8);
            TextRenderer.DrawText(g, TickLabel(t, rulerStep), Font, new Point(Math.Clamp(x - 25, 0, Math.Max(0, Width - 65)), 0), StudioTheme.Muted);
        }
        g.FillRectangle(baseBrush, PaddingX, Center - 4, TrackWidth, 8);
        var left = Math.Clamp(X(start), PaddingX, Width - PaddingX);
        var right = Math.Clamp(X(end), PaddingX, Width - PaddingX);
        g.FillRectangle(wash, left, Center - 20, Math.Max(0, right - left), 45);
        g.FillRectangle(selectedBrush, left, Center - 4, Math.Max(0, right - left), 8);
        if (ShowWaveform && Waveform is { } waveform)
        {
            using var wavePen = new Pen(StudioTheme.Muted);
            for (var x = PaddingX; x < Width - PaddingX; x++)
            {
                var first = Math.Max(0, (int)(Time(x) / waveform.Step));
                var last = Math.Min(waveform.Peaks.Length - 1, (int)(Time(x + 1) / waveform.Step));
                var peak = 0f;
                for (var i = first; i <= last; i++) peak = Math.Max(peak, waveform.Peaks[i]);
                var height = Math.Max(1, (int)(Math.Min(1, peak) * 19));
                g.DrawLine(wavePen, x, Center - height, x, Center + height);
            }
        }
        if (Bpm > 0)
        {
            var beat = 60 / Bpm;
            var stride = Math.Max(1, (int)Math.Ceiling(viewLength / beat / Math.Max(1, TrackWidth / 12)));
            using var beatPen = new Pen(SnapToBeat ? StudioTheme.Accent : StudioTheme.Muted);
            for (var n = Math.Ceiling((viewStart - BeatOffset) / beat / stride) * stride;
                 BeatOffset + n * beat <= viewStart + viewLength; n += stride)
                g.DrawLine(beatPen, X(BeatOffset + n * beat), Center + 22, X(BeatOffset + n * beat), Center + 28);
        }
        DrawBoundary(start, StartGrip, startBrush, "[");
        DrawBoundary(end, EndGrip, endBrush, "]");
        if (IsTimeVisible(position))
        {
            var x = X(position);
            g.DrawLine(playPen, x, Center - 3, x, Center + 20);
            g.FillPolygon(Brushes.White, new Point[] { new Point(x, Center + 3), new Point(x - 6, Center + 13), new Point(x + 6, Center + 13) });
        }
        g.FillRectangle(baseBrush, PaddingX, OverviewY, TrackWidth, 8);
        g.FillRectangle(selectedBrush, OverviewX(start), OverviewY, Math.Max(1, OverviewX(end) - OverviewX(start)), 8);
        g.DrawRectangle(overviewPen, OverviewX(viewStart), OverviewY - 2,
            Math.Max(3, OverviewX(viewStart + viewLength) - OverviewX(viewStart)), 12);
        g.DrawLine(Pens.White, OverviewX(position), OverviewY - 3, OverviewX(position), OverviewY + 11);

        if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Width - 3, Height - 3), StudioTheme.Accent, BackColor);

        void DrawBoundary(double time, Rectangle grip, Brush brush, string label)
        {
            if (!IsTimeVisible(time)) return;
            g.FillRectangle(brush, grip);
            TextRenderer.DrawText(g, label, Font, grip, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using var pen = new Pen(brush, 2);
            g.DrawLine(pen, X(time), Center - 2, X(time), Center + 8);
        }
    }

    private static double TickStep(double desired)
    {
        var scale = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(0.001, desired))));
        return new[] { 1d, 2, 5, 10 }.First(n => n * scale >= desired) * scale;
    }
    private static string TickLabel(double time, double step)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, time));
        var prefix = time >= 3600 ? $"{(int)span.TotalHours}:{span.Minutes:00}" : $"{(int)span.TotalMinutes}";
        return prefix + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture) +
            (step < 1 ? "." + span.Milliseconds.ToString("000", CultureInfo.InvariantCulture) : "");
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        if (dragging != DragTarget.None) return;
        if ((ModifierKeys & Keys.Shift) != 0)
            SetView(viewStart - e.Delta / 120d * viewLength * 0.15, viewLength);
        else
        {
            var fraction = Math.Clamp((double)(e.X - PaddingX) / TrackWidth, 0, 1);
            var anchor = viewStart + fraction * viewLength;
            var length = Math.Clamp(viewLength * Math.Pow(1.4, -e.Delta / 120d), Math.Min(0.5, duration), duration);
            SetView(anchor - fraction * length, length);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        lastMouseX = e.X;
        if (e.Button is MouseButtons.Middle or MouseButtons.Right) dragging = DragTarget.Pan;
        else if (e.Button != MouseButtons.Left) return;
        else if (e.Y >= OverviewY - 5) dragging = DragTarget.Overview;
        else
        {
            var hitStart = IsTimeVisible(start) && (StartGrip.Contains(e.Location) || e.Y <= Center + 8 && Math.Abs(e.X - X(start)) <= 8);
            var hitEnd = IsTimeVisible(end) && (EndGrip.Contains(e.Location) || e.Y <= Center + 8 && Math.Abs(e.X - X(end)) <= 8);
            if (hitStart && hitEnd)
                dragging = e.X <= (X(start) + X(end)) / 2d ? DragTarget.Start : DragTarget.End;
            else if (hitStart) dragging = DragTarget.Start;
            else if (hitEnd) dragging = DragTarget.End;
            else dragging = DragTarget.Play;
        }
        Capture = true;
        dragOffsetX = dragging == DragTarget.Start ? e.X - X(start) : dragging == DragTarget.End ? e.X - X(end) : 0;
        if (dragging != DragTarget.Pan) MoveHandle(e.X);
        edgeScroll.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging == DragTarget.Pan) SetView(viewStart - (e.X - lastMouseX) * viewLength / TrackWidth, viewLength);
        else if (dragging != DragTarget.None) MoveHandle(e.X);
        lastMouseX = e.X;
        Cursor = dragging == DragTarget.Pan ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        StopDragging();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) StopDragging();
    }
    private void StopDragging()
    {
        dragging = DragTarget.None;
        edgeScroll.Stop();
        Cursor = Cursors.Hand;
    }
    private void MoveHandle(int x)
    {
        var time = Time(x - dragOffsetX);
        switch (dragging)
        {
            case DragTarget.Start: Start = time; break;
            case DragTarget.End: End = time; break;
            case DragTarget.Play: time = Snap(time); Position = time; SeekRequested?.Invoke(time); break;
            case DragTarget.Overview: SetView((double)(x - PaddingX) / TrackWidth * duration - viewLength / 2, viewLength); break;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) edgeScroll.Dispose();
        base.Dispose(disposing);
    }
}
