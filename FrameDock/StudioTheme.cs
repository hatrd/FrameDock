using System.Runtime.InteropServices;

namespace FrameDock;

// One restrained palette for the editing surface, native controls and secondary windows.
internal static class StudioTheme
{
    public static readonly Color Canvas = Color.FromArgb(26, 30, 36);
    public static readonly Color Surface = Color.FromArgb(35, 41, 49);
    public static readonly Color Raised = Color.FromArgb(45, 53, 63);
    public static readonly Color Border = Color.FromArgb(67, 79, 92);
    public static readonly Color Text = Color.FromArgb(235, 240, 245);
    public static readonly Color Muted = Color.FromArgb(166, 180, 195);
    public static readonly Color Accent = Color.FromArgb(133, 198, 213);
    public static readonly Color Selection = Color.FromArgb(48, 77, 91);
    public static readonly Color Start = Color.FromArgb(122, 214, 173);
    public static readonly Color End = Color.FromArgb(239, 185, 126);
    public static readonly Font BodyFont = new("Microsoft YaHei UI", 9f);
    public static readonly Font TitleFont = new("Bahnschrift", 19f, FontStyle.Bold);
    public static readonly Font EmptyStateFont = new("Microsoft YaHei UI", 18f);
    public static readonly Font SectionFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    public static readonly Font TimeFont = new("Consolas", 13f);

    public static void StyleButton(Button button, Color? accent = null, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? accent ?? Accent : Raised;
        button.ForeColor = primary ? Canvas : accent ?? Text;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = accent ?? Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(159, 214, 226) : Selection;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(108, 176, 193) : Border;
    }

    public static void StyleToggle(CheckBox toggle)
    {
        toggle.FlatAppearance.BorderColor = Border;
        toggle.FlatAppearance.CheckedBackColor = Selection;
        toggle.BackColor = Raised;
        toggle.ForeColor = Text;
        toggle.Padding = new Padding(8, 4, 8, 4);
        toggle.Margin = new Padding(0, 0, 6, 6);
        toggle.MinimumSize = new Size(0, 32);
    }

    public static void StyleInput(Control input)
    {
        input.BackColor = Raised;
        input.ForeColor = Text;
        input.Margin = new Padding(0, 2, 0, 5);
        if (input is ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = 22;
            combo.DrawItem += (_, e) =>
            {
                var selected = (e.State & DrawItemState.Selected) != 0;
                using var background = new SolidBrush(selected ? Selection : Raised);
                e.Graphics.FillRectangle(background, e.Bounds);
                var text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
                TextRenderer.DrawText(e.Graphics, text, e.Font,
                    new Rectangle(e.Bounds.X + 5, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height), Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
        }
        if (input is NumericUpDown number) number.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void ApplyTitleBar(Form form)
    {
        // Let Windows retain native resize, snap and accessibility behavior while matching the workspace.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var dark = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
