using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed record ReportChartPoint(string Label, decimal? Value);
internal sealed record ReportProductPoint(string Name, decimal Quantity);
internal sealed record ReportPaymentPoint(string Name, decimal Amount);

internal sealed record ReportVisualData(
    DateTime From,
    DateTime To,
    IReadOnlyList<ReportChartPoint> DailySales,
    IReadOnlyList<ReportChartPoint> DailyNetProfit,
    IReadOnlyList<ReportChartPoint> HourlySales,
    IReadOnlyList<ReportProductPoint> BestSellers,
    IReadOnlyList<ReportProductPoint> SlowestSellers,
    IReadOnlyList<ReportPaymentPoint> PaymentMethods,
    decimal Revenue,
    decimal Expenses);

internal static class ReportChartsPreferenceStore
{
    private const string PreferenceKey = "report-charts-visible";

    public static async Task<bool> LoadAsync(TiendaDbContext db, CancellationToken cancellationToken)
    {
        var setting = await db.BusinessAssets.AsNoTracking()
            .Where(asset => asset.Key == PreferenceKey)
            .Select(asset => asset.Data)
            .SingleOrDefaultAsync(cancellationToken);
        return setting is null || setting.Length == 0 || setting[0] != 0;
    }

    public static async Task SaveAsync(TiendaDbContext db, bool visible, CancellationToken cancellationToken)
    {
        var asset = await db.BusinessAssets.SingleOrDefaultAsync(
            item => item.Key == PreferenceKey, cancellationToken);
        if (asset is null)
        {
            db.BusinessAssets.Add(new BusinessAsset
            {
                Key = PreferenceKey,
                ContentType = "application/octet-stream",
                Data = [visible ? (byte)1 : (byte)0],
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            asset.ContentType = "application/octet-stream";
            asset.Data = [visible ? (byte)1 : (byte)0];
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class ReportVisualDataService
{
    public static async Task<ReportVisualData> LoadAsync(
        TiendaDbContext db,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (from >= to)
        {
            throw new InvalidOperationException("El rango debe ser creciente.");
        }

        var rangeStart = from.LocalDateTime;
        var rangeEnd = to.LocalDateTime;
        var chartStart = rangeEnd.Date.AddDays(-89);
        if (rangeStart.Date > chartStart)
        {
            chartStart = rangeStart.Date;
        }
        var effectiveFrom = rangeStart > chartStart ? rangeStart : chartStart;
        var effectiveTo = rangeEnd;
        var fromUtc = new DateTimeOffset(effectiveFrom).ToUniversalTime().UtcDateTime;
        var toUtc = to.UtcDateTime;
        var salesInRange = db.Sales.AsNoTracking()
            .Where(sale => sale.State == SaleState.Completed
                && sale.OccurredAtUtc >= fromUtc && sale.OccurredAtUtc < toUtc);

        var sales = await salesInRange
            .Select(sale => new
            {
                sale.Id,
                sale.OccurredAtUtc,
                sale.Total,
                Lines = sale.Lines.Select(line => new
                {
                    line.Id,
                    line.ProductId,
                    line.ProductName,
                    line.Quantity
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        var costSnapshots = await db.SaleCostSnapshots.AsNoTracking()
            .Where(snapshot => salesInRange.Any(sale => sale.Id == snapshot.SaleId))
            .Select(snapshot => new { snapshot.SaleId, snapshot.Cost })
            .ToListAsync(cancellationToken);
        var costedLineIds = await db.SaleLineFinancialSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.UnitCost != null
                && db.SaleLines.Any(line => line.Id == snapshot.SaleLineId
                    && salesInRange.Any(sale => sale.Id == line.SaleId)))
            .Select(snapshot => snapshot.SaleLineId)
            .ToListAsync(cancellationToken);
        var paymentMethods = await new PaymentReportService(db)
            .LoadAsync(new DateTimeOffset(effectiveFrom).ToUniversalTime(), to, cancellationToken);
        var expenses = await db.Expenses.AsNoTracking()
            .Where(expense => expense.OccurredAtUtc >= fromUtc && expense.OccurredAtUtc < toUtc)
            .Select(expense => new { expense.Amount, expense.OccurredAtUtc })
            .ToListAsync(cancellationToken);
        var products = await db.Products.AsNoTracking()
            .Where(product => product.IsActive)
            .Select(product => new { product.Id, product.Name })
            .ToListAsync(cancellationToken);

        var dates = Enumerable.Range(0, Math.Max(1, (rangeEnd.Date - chartStart).Days + 1))
            .Select(offset => chartStart.AddDays(offset))
            .ToArray();
        var salesByDay = sales.GroupBy(sale => DateTime.SpecifyKind(sale.OccurredAtUtc, DateTimeKind.Utc)
                .ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.Sum(sale => sale.Total));
        var expensesByDay = expenses.GroupBy(expense => DateTime.SpecifyKind(
                expense.OccurredAtUtc, DateTimeKind.Utc).ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.Sum(expense => expense.Amount));
        var costsBySale = costSnapshots.ToDictionary(snapshot => snapshot.SaleId, snapshot => snapshot.Cost);
        var costedLines = costedLineIds.ToHashSet();
        var netProfit = dates.Select(date =>
        {
            var daySales = sales.Where(sale => DateTime.SpecifyKind(
                sale.OccurredAtUtc, DateTimeKind.Utc).ToLocalTime().Date == date).ToArray();
            var missingCost = daySales.Any(sale =>
                !costsBySale.ContainsKey(sale.Id)
                || sale.Lines.Any(line => !costedLines.Contains(line.Id)));
            return new ReportChartPoint(
                date.ToString("ddd dd/MM", CultureInfo.GetCultureInfo("es-BO")),
                missingCost
                    ? null
                    : daySales.Sum(sale => sale.Total)
                        - daySales.Sum(sale => costsBySale.GetValueOrDefault(sale.Id))
                        - expensesByDay.GetValueOrDefault(date));
        }).ToArray();

        var productNames = products.ToDictionary(product => product.Id, product => product.Name);
        foreach (var line in sales.SelectMany(sale => sale.Lines))
        {
            productNames.TryAdd(line.ProductId, line.ProductName);
        }
        var quantities = sales.SelectMany(sale => sale.Lines)
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        var productTotals = productNames.Select(product =>
            new ReportProductPoint(product.Value, quantities.GetValueOrDefault(product.Key))).ToArray();
        var hourTotals = sales.GroupBy(sale =>
                DateTime.SpecifyKind(sale.OccurredAtUtc, DateTimeKind.Utc).ToLocalTime().Hour)
            .ToDictionary(group => group.Key, group => group.Sum(sale => sale.Total));

        return new ReportVisualData(
            chartStart,
            rangeEnd,
            dates.Select(date => new ReportChartPoint(
                date.ToString("ddd dd/MM", CultureInfo.GetCultureInfo("es-BO")),
                salesByDay.GetValueOrDefault(date))).ToArray(),
            netProfit,
            Enumerable.Range(8, 16).Select(hour => new ReportChartPoint(
                $"{hour:00}:00", hourTotals.GetValueOrDefault(hour))).ToArray(),
            productTotals.OrderByDescending(product => product.Quantity).ThenBy(product => product.Name)
                .Take(10).ToArray(),
            productTotals.OrderBy(product => product.Quantity).ThenBy(product => product.Name)
                .Take(10).ToArray(),
            paymentMethods.Select(point => new ReportPaymentPoint(point.Name, point.Amount)).ToArray(),
            sales.Sum(sale => sale.Total),
            expenses.Sum(expense => expense.Amount));
    }
}

internal enum ReportChartKind
{
    Line,
    Columns,
    HorizontalBars,
    Doughnut
}

internal sealed class ReportChartControl : Control
{
    private readonly ToolTip _toolTip = new() { AutomaticDelay = 200 };
    private readonly IReadOnlyList<ReportChartPoint> _points;
    private readonly IReadOnlyList<ReportProductPoint> _products;
    private readonly IReadOnlyList<ReportPaymentPoint> _payments;
    private readonly Color _accent;
    private readonly Color[] _palette =
    [
        Color.FromArgb(76, 210, 160), Color.FromArgb(246, 111, 130),
        Color.FromArgb(139, 167, 255), Color.FromArgb(183, 151, 255),
        Color.FromArgb(246, 178, 88)
    ];
    private readonly string _title;
    private readonly ReportChartKind _kind;
    private decimal _firstTotal;
    private decimal _secondTotal;

    public ReportChartControl(
        string title,
        ReportChartKind kind,
        IReadOnlyList<ReportChartPoint>? points = null,
        IReadOnlyList<ReportProductPoint>? products = null,
        IReadOnlyList<ReportPaymentPoint>? payments = null,
        Color? accent = null)
    {
        _title = title;
        _kind = kind;
        _points = points ?? [];
        _products = products ?? [];
        _payments = payments ?? [];
        _accent = accent ?? Color.FromArgb(139, 167, 255);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(22, 26, 39);
        ForeColor = Color.FromArgb(237, 241, 250);
        MinimumSize = new Size(320, 220);
        Size = new Size(560, 240);
        AccessibleName = title;
    }

    public void SetComparison(decimal first, decimal second)
    {
        _firstTotal = first;
        _secondTotal = second;
        Invalidate();
    }

    public byte[] CapturePng()
    {
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);
        using var border = new Pen(Color.FromArgb(53, 61, 82));
        using var card = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(card, ClientRectangle);
        e.Graphics.DrawRectangle(border, new Rectangle(0, 0, Width - 1, Height - 1));
        using var titleFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 7.5f);
        using var titleBrush = new SolidBrush(ForeColor);
        using var labelBrush = new SolidBrush(Color.FromArgb(154, 165, 187));
        e.Graphics.DrawString(_title, titleFont, titleBrush, 14, 12);
        var plot = new RectangleF(48, 45, Math.Max(1, Width - 66), Math.Max(1, Height - 88));

        switch (_kind)
        {
            case ReportChartKind.Line:
                DrawLine(e.Graphics, plot, labelFont, labelBrush);
                break;
            case ReportChartKind.Columns:
                DrawComparison(e.Graphics, plot, labelFont, labelBrush);
                break;
            case ReportChartKind.HorizontalBars:
                DrawHorizontalBars(e.Graphics, plot, labelFont, labelBrush);
                break;
            case ReportChartKind.Doughnut:
                DrawDoughnut(e.Graphics, plot, labelFont, labelBrush);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = _kind == ReportChartKind.Doughnut
            ? HoveredDoughnutIndex(e.Location)
            : HoveredIndex(e.Location);
        var text = _kind switch
        {
            ReportChartKind.Line or ReportChartKind.Columns when index >= 0 && index < _points.Count =>
                $"{_points[index].Label}: {(_points[index].Value is decimal value ? value.ToString("N2") + " Bs" : "N/D")}",
            ReportChartKind.HorizontalBars when index >= 0 && index < _products.Count =>
                $"{_products[index].Name}: {_products[index].Quantity.ToString("N2")} unidades",
            ReportChartKind.Doughnut when index >= 0 && index < _payments.Count =>
                $"{_payments[index].Name}: {_payments[index].Amount:N2} Bs",
            _ => string.Empty
        };
        if (text.Length > 0)
        {
            _toolTip.Show(text, this, e.Location.X + 12, e.Location.Y + 12, 1600);
        }
        else
        {
            _toolTip.Hide(this);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private int HoveredIndex(Point point)
    {
        var plotHeight = Height - 88;
        if (point.Y < 45 || point.Y > 45 + plotHeight)
        {
            return -1;
        }
        var count = _kind switch
        {
            ReportChartKind.Line or ReportChartKind.Columns => _points.Count,
            ReportChartKind.HorizontalBars => _products.Count,
            ReportChartKind.Doughnut => _payments.Count,
            _ => 0
        };
        if (count == 0)
        {
            return -1;
        }
        if (_kind == ReportChartKind.HorizontalBars)
        {
            return Math.Clamp((point.Y - 45) * count / Math.Max(1, plotHeight), 0, count - 1);
        }
        var width = Width - 66;
        return Math.Clamp((point.X - 48) * count / Math.Max(1, width), 0, count - 1);
    }

    private int HoveredDoughnutIndex(Point point)
    {
        if (_payments.Count == 0)
        {
            return -1;
        }
        var plot = new RectangleF(48, 45, Math.Max(1, Width - 66), Math.Max(1, Height - 88));
        var diameter = Math.Min(plot.Height - 8, plot.Width * 0.46f);
        var centerX = plot.Left + 8 + diameter / 2;
        var centerY = plot.Top + 4 + diameter / 2;
        var deltaX = point.X - centerX;
        var deltaY = point.Y - centerY;
        var radius = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (radius < diameter * 0.2f || radius > diameter / 2 || _payments.Sum(item => item.Amount) <= 0)
        {
            return -1;
        }
        var angle = (MathF.Atan2(deltaY, deltaX) * 180f / MathF.PI + 450f) % 360f;
        var accumulated = 0f;
        var total = _payments.Sum(item => item.Amount);
        for (var index = 0; index < _payments.Count; index++)
        {
            accumulated += (float)(_payments[index].Amount / total * 360m);
            if (angle < accumulated)
            {
                return index;
            }
        }
        return _payments.Count - 1;
    }

    private void DrawLine(Graphics graphics, RectangleF plot, Font font, Brush brush)
    {
        DrawGrid(graphics, plot, font, brush);
        var values = _points.Select(point => point.Value).ToArray();
        var known = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (known.Length == 0)
        {
            DrawEmpty(graphics, plot);
            return;
        }
        var minimum = Math.Min(0, known.Min());
        var maximum = Math.Max(1, known.Max());
        using var pen = new Pen(_accent, 2.4f) { LineJoin = LineJoin.Round };
        using var marker = new SolidBrush(_accent);
        var step = plot.Width / Math.Max(1, values.Length - 1);
        PointF? previous = null;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not decimal value)
            {
                previous = null;
                continue;
            }
            var location = new PointF(
                plot.Left + index * step,
                plot.Bottom -                 (float)((value - minimum) / (maximum - minimum) * (decimal)plot.Height));
            if (previous is PointF previousPoint)
            {
                graphics.DrawLine(pen, previousPoint, location);
            }
            graphics.FillEllipse(marker, location.X - 3.2f, location.Y - 3.2f, 6.4f, 6.4f);
            previous = location;
        }
        DrawXAxisLabels(graphics, plot, font, brush, _points.Select(point => point.Label).ToArray());
    }

    private void DrawComparison(Graphics graphics, RectangleF plot, Font font, Brush brush)
    {
        var max = Math.Max(1, Math.Max(_firstTotal, _secondTotal));
        using var green = new SolidBrush(Color.FromArgb(76, 210, 160));
        using var red = new SolidBrush(Color.FromArgb(246, 111, 130));
        using var label = new SolidBrush(ForeColor);
        DrawGrid(graphics, plot, font, brush);
        var barWidth = Math.Min(78, plot.Width / 4);
        var x = plot.Left + plot.Width / 2 - barWidth - 4;
        var firstHeight = (float)(_firstTotal / max * (decimal)plot.Height);
        var secondHeight = (float)(_secondTotal / max * (decimal)plot.Height);
        graphics.FillRectangle(green, new RectangleF(x, plot.Bottom - firstHeight, barWidth, Math.Max(2, firstHeight)));
        graphics.FillRectangle(red, new RectangleF(x + barWidth + 8, plot.Bottom - secondHeight, barWidth, Math.Max(2, secondHeight)));
        using var nameFont = new Font("Segoe UI", 8, FontStyle.Bold);
        graphics.DrawString("Ingresos", nameFont, green, x - 4, plot.Bottom + 7);
        graphics.DrawString("Gastos", nameFont, red, x + barWidth + 4, plot.Bottom + 7);
        graphics.DrawString($"{_firstTotal:N2} Bs", font, label, x - 10, Math.Max(plot.Top, plot.Bottom - firstHeight - 18));
        graphics.DrawString($"{_secondTotal:N2} Bs", font, label, x + barWidth, Math.Max(plot.Top, plot.Bottom - secondHeight - 18));
    }

    private void DrawHorizontalBars(Graphics graphics, RectangleF plot, Font font, Brush brush)
    {
        if (_products.Count == 0)
        {
            DrawEmpty(graphics, plot);
            return;
        }
        var values = _products.Select(product => product.Quantity).ToArray();
        var max = Math.Max(1, values.DefaultIfEmpty(0).Max());
        using var label = new SolidBrush(ForeColor);
        using var muted = new SolidBrush(Color.FromArgb(154, 165, 187));
        using var bar = new SolidBrush(_accent);
        var rowHeight = plot.Height / Math.Max(1, values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var y = plot.Top + index * rowHeight;
            var name = Truncate(_products[index].Name, 18);
            graphics.DrawString(name, font, muted, 8, y + 3);
            var width = (float)(values[index] / max * (decimal)(plot.Width - 54));
            graphics.FillRectangle(bar,
                new RectangleF(plot.Left, y + 2, Math.Max(2, width), Math.Max(4, rowHeight - 6)));
            graphics.DrawString(values[index].ToString("N2"), font, label,
                Math.Min(plot.Right - 32, plot.Left + width + 5), y + 3);
        }
    }

    private void DrawDoughnut(Graphics graphics, RectangleF plot, Font font, Brush brush)
    {
        if (_payments.Count == 0 || _payments.Sum(point => point.Amount) <= 0)
        {
            DrawEmpty(graphics, plot);
            return;
        }
        var total = _payments.Sum(point => point.Amount);
        var diameter = Math.Min(plot.Height - 8, plot.Width * 0.46f);
        var pie = new RectangleF(plot.Left + 8, plot.Top + 4, diameter, diameter);
        var angle = -90f;
        for (var index = 0; index < _payments.Count; index++)
        {
            var sweep = (float)(_payments[index].Amount / total * 360m);
            using var slice = new SolidBrush(_palette[index % _palette.Length]);
            graphics.FillPie(slice, pie, angle, sweep);
            angle += sweep;
        }
        using var hole = new SolidBrush(BackColor);
        var inset = diameter * 0.29f;
        graphics.FillEllipse(hole, pie.X + inset, pie.Y + inset, diameter - 2 * inset, diameter - 2 * inset);
        using var centerFont = new Font("Segoe UI", 8, FontStyle.Bold);
        using var white = new SolidBrush(ForeColor);
        graphics.DrawString($"{total:N0}\nBs", centerFont, white, pie.X + inset, pie.Y + diameter * 0.39f);
        var legendX = pie.Right + 18;
        using var fontBrush = new SolidBrush(Color.FromArgb(190, 199, 218));
        for (var index = 0; index < _payments.Count; index++)
        {
            var y = plot.Top + 10 + index * 26;
            using var swatch = new SolidBrush(_palette[index % _palette.Length]);
            graphics.FillEllipse(swatch, legendX, y + 2, 8, 8);
            graphics.DrawString($"{_payments[index].Name} · {_payments[index].Amount / total:P0}",
                font, fontBrush, legendX + 14, y);
        }
    }

    private static void DrawGrid(Graphics graphics, RectangleF plot, Font font, Brush brush)
    {
        using var pen = new Pen(Color.FromArgb(49, 58, 78));
        for (var index = 0; index < 4; index++)
        {
            var y = plot.Top + plot.Height * index / 3;
            graphics.DrawLine(pen, plot.Left, y, plot.Right, y);
        }
    }

    private static void DrawXAxisLabels(Graphics graphics, RectangleF plot, Font font, Brush brush, string[] labels)
    {
        if (labels.Length == 0)
        {
            return;
        }
        var count = Math.Min(6, labels.Length);
        for (var index = 0; index < count; index++)
        {
            var sourceIndex = count == 1 ? 0 : index * (labels.Length - 1) / (count - 1);
            var x = plot.Left + plot.Width * sourceIndex / Math.Max(1, labels.Length - 1);
            graphics.DrawString(Truncate(labels[sourceIndex], 10), font, brush, x - 20, plot.Bottom + 7);
        }
    }

    private static void DrawEmpty(Graphics graphics, RectangleF plot)
    {
        using var brush = new SolidBrush(Color.FromArgb(154, 165, 187));
        using var font = new Font("Segoe UI", 9);
        graphics.DrawString("Sin datos para el período", font, brush, plot.Left + 8, plot.Top + 20);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}

internal static class ReportExcelExporter
{
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ChartNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static byte[] Create(ReportSummaryResponse report, ReportVisualData data)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            AddXml(archive, "[Content_Types].xml", ContentTypes());
            AddXml(archive, "_rels/.rels", new XElement(PackageRelNs + "Relationships",
                Rel("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml")));
            AddXml(archive, "xl/workbook.xml", Workbook());
            AddXml(archive, "xl/_rels/workbook.xml.rels", WorkbookRels());
            AddXml(archive, "xl/worksheets/sheet1.xml", DataSheet(report, data));
            AddXml(archive, "xl/worksheets/sheet2.xml", ChartSheet());
            AddXml(archive, "xl/worksheets/_rels/sheet2.xml.rels", new XElement(PackageRelNs + "Relationships",
                Rel("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing", "../drawings/drawing1.xml")));
            AddXml(archive, "xl/drawings/drawing1.xml", Drawing());
            AddXml(archive, "xl/drawings/_rels/drawing1.xml.rels", DrawingRels());
            for (var index = 0; index < 7; index++)
            {
                AddXml(archive, $"xl/charts/chart{index + 1}.xml", Chart(index, data));
            }
        }
        return output.ToArray();
    }

    private static XElement ContentTypes()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        return new XElement(ns + "Types",
            new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(ns + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(ns + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
            new XElement(ns + "Override", new XAttribute("PartName", "/xl/worksheets/sheet2.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
            new XElement(ns + "Override", new XAttribute("PartName", "/xl/drawings/drawing1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")),
            Enumerable.Range(1, 7).Select(index =>
                new XElement(ns + "Override", new XAttribute("PartName", $"/xl/charts/chart{index}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawingml.chart+xml"))));
    }

    private static XElement Workbook() =>
        new(MainNs + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", RelNs),
            new XElement(MainNs + "sheets",
                new XElement(MainNs + "sheet", new XAttribute("name", "Datos"), new XAttribute("sheetId", 1), new XAttribute(RelNs + "id", "rId1")),
                new XElement(MainNs + "sheet", new XAttribute("name", "Gráficos"), new XAttribute("sheetId", 2), new XAttribute(RelNs + "id", "rId2"))),
            new XElement(MainNs + "calcPr", new XAttribute("calcMode", "auto")));

    private static XElement WorkbookRels() =>
        new(PackageRelNs + "Relationships",
            Rel("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml"),
            Rel("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet2.xml"));

    private static XElement DataSheet(ReportSummaryResponse report, ReportVisualData data)
    {
        var rows = new List<object?[]>
        {
            new object?[] { "Reporte financiero", null },
            new object?[] { "Desde", report.From.ToLocalTime().ToString("dd/MM/yyyy HH:mm") },
            new object?[] { "Hasta", report.To.ToLocalTime().ToString("dd/MM/yyyy HH:mm") },
            new object?[] { "Ingresos", report.Revenue },
            new object?[] { "Gastos", report.Expenses },
            new object?[] { "Utilidad neta", report.NetProfit },
            new object?[]
            {
                "Cobertura de gráficos",
                data.From.Date > report.From.ToLocalTime().Date
                    ? "Limitada a los últimos 90 días del período"
                    : "Período completo"
            },
            new object?[] { "Ventas por día", "Ingresos", "Utilidad neta" }
        };
        for (var index = 0; index < data.DailySales.Count; index++)
        {
            rows.Add([data.DailySales[index].Label, data.DailySales[index].Value, data.DailyNetProfit[index].Value]);
        }
        var dailyStart = 9;
        var dailyEnd = dailyStart + Math.Max(0, data.DailySales.Count - 1);
        rows.AddRange(new object?[][]
        {
            new object?[] { null, null },
            new object?[] { "Ingresos vs Gastos", "Importe" },
            new object?[] { "Ingresos", data.Revenue },
            new object?[] { "Gastos", data.Expenses },
            new object?[] { null, null },
            new object?[] { "Productos más vendidos", "Unidades" }
        });
        var comparisonStart = dailyEnd + 3;
        var bestSellers = data.BestSellers.Count == 0
            ? new[] { new ReportProductPoint("Sin ventas", 0) }
            : data.BestSellers.ToArray();
        var bestStart = rows.Count + 1;
        rows.AddRange(bestSellers.Select(point => new object?[] { point.Name, point.Quantity }));
        rows.AddRange(new object?[][]
        {
            new object?[] { null, null },
            new object?[] { "Productos menos vendidos", "Unidades" }
        });
        var slowestSellers = data.SlowestSellers.Count == 0
            ? new[] { new ReportProductPoint("Sin ventas", 0) }
            : data.SlowestSellers.ToArray();
        var slowestStart = rows.Count + 1;
        rows.AddRange(slowestSellers.Select(point => new object?[] { point.Name, point.Quantity }));
        rows.AddRange(new object?[][]
        {
            new object?[] { null, null },
            new object?[] { "Ventas por hora", "Ingresos" }
        });
        var hourlyStart = rows.Count + 1;
        rows.AddRange(data.HourlySales.Select(point => new object?[] { point.Label, point.Value }));
        rows.AddRange(new object?[][]
        {
            new object?[] { null, null },
            new object?[] { "Métodos de pago", "Importe" }
        });
        var payments = data.PaymentMethods.Count == 0
            ? new[] { new ReportPaymentPoint("Sin pagos", 0) }
            : data.PaymentMethods.ToArray();
        var paymentsStart = rows.Count + 1;
        rows.AddRange(payments.Select(point => new object?[] { point.Name, point.Amount }));
        rows.AddRange(new object?[][]
        {
            new object?[] { null, null },
            new object?[] { "Utilidad neta diaria", "Utilidad neta" }
        });
        var profitStart = rows.Count + 1;
        rows.AddRange(data.DailyNetProfit.Select(point => new object?[] { point.Label, point.Value }));

        var sheetData = new XElement(MainNs + "sheetData",
            rows.Select((row, rowIndex) => new XElement(MainNs + "row",
                new XAttribute("r", rowIndex + 1),
                row.Select((value, columnIndex) => Cell(value, rowIndex + 1, columnIndex + 1)))));
        return new XElement(MainNs + "worksheet",
            sheetData,
            new XElement(MainNs + "cols",
                new XElement(MainNs + "col", new XAttribute("min", 1), new XAttribute("max", 1), new XAttribute("width", 34), new XAttribute("customWidth", 1)),
                new XElement(MainNs + "col", new XAttribute("min", 2), new XAttribute("max", 3), new XAttribute("width", 20), new XAttribute("customWidth", 1))));
    }

    private static XElement Cell(object? value, int row, int column)
    {
        var address = $"{ColumnName(column)}{row}";
        if (value is null)
        {
            return new XElement(MainNs + "c", new XAttribute("r", address));
        }
        if (value is decimal amount)
        {
            return new XElement(MainNs + "c", new XAttribute("r", address), new XAttribute("t", "n"),
                new XElement(MainNs + "v", amount.ToString(CultureInfo.InvariantCulture)));
        }
        if (value is int count)
        {
            return new XElement(MainNs + "c", new XAttribute("r", address), new XAttribute("t", "n"),
                new XElement(MainNs + "v", count.ToString(CultureInfo.InvariantCulture)));
        }
        return new XElement(MainNs + "c", new XAttribute("r", address), new XAttribute("t", "inlineStr"),
            new XElement(MainNs + "is", new XElement(MainNs + "t", value.ToString())));
    }

    private static XElement ChartSheet() =>
        new(MainNs + "worksheet",
            new XAttribute(XNamespace.Xmlns + "r", RelNs),
            new XElement(MainNs + "sheetViews", new XElement(MainNs + "sheetView", new XAttribute("workbookViewId", 0))),
            new XElement(MainNs + "drawing", new XAttribute(RelNs + "id", "rId1")));

    private static XElement Drawing() =>
        new(DrawingNs + "wsDr",
            Enumerable.Range(0, 7).Select(index =>
            {
                var column = index % 2 == 0 ? 0 : 8;
                var row = (index / 2) * 18;
                return new XElement(DrawingNs + "twoCellAnchor",
                    new XElement(DrawingNs + "from",
                        new XElement(DrawingNs + "col", column), new XElement(DrawingNs + "colOff", 0),
                        new XElement(DrawingNs + "row", row), new XElement(DrawingNs + "rowOff", 0)),
                    new XElement(DrawingNs + "to",
                        new XElement(DrawingNs + "col", column + 7), new XElement(DrawingNs + "colOff", 0),
                        new XElement(DrawingNs + "row", row + 16), new XElement(DrawingNs + "rowOff", 0)),
                    new XElement(DrawingNs + "graphicFrame",
                        new XAttribute("macro", ""),
                        new XElement(DrawingNs + "nvGraphicFramePr",
                            new XElement(DrawingNs + "cNvPr", new XAttribute("id", index + 2), new XAttribute("name", $"Gráfico {index + 1}")),
                            new XElement(DrawingNs + "cNvGraphicFramePr")),
                        new XElement(DrawingNs + "xfrm",
                            new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                            new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
                        new XElement(A + "graphic",
                            new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/chart"),
                                new XElement(ChartNs + "chart", new XAttribute(RelNs + "id", $"rId{index + 1}"))))),
                    new XElement(DrawingNs + "clientData"));
            }));

    private static XElement DrawingRels() =>
        new(PackageRelNs + "Relationships",
            Enumerable.Range(1, 7).Select(index => Rel($"rId{index}",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart", $"../charts/chart{index}.xml")));

    private static XElement Chart(int index, ReportVisualData data)
    {
        var title = index switch
        {
            0 => "Ventas por día",
            1 => "Ingresos vs gastos",
            2 => "Productos más vendidos",
            3 => "Productos menos vendidos",
            4 => "Ventas por hora",
            5 => "Métodos de pago",
            _ => "Utilidad neta diaria"
        };
        var type = index switch
        {
            1 or 2 or 3 => "barChart",
            5 => "pieChart",
            _ => "lineChart"
        };
        var source = ChartSource(index, data);
        var chart = new XElement(ChartNs + type,
            type == "barChart" ? new XElement(ChartNs + "barDir", new XAttribute("val", index is 2 or 3 ? "bar" : "col")) : null,
            type == "barChart" ? new XElement(ChartNs + "grouping", new XAttribute("val", index == 1 ? "clustered" : "clustered")) : null,
            type == "pieChart" ? new XElement(ChartNs + "varyColors", new XAttribute("val", true)) : null,
            Series(title, index, source));
        if (type == "barChart")
        {
            chart.Add(new XElement(ChartNs + "overlap", new XAttribute("val", 0)));
        }
        if (type != "pieChart")
        {
            chart.Add(new XElement(ChartNs + "axId", new XAttribute("val", 100 + index * 2)));
            chart.Add(new XElement(ChartNs + "axId", new XAttribute("val", 101 + index * 2)));
        }
        var chartNode = new XElement(ChartNs + "chart",
            new XElement(ChartNs + "title",
                new XElement(ChartNs + "tx",
                    new XElement(ChartNs + "rich",
                        new XElement(A + "bodyPr"),
                        new XElement(A + "lstStyle"),
                        new XElement(A + "p",
                            new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "es-BO")),
                                new XElement(A + "t", title))))),
                new XElement(ChartNs + "overlay", new XAttribute("val", false))),
            new XElement(ChartNs + "plotArea", new XElement(ChartNs + "layout"),
                chart,
                type == "pieChart" ? null : CategoryAxis(100 + index * 2, index),
                type == "pieChart" ? null : ValueAxis(101 + index * 2, index)),
            type == "pieChart"
                ? new XElement(ChartNs + "legend",
                    new XElement(ChartNs + "legendPos", new XAttribute("val", "r")),
                    new XElement(ChartNs + "layout"),
                    new XElement(ChartNs + "overlay", new XAttribute("val", false)))
                : null,
            new XElement(ChartNs + "plotVisOnly", new XAttribute("val", true)),
            new XElement(ChartNs + "dispBlanksAs", new XAttribute("val", "gap")));
        return new XElement(ChartNs + "chartSpace",
            new XAttribute(XNamespace.Xmlns + "c", ChartNs),
            new XAttribute(XNamespace.Xmlns + "a", A),
            new XElement(ChartNs + "date1904", new XAttribute("val", false)),
            new XElement(ChartNs + "lang", new XAttribute("val", "es-BO")),
            chartNode);
    }

    private static XElement Series(string title, int chartIndex, ChartRange range)
    {
        var valuesFormula = Formula(range.ValueColumn, range.ValueStart, range.ValueEnd);
        var categoryFormula = Formula(range.CategoryColumn, range.CategoryStart, range.CategoryEnd);
        var series = new XElement(ChartNs + "ser",
            new XElement(ChartNs + "idx", new XAttribute("val", 0)),
            new XElement(ChartNs + "order", new XAttribute("val", 0)),
            new XElement(ChartNs + "tx", new XElement(ChartNs + "v", title)),
            new XElement(ChartNs + "spPr",
                new XElement(A + "solidFill", new XElement(A + "srgbClr",
                    new XAttribute("val", chartIndex == 5 ? "48D6A0" : chartIndex == 6 ? "48D6A0" : "8DA7FF"))),
                new XElement(A + "ln", new XAttribute("w", 25400))));
        if (chartIndex == 1)
        {
            series.Add(new XElement(ChartNs + "dPt",
                new XElement(ChartNs + "idx", new XAttribute("val", 1)),
                new XElement(ChartNs + "spPr",
                    new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "FB7185"))))));
        }
        if (chartIndex == 5)
        {
            var palette = new[] { "48D6A0", "FB7185", "8DA7FF", "B39AFF", "F6B258" };
            for (var point = 0; point < range.Values.Length; point++)
            {
                series.Add(new XElement(ChartNs + "dPt",
                    new XElement(ChartNs + "idx", new XAttribute("val", point)),
                    new XElement(ChartNs + "spPr",
                        new XElement(A + "solidFill", new XElement(A + "srgbClr",
                            new XAttribute("val", palette[point % palette.Length]))))));
            }
        }
        series.Add(
            new XElement(ChartNs + "cat", new XElement(ChartNs + "strRef",
                new XElement(ChartNs + "f", categoryFormula),
                StringCache(range.Categories))),
            new XElement(ChartNs + "val", new XElement(ChartNs + "numRef",
                new XElement(ChartNs + "f", valuesFormula),
                NumberCache(range.Values))));
        return series;
    }

    private static XElement CategoryAxis(int id, int chartIndex) =>
        new(ChartNs + "catAx",
            new XElement(ChartNs + "axId", new XAttribute("val", id)),
            new XElement(ChartNs + "scaling", new XElement(ChartNs + "orientation", new XAttribute("val", "minMax"))),
            new XElement(ChartNs + "axPos", new XAttribute("val", chartIndex is 2 or 3 ? "l" : "b")),
            new XElement(ChartNs + "crossAx", new XAttribute("val", id + 1)),
            new XElement(ChartNs + "crosses", new XAttribute("val", "autoZero")));

    private static XElement ValueAxis(int id, int chartIndex) =>
        new(ChartNs + "valAx",
            new XElement(ChartNs + "axId", new XAttribute("val", id)),
            new XElement(ChartNs + "scaling", new XElement(ChartNs + "orientation", new XAttribute("val", "minMax"))),
            new XElement(ChartNs + "axPos", new XAttribute("val", chartIndex is 2 or 3 ? "b" : "l")),
            new XElement(ChartNs + "crossAx", new XAttribute("val", id - 1)),
            new XElement(ChartNs + "crosses", new XAttribute("val", "autoZero")),
            new XElement(ChartNs + "numFmt", new XAttribute("formatCode", "#,##0.00"), new XAttribute("sourceLinked", 0)));

    private sealed record ChartRange(
        string CategoryColumn, int CategoryStart, int CategoryEnd,
        string ValueColumn, int ValueStart, int ValueEnd,
        string[] Categories, decimal?[] Values);

    private static ChartRange ChartSource(int index, ReportVisualData data)
    {
        var dailyEnd = 8 + data.DailySales.Count;
        var comparisonStart = dailyEnd + 3;
        var bestProducts = data.BestSellers.Count == 0
            ? new[] { new ReportProductPoint("Sin ventas", 0) }
            : data.BestSellers.ToArray();
        var bestStart = dailyEnd + 7;
        var slowProducts = data.SlowestSellers.Count == 0
            ? new[] { new ReportProductPoint("Sin ventas", 0) }
            : data.SlowestSellers.ToArray();
        var slowStart = bestStart + bestProducts.Length + 2;
        var hourlyStart = slowStart + slowProducts.Length + 2;
        var payments = data.PaymentMethods.Count == 0
            ? new[] { new ReportPaymentPoint("Sin pagos", 0) }
            : data.PaymentMethods.ToArray();
        var paymentsStart = hourlyStart + data.HourlySales.Count + 2;
        var profitStart = paymentsStart + payments.Length + 2;
        return index switch
        {
            0 => Range("A", 9, data.DailySales.Select(point => point.Label).ToArray(),
                "B", data.DailySales.Select(point => point.Value).ToArray()),
            1 => Range("A", comparisonStart, ["Ingresos", "Gastos"], "B", [data.Revenue, data.Expenses]),
            2 => Range("A", bestStart, bestProducts.Select(point => point.Name).ToArray(),
                "B", bestProducts.Select(point => (decimal?)point.Quantity).ToArray()),
            3 => Range("A", slowStart, slowProducts.Select(point => point.Name).ToArray(),
                "B", slowProducts.Select(point => (decimal?)point.Quantity).ToArray()),
            4 => Range("A", hourlyStart, data.HourlySales.Select(point => point.Label).ToArray(),
                "B", data.HourlySales.Select(point => point.Value).ToArray()),
            5 => Range("A", paymentsStart, payments.Select(point => point.Name).ToArray(),
                "B", payments.Select(point => (decimal?)point.Amount).ToArray()),
            _ => Range("A", profitStart, data.DailyNetProfit.Select(point => point.Label).ToArray(),
                "B", data.DailyNetProfit.Select(point => point.Value).ToArray())
        };
    }

    private static ChartRange Range(string categoryColumn, int categoryStart, string[] categories,
        string valueColumn, decimal?[] values) =>
        new(categoryColumn, categoryStart, categoryStart + Math.Max(0, categories.Length - 1),
            valueColumn, categoryStart, categoryStart + Math.Max(0, values.Length - 1), categories, values);

    private static XElement StringCache(string[] values) =>
        new(ChartNs + "strCache",
            new XElement(ChartNs + "ptCount", new XAttribute("val", values.Length)),
            values.Select((value, index) => new XElement(ChartNs + "pt", new XAttribute("idx", index),
                new XElement(ChartNs + "v", value))));

    private static XElement NumberCache(decimal?[] values) =>
        new(ChartNs + "numCache",
            new XElement(ChartNs + "formatCode", "#,##0.00"),
            new XElement(ChartNs + "ptCount", new XAttribute("val", values.Length)),
            values.Select((value, index) => value.HasValue
                ? new XElement(ChartNs + "pt", new XAttribute("idx", index),
                    new XElement(ChartNs + "v", value.Value.ToString(CultureInfo.InvariantCulture)))
                : null));

    private static string Formula(string column, int start, int end) =>
        $"'Datos'!${column}${start}:${column}${Math.Max(start, end)}";

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }

    private static XElement Rel(string id, string type, string target) =>
        new(PackageRelNs + "Relationship",
            new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target));

    private static void AddXml(ZipArchive archive, string path, XElement document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), document).Save(writer);
    }
}
