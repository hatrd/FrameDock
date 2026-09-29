namespace FrameDock;

internal sealed class RangeBar : Control
{
    private enum DragTarget { None, Play, Start, End }
    private DragTarget dragging;
    private double duration = 1;
    private double position;
    private double start;
    private double end = 1;

    public event Action<double>? SeekRequested;
    public event Action? RangeChanged;

    public double Duration
    {
        get => duration;
        set { duration = Math.Max(0.001, value); start = 0; end = duration; position = 0; Invalidate(); RangeChanged?.Invoke(); }
    }
    public double Position { get => position; set { position = Math.Clamp(value, 0, duration); Invalidate(); } }
    public double Start { get => start; set { start = Math.Clamp(value, 0, end - 0.001); Invalidate(); RangeChanged?.Invoke(); } }
    public double End { get => end; set { end = Math.Clamp(value, start + 0.001, duration); Invalidate(); RangeChanged?.Invoke(); } }

    public RangeBar()
    {
        DoubleBuffered = true;
        Height = 56;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(24, 27, 32);
    }

    private int X(double seconds) => 12 + (int)Math.Round((Width - 24) * seconds / duration);
    private double Time(int x) => Math.Clamp((double)(x - 12) / Math.Max(1, Width - 24) * duration, 0, duration);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        var center = Height / 2;
        using var baseBrush = new SolidBrush(Color.FromArgb(67, 73, 82));
        using var selectedBrush = new SolidBrush(Color.FromArgb(58, 146, 225));
        using var startPen = new Pen(Color.FromArgb(101, 220, 167), 3);
        using var endPen = new Pen(Color.FromArgb(250, 169, 89), 3);
        using var playPen = new Pen(Color.White, 2);
        g.FillRectangle(baseBrush, 12, center - 5, Math.Max(0, Width - 24), 10);
        g.FillRectangle(selectedBrush, X(start), center - 5, Math.Max(1, X(end) - X(start)), 10);
        g.DrawLine(startPen, X(start), center - 16, X(start), center + 16);
        g.DrawLine(endPen, X(end), center - 16, X(end), center + 16);
        g.DrawLine(playPen, X(position), center - 22, X(position), center + 22);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var distanceStart = Math.Abs(e.X - X(start));
        var distanceEnd = Math.Abs(e.X - X(end));
        var distancePlay = Math.Abs(e.X - X(position));
        if (Math.Min(distanceStart, distanceEnd) <= 11 && Math.Min(distanceStart, distanceEnd) < distancePlay)
            dragging = distanceStart <= distanceEnd ? DragTarget.Start : DragTarget.End;
        else dragging = DragTarget.Play;
        Capture = true;
        MoveHandle(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging != DragTarget.None) MoveHandle(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = DragTarget.None;
        Capture = false;
    }

    private void MoveHandle(int x)
    {
        var time = Time(x);
        switch (dragging)
        {
            case DragTarget.Start: Start = Math.Min(time, end - 0.001); break;
            case DragTarget.End: End = Math.Max(time, start + 0.001); break;
            case DragTarget.Play: Position = time; SeekRequested?.Invoke(time); break;
        }
    }
}
