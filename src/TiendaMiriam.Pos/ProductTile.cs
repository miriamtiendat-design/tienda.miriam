using System.Drawing.Drawing2D;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Pos;

internal sealed class ProductTile : Button
{
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 16 };
    private readonly Product _product;
    private readonly Image? _photo;
    private readonly Color _tileColor;
    private readonly string _stockLabel;
    private readonly bool _animationsEnabled;
    private float _hoverProgress;
    private float _hoverTarget;
    private DateTime _popStartedAtUtc;
    private float _popProgress;

    public event EventHandler? ColorRequested;

    public bool IsSelected;
    public Guid ProductId => _product.Id;

    public ProductTile(
        Product product,
        int width,
        int height,
        byte[]? photo,
        Color tileColor,
        string stockLabel,
        bool animationsEnabled = true)
    {
        _product = product;
        _tileColor = tileColor;
        _stockLabel = stockLabel;
        _animationsEnabled = animationsEnabled;
        Size = new Size(width, height);
        MinimumSize = Size;
        MaximumSize = Size;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        FlatAppearance.BorderSize = 2;
        FlatAppearance.BorderColor = ModernTheme.Orange;
        FlatAppearance.MouseOverBackColor = ControlPaint.Light(tileColor, 0.08f);
        FlatAppearance.MouseDownBackColor = ControlPaint.Dark(tileColor, 0.08f);
        BackColor = tileColor;
        ForeColor = ModernTheme.Ink;
        Cursor = Cursors.Hand;
        Text = string.Empty;
        AccessibleName = $"{product.Name}, {PosFormatting.Money(product.Price)}, {stockLabel}. Tocar para agregar.";
        AccessibleDescription = "Clic izquierdo agrega el producto. El menú contextual permite cambiar su color.";
        if (photo is not null)
        {
            using var stream = new MemoryStream(photo);
            using var source = Image.FromStream(stream);
            _photo = new Bitmap(source);
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Cambiar color de tarjeta", null, (_, _) => ColorRequested?.Invoke(this, EventArgs.Empty));
        ContextMenuStrip = menu;
        Region = CreateRoundedRegion(ClientSize);
        SizeChanged += (_, _) =>
        {
            Region?.Dispose();
            Region = CreateRoundedRegion(ClientSize);
        };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverProgress += (_hoverTarget - _hoverProgress) * 0.35f;
            if (Math.Abs(_hoverTarget - _hoverProgress) < 0.015f)
            {
                _hoverProgress = _hoverTarget;
            }
            _popProgress = _animationsEnabled
                ? Math.Clamp((float)(DateTime.UtcNow - _popStartedAtUtc).TotalMilliseconds / 260f, 0f, 1f)
                : 1f;
            if (_popProgress >= 1f && Math.Abs(_hoverTarget - _hoverProgress) < 0.015f)
            {
                _hoverTimer.Stop();
            }
            Invalidate();
        };
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (!_animationsEnabled)
        {
            _hoverProgress = 0f;
            _hoverTarget = 0f;
            Invalidate();
            base.OnMouseEnter(e);
            return;
        }
        _hoverTarget = 1f;
        _hoverTimer.Start();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_animationsEnabled)
        {
            _hoverProgress = 0f;
            _hoverTarget = 0f;
            Invalidate();
            base.OnMouseLeave(e);
            return;
        }
        _hoverTarget = 0f;
        _hoverTimer.Start();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        var popScale = _animationsEnabled && _popProgress < 1f
            ? 1f + 0.07f * MathF.Sin(_popProgress * MathF.PI)
            : 1f;
        if (popScale > 1f)
        {
            graphics.TranslateTransform(Width / 2f, Height / 2f);
            graphics.ScaleTransform(popScale, popScale);
            graphics.TranslateTransform(-Width / 2f, -Height / 2f);
        }
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(_tileColor);
        var bounds = ClientRectangle;
        bounds.Inflate(-3, -3);
        bounds.Offset(0, -(int)Math.Round(3 * _hoverProgress));
        using var cardPath = CreateRoundedPath(bounds, 12);
        graphics.SetClip(cardPath);
        if (_photo is not null)
        {
            DrawCover(graphics, _photo, bounds);
        }
        else
        {
            using var brush = new LinearGradientBrush(
                bounds,
                ControlPaint.Light(_tileColor, 0.18f),
                ControlPaint.Dark(_tileColor, 0.22f),
                45f);
            graphics.FillRectangle(brush, bounds);
            var initials = string.Concat(_product.Name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(word => char.ToUpperInvariant(word[0])));
            using var initialsFont = new Font("Segoe UI", Math.Max(12, Math.Min(34, Height / 3f)),
                FontStyle.Bold, GraphicsUnit.Pixel);
            using var initialsBrush = new SolidBrush(Color.White);
            var initialSize = graphics.MeasureString(initials, initialsFont);
            graphics.DrawString(initials, initialsFont, initialsBrush,
                Math.Max(4, (Width - initialSize.Width) / 2), Math.Max(4, (Height * 0.25f - initialSize.Height) / 2));
        }

        var overlayHeight = Math.Clamp(Height * 0.58f, 48, 100);
        var overlayHeightPixels = (int)overlayHeight;
        var overlay = new Rectangle(bounds.Left, Math.Max(bounds.Top, bounds.Bottom - overlayHeightPixels),
            bounds.Width, Math.Min(overlayHeightPixels, bounds.Height));
        using (var shade = new SolidBrush(Color.FromArgb(238, ModernTheme.Surface)))
        {
            graphics.FillRectangle(shade, overlay);
        }
        var compact = Height < 95 || Width < 92;
        using var nameFont = new Font("Segoe UI", compact ? 8f : Math.Clamp(Height / 10f, 9f, 14f),
            FontStyle.Bold, GraphicsUnit.Point);
        using var stockFont = new Font("Segoe UI", compact ? 7f : Math.Clamp(Height / 13f, 8f, 11f),
            FontStyle.Bold, GraphicsUnit.Point);
        using var priceFont = new Font("Segoe UI", compact ? 9f : Math.Clamp(Height / 8f, 10f, 18f),
            FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(ModernTheme.Ink);
        var horizontalInset = compact ? 3 : 7;
        var nameBounds = new RectangleF(overlay.Left + horizontalInset, overlay.Top + 3,
            Math.Max(1, overlay.Width - horizontalInset * 2), Math.Max(1, overlay.Height * 0.34f));
        var stockBounds = new RectangleF(overlay.Left + horizontalInset, overlay.Top + overlay.Height * 0.36f,
            Math.Max(1, overlay.Width - horizontalInset * 2), Math.Max(1, overlay.Height * 0.25f));
        var priceBounds = new RectangleF(overlay.Left + horizontalInset, overlay.Top + overlay.Height * 0.64f,
            Math.Max(1, overlay.Width - horizontalInset * 2), Math.Max(1, overlay.Height * 0.33f));
        graphics.DrawString(_product.Name, nameFont, textBrush, nameBounds,
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        var stockColor = _stockLabel == "AGOTADO"
            ? ModernTheme.Danger
            : ModernTheme.Green;
        using (var stockBrush = new SolidBrush(stockColor))
        {
            graphics.DrawString(_stockLabel, stockFont, stockBrush, stockBounds,
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }
        using var priceBrush = new SolidBrush(ModernTheme.OrangeDark);
        graphics.DrawString(PosFormatting.Money(_product.Price), priceFont,
            priceBrush, priceBounds,
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

        using var border = new Pen(
            IsSelected ? ModernTheme.Blue : Color.FromArgb((int)(110 + 130 * _hoverProgress), ModernTheme.Border),
            IsSelected ? 3.5f : 1.4f + _hoverProgress * 0.7f);
        graphics.DrawPath(border, cardPath);
        if (_hoverProgress > 0.02f)
        {
            using var glow = new Pen(Color.FromArgb((int)(22 + 38 * _hoverProgress), ModernTheme.Blue), 4f);
            graphics.DrawPath(glow, cardPath);
        }
        graphics.ResetTransform();
    }

    public void TriggerPop()
    {
        if (!_animationsEnabled || IsDisposed)
        {
            return;
        }
        _popStartedAtUtc = DateTime.UtcNow;
        _popProgress = 0f;
        _hoverTimer.Start();
    }

    private static Region CreateRoundedRegion(Size size)
    {
        using var path = CreateRoundedPath(new Rectangle(Point.Empty, size), 14);
        return new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawCover(Graphics graphics, Image image, Rectangle bounds)
    {
        var scale = Math.Max(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
        var width = (int)Math.Ceiling(image.Width * scale);
        var height = (int)Math.Ceiling(image.Height * scale);
        var destination = new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
        var state = graphics.Save();
        graphics.SetClip(bounds);
        graphics.DrawImage(image, destination);
        graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            Region?.Dispose();
            _photo?.Dispose();
        }
        base.Dispose(disposing);
    }
}
