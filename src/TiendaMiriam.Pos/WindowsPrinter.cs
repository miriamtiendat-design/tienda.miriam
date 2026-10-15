using System.Drawing;
using System.Drawing.Printing;

namespace TiendaMiriam.Pos;

internal static class WindowsPrinter
{
    public static void Print(string? printerName, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return;
        }

        using var document = new PrintDocument();
        document.PrintController = new StandardPrintController();
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidPrinterException(document.PrinterSettings);
        }

        var lineIndex = 0;
        using var font = new Font("Consolas", 9);
        document.PrintPage += (_, args) =>
        {
            var graphics = args.Graphics
                ?? throw new InvalidOperationException("El controlador de impresión no proporcionó una superficie gráfica.");
            var y = (float)args.MarginBounds.Top;
            var lineHeight = font.GetHeight(graphics);
            while (lineIndex < lines.Count && y + lineHeight <= args.MarginBounds.Bottom)
            {
                graphics.DrawString(lines[lineIndex++], font, Brushes.Black,
                    new RectangleF(args.MarginBounds.Left, y, args.MarginBounds.Width, lineHeight + 2));
                y += lineHeight + 2;
            }
            args.HasMorePages = lineIndex < lines.Count;
        };
        document.Print();
    }
}
