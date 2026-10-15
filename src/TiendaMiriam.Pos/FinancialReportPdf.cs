using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Pos;

internal static class FinancialReportPdf
{
    private const int PageWidth = 595;
    private const int PageHeight = 842;

    public static byte[] Create(
        ReportSummaryResponse report,
        byte[]? logoBytes,
        IReadOnlyList<byte[]>? chartImages = null,
        bool chartsLimitedTo90Days = false)
    {
        var image = logoBytes is { Length: > 0 } ? CreateImage(logoBytes) : null;
        var charts = chartImages?.Where(bytes => bytes.Length > 0).Select(CreateImage).ToArray() ?? [];
        var content = CreateContent(report, image);
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii(image is null
                ? "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"
                : "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> /XObject << /Im0 5 0 R >> >> /Contents 6 0 R >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        };

        if (image is not null)
        {
            objects.Add(CreateStreamObject(
                $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {image.CompressedPixels.Length} >>",
                image.CompressedPixels));
        }
        objects.Add(CreateStreamObject($"<< /Length {content.Length} >>", content));
        if (charts.Length > 0)
        {
            var chartObjectIds = new int[charts.Length];
            for (var index = 0; index < charts.Length; index++)
            {
                chartObjectIds[index] = objects.Count + 1;
                objects.Add(CreateImageObject(charts[index]));
            }

            var chartContent = CreateChartPageContent(charts, chartsLimitedTo90Days);
            var chartContentId = objects.Count + 1;
            objects.Add(CreateStreamObject($"<< /Length {chartContent.Length} >>", chartContent));
            var chartPageId = objects.Count + 1;
            var xObjects = string.Join(" ", chartObjectIds.Select((id, index) => $"/Chart{index} {id} 0 R"));
            objects.Add(Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> /XObject << {xObjects} >> >> /Contents {chartContentId} 0 R >>"));
            objects[1] = Ascii($"<< /Type /Pages /Kids [3 0 R {chartPageId} 0 R] /Count 2 >>");
        }
        return Assemble(objects);
    }

    private static byte[] CreateChartPageContent(IReadOnlyList<PdfImage> charts, bool chartsLimitedTo90Days)
    {
        var commands = new StringBuilder();
        commands.AppendLine("q 0.91 0.29 0.12 rg 0 760 595 82 re f Q");
        AddText(commands, "TIENDA MIRIAM", 42, 808, 18, "1 1 1");
        AddText(commands, "ANÁLISIS VISUAL DEL PERÍODO", 42, 786, 10, "1 1 1");
        if (chartsLimitedTo90Days)
        {
            AddText(commands, "Gráficos limitados a los últimos 90 días del período.", 42, 768, 8, "1 1 1");
        }

        const float maxWidth = 245;
        const float maxHeight = 124;
        for (var index = 0; index < charts.Count; index++)
        {
            var image = charts[index];
            var scale = Math.Min(maxWidth / image.Width, maxHeight / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            var x = index % 2 == 0 ? 42 : 308;
            var top = 716 - (index / 2) * 168;
            var y = top - height;
            commands.AppendLine(FormattableString.Invariant(
                $"q {width:0.##} 0 0 {height:0.##} {x} {y:0.##} cm /Chart{index} Do Q"));
        }
        AddText(commands, "Gráficos incluidos con los datos del período seleccionado.", 42, 48, 9, "0.38 0.43 0.48");
        return Ascii(commands.ToString());
    }

    private static byte[] CreateImageObject(PdfImage image) =>
        CreateStreamObject(
            $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {image.CompressedPixels.Length} >>",
            image.CompressedPixels);

    private static byte[] CreateContent(ReportSummaryResponse report, PdfImage? image)
    {
        var commands = new StringBuilder();
        commands.AppendLine("q 0.91 0.29 0.12 rg 0 760 595 82 re f Q");
        if (image is null)
        {
            commands.AppendLine("q 1 1 1 rg 42 779 54 44 re f Q");
            AddText(commands, "M", 57, 790, 28, "0.91 0.29 0.12");
        }
        else
        {
            const float maxWidth = 76;
            const float maxHeight = 58;
            var scale = Math.Min(maxWidth / image.Width, maxHeight / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            commands.AppendLine(
                FormattableString.Invariant($"q {width:0.##} 0 0 {height:0.##} 42 {779 + (maxHeight - height) / 2:0.##} cm /Im0 Do Q"));
        }

        AddText(commands, "TIENDA MIRIAM", 136, 811, 20, "1 1 1");
        AddText(commands, "REPORTE FINANCIERO", 136, 789, 10, "1 1 1");
        AddText(commands,
            $"Período: {report.From.ToLocalTime():dd/MM/yyyy HH:mm} — {report.To.ToLocalTime():dd/MM/yyyy HH:mm}",
            42, 730, 11, "0.18 0.22 0.27");
        commands.AppendLine("q 0.82 0.85 0.88 RG 42 716 m 553 716 l S Q");
        AddText(commands, "RESUMEN FINANCIERO", 42, 688, 13, "0.72 0.25 0.11");

        var rows = new (string Label, string Value)[]
        {
            ("Ventas completadas", report.CompletedSales.ToString()),
            ("Ingresos totales", PosFormatting.Money(report.Revenue)),
            ("Costo de ventas", PosFormatting.Money(report.CostOfGoods)),
            ("Ganancia bruta", report.GrossProfit is decimal grossProfit
                ? PosFormatting.Money(grossProfit)
                : "No disponible: costos históricos incompletos"),
            ("Gastos operativos", PosFormatting.Money(report.Expenses)),
            ("Inversión en compras recibidas", PosFormatting.Money(report.PurchaseInvestment)),
            ("Utilidad neta", report.NetProfit is decimal netProfit
                ? PosFormatting.Money(netProfit)
                : "No disponible: costos históricos incompletos"),
            ("Ventas con costos incompletos", report.SalesWithoutCostSnapshot.ToString())
        };

        var y = 648;
        foreach (var (label, value) in rows)
        {
            AddText(commands, label, 48, y, 11, "0.16 0.20 0.24");
            AddText(commands, value, 315, y, 11, "0.16 0.20 0.24");
            commands.AppendLine(FormattableString.Invariant($"q 0.88 0.90 0.92 RG 48 {y - 12} m 547 {y - 12} l S Q"));
            y -= 43;
        }

        AddText(commands, $"Generado: {DateTime.Now:dd/MM/yyyy HH:mm}", 42, 54, 9, "0.38 0.43 0.48");
        return Ascii(commands.ToString());
    }

    private static void AddText(StringBuilder commands, string text, int x, int y, int size, string color)
    {
        var hex = Convert.ToHexString(ToWinAnsi(text));
        commands.AppendLine(FormattableString.Invariant(
            $"BT /F1 {size} Tf {color} rg {x} {y} Td <{hex}> Tj ET"));
    }

    private static byte[] ToWinAnsi(string text)
    {
        var bytes = new byte[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            bytes[index] = character <= byte.MaxValue ? (byte)character : (byte)'?';
        }
        return bytes;
    }

    private static PdfImage CreateImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var source = Image.FromStream(stream);
        const int maxWidth = 420;
        const int maxHeight = 160;
        var scale = Math.Min(1d, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var rectangle = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] rgb;
        try
        {
            var stride = Math.Abs(bitmapData.Stride);
            var row = new byte[stride];
            rgb = new byte[checked(width * height * 3)];
            for (var y = 0; y < height; y++)
            {
                var sourceRow = bitmapData.Stride >= 0 ? y : height - 1 - y;
                Marshal.Copy(IntPtr.Add(bitmapData.Scan0, sourceRow * stride), row, 0, stride);
                for (var x = 0; x < width; x++)
                {
                    var sourceIndex = x * 3;
                    var targetIndex = (y * width + x) * 3;
                    rgb[targetIndex] = row[sourceIndex + 2];
                    rgb[targetIndex + 1] = row[sourceIndex + 1];
                    rgb[targetIndex + 2] = row[sourceIndex];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rgb);
        }
        return new PdfImage(width, height, compressed.ToArray());
    }

    private static byte[] CreateStreamObject(string dictionary, byte[] stream)
    {
        using var result = new MemoryStream();
        var header = Ascii(dictionary + "\nstream\n");
        result.Write(header);
        result.Write(stream);
        result.Write(Ascii("\nendstream"));
        return result.ToArray();
    }

    private static byte[] Assemble(IReadOnlyList<byte[]> objects)
    {
        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));
        var offsets = new long[objects.Count + 1];
        for (var index = 0; index < objects.Count; index++)
        {
            offsets[index + 1] = output.Position;
            output.Write(Ascii($"{index + 1} 0 obj\n"));
            output.Write(objects[index]);
            output.Write(Ascii("\nendobj\n"));
        }

        var xrefOffset = output.Position;
        output.Write(Ascii($"xref\n0 {offsets.Length}\n"));
        output.Write(Ascii("0000000000 65535 f \n"));
        for (var index = 1; index < offsets.Length; index++)
        {
            output.Write(Ascii($"{offsets[index]:D10} 00000 n \n"));
        }
        output.Write(Ascii($"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n"));
        return output.ToArray();
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private sealed record PdfImage(int Width, int Height, byte[] CompressedPixels);
}
