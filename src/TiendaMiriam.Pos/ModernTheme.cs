namespace TiendaMiriam.Pos;

internal static class ModernTheme
{
    public static readonly Color Canvas = Color.FromArgb(9, 11, 18);
    public static readonly Color Surface = Color.FromArgb(22, 26, 39);
    public static readonly Color SurfaceMuted = Color.FromArgb(31, 37, 54);
    public static readonly Color Border = Color.FromArgb(55, 64, 86);
    public static readonly Color Ink = Color.FromArgb(237, 241, 250);
    public static readonly Color Muted = Color.FromArgb(154, 165, 187);
    public static readonly Color Orange = Color.FromArgb(231, 132, 72);
    public static readonly Color OrangeDark = Color.FromArgb(255, 178, 111);
    public static readonly Color Red = Color.FromArgb(251, 113, 133);
    public static readonly Color Blue = Color.FromArgb(129, 155, 255);
    public static readonly Color Green = Color.FromArgb(72, 214, 160);
    public static readonly Color Purple = Color.FromArgb(179, 154, 255);
    public static readonly Color Danger = Color.FromArgb(251, 113, 133);
    public static readonly Color Success = Color.FromArgb(72, 214, 160);

    public static void StyleButton(Button button, bool primary = false)
    {
        StyleButton(button,
            primary ? Orange : SurfaceMuted,
            primary ? Color.White : Ink,
            primary ? OrangeDark : Border);
    }

    public static void StyleButton(Button button, Color background, Color foreground, Color? border = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border ?? background;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(background, 0.08f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(background, 0.08f);
        button.Cursor = Cursors.Hand;
    }

    public static void StyleInput(Control control)
    {
        control.BackColor = Surface;
        control.ForeColor = Ink;
        if (control is TextBoxBase textBox)
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }
        if (control is ComboBox comboBox)
        {
            comboBox.FlatStyle = FlatStyle.Flat;
        }
    }
}
