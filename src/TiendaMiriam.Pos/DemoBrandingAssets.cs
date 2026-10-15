using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal static class DemoBrandingAssets
{
    public static async Task EnsureLocalAssetsAsync(TiendaDbContext db)
    {
        var logo = await db.BusinessAssets.SingleOrDefaultAsync(asset => asset.Key == DemoDataSeeder.LogoAssetKey);
        if (logo is null)
        {
            db.BusinessAssets.Add(new BusinessAsset
            {
                Key = DemoDataSeeder.LogoAssetKey,
                ContentType = "image/png",
                Data = CreateLogoPng(),
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else if (logo.ContentType == "image/svg+xml")
        {
            logo.ContentType = "image/png";
            logo.Data = CreateLogoPng();
            logo.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (await db.BusinessAssets.AnyAsync(asset => asset.Key == DemoDataSeeder.SeedMarker))
        {
            var demoSkus = new[] { "BEB-001", "BEB-002", "ASE-001", "ASE-002", "COM-001", "COM-002", "COM-003" };
            foreach (var product in await db.Products.AsNoTracking()
                         .Where(product => demoSkus.Contains(product.Sku)).ToListAsync())
            {
                if (await db.ProductPhotos.AnyAsync(photo => photo.ProductId == product.Id))
                {
                    continue;
                }
                db.ProductPhotos.Add(new ProductPhoto
                {
                    ProductId = product.Id,
                    ContentType = "image/png",
                    Data = CreateProductPlaceholderPng(product.Name),
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync();
    }

    public static byte[] CreateLogoPng()
    {
        using var image = new Bitmap(520, 180);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(ModernTheme.Canvas);
        using var gradient = new LinearGradientBrush(
            new Rectangle(18, 18, 144, 144),
            ModernTheme.Orange,
            ModernTheme.Red,
            45f);
        graphics.FillRoundedRectangle(gradient, 18, 18, 144, 144, 30);
        using var markFont = new Font("Segoe UI", 44, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brandFont = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subtitleFont = new Font("Segoe UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var orange = new SolidBrush(ModernTheme.OrangeDark);
        using var ink = new SolidBrush(ModernTheme.Ink);
        using var muted = new SolidBrush(ModernTheme.Muted);
        graphics.DrawString("TM", markFont, white, new PointF(39, 54));
        graphics.DrawString("TIENDA", brandFont, orange, new PointF(185, 42));
        graphics.DrawString("MIRIAM", brandFont, ink, new PointF(185, 83));
        graphics.DrawString("SANTA CRUZ · BOLIVIA", subtitleFont, muted, new PointF(188, 131));
        return ToPng(image);
    }

    public static byte[] CreateProductPlaceholderPng(string name)
    {
        using var image = new Bitmap(320, 220);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var gradient = new LinearGradientBrush(
            new Rectangle(0, 0, image.Width, image.Height),
            ModernTheme.SurfaceMuted,
            ModernTheme.Canvas,
            35f);
        graphics.FillRectangle(gradient, 0, 0, image.Width, image.Height);
        using var accent = new SolidBrush(ModernTheme.Orange);
        graphics.FillEllipse(accent, 30, 18, 84, 84);
        using var markFont = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleFont = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionFont = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(ModernTheme.Ink);
        using var muted = new SolidBrush(ModernTheme.Muted);
        graphics.DrawString("TM", markFont, Brushes.White, new PointF(42, 35));
        var safeName = name.Length > 25 ? string.Concat(name.AsSpan(0, 22), "...") : name;
        graphics.DrawString(safeName, titleFont, ink, new RectangleF(18, 125, 284, 52));
        graphics.DrawString("IMAGEN DE DEMOSTRACIÓN", captionFont, muted, new PointF(18, 190));
        return ToPng(image);
    }

    private static byte[] ToPng(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static byte[] NormalizePng(Image image)
    {
        const int maximumWidth = 1600;
        const int maximumHeight = 1200;
        const long maximumPixelCount = 40_000_000;
        if (image.Width <= 0 || image.Height <= 0
            || (long)image.Width * image.Height > maximumPixelCount)
        {
            throw new InvalidOperationException("La imagen es demasiado grande. Usa una imagen de hasta 40 megapíxeles.");
        }

        var scale = Math.Min(1d, Math.Min(
            (double)maximumWidth / image.Width,
            (double)maximumHeight / image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(0, 0, width, height));
        }
        return ToPng(normalized);
    }

    public static byte[] NormalizePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        return NormalizePng(image);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(
        this Graphics graphics, Brush brush, int x, int y, int width, int height, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
