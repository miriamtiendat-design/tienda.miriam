using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed class AuditWindow : DataModuleWindow
{
    private readonly LocalUserIdentity _user;
    private readonly DateTimePicker _from = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy",
        Value = DateTime.Today.AddDays(-30),
        Width = 112
    };
    private readonly DateTimePicker _to = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy",
        Value = DateTime.Today,
        Width = 112
    };
    private readonly TextBox _actor = new() { Width = 110, PlaceholderText = "Usuario" };
    private readonly TextBox _action = new() { Width = 150, PlaceholderText = "Acción" };
    private readonly TextBox _entity = new() { Width = 150, PlaceholderText = "Entidad" };
    private readonly TextBox _terminal = new() { Width = 115, PlaceholderText = "Terminal / caja" };
    private readonly TextBox _search = new() { Width = 170, PlaceholderText = "Buscar texto" };
    private readonly TextBox _nextObservation = new() { Width = 190, PlaceholderText = "Motivo próximo cambio (opcional)" };
    private readonly NumericUpDown _retentionDays = new()
    {
        Minimum = 30,
        Maximum = 3650,
        Value = AuditRetentionService.DefaultDays,
        Width = 78,
        ThousandsSeparator = true
    };
    private readonly Button _saveRetention = new() { Text = "Guardar retención (días)", AutoSize = true, Height = 36 };
    private readonly Button _exportExcel = new() { Text = "Exportar Excel", AutoSize = true, Height = 36 };
    private readonly Button _exportPdf = new() { Text = "Exportar PDF", AutoSize = true, Height = 36 };
    private IReadOnlyList<AuditEntry> _filtered = [];
    private bool _retentionLoaded;

    public AuditWindow(DbContextOptions<TiendaDbContext> options, LocalUserIdentity user)
        : base(options, "Auditoría de Cambios")
    {
        if (user.Role != AppRole.Administrator)
        {
            throw new UnauthorizedAccessException("La auditoría está disponible sólo para el administrador.");
        }
        _user = user;
        Grid.Columns.Clear();
        Grid.Columns.Add("OccurredAt", "Fecha y hora");
        Grid.Columns.Add("Actor", "Usuario");
        Grid.Columns.Add("Action", "Acción");
        Grid.Columns.Add("Entity", "Entidad");
        Grid.Columns.Add("Previous", "Valor anterior");
        Grid.Columns.Add("Current", "Valor nuevo");
        Grid.Columns.Add("Observation", "Observación");
        Grid.Columns.Add("Terminal", "Terminal / caja");
        Grid.Columns["OccurredAt"]!.Width = 145;
        Grid.Columns["Action"]!.Width = 180;
        Grid.Columns["Previous"]!.Width = 220;
        Grid.Columns["Current"]!.Width = 220;
        Grid.Columns["Observation"]!.Width = 190;
        Grid.Columns["Terminal"]!.Width = 140;

        foreach (var input in new Control[]
        {
            _from, _to, _actor, _action, _entity, _terminal, _search, _nextObservation
        })
        {
            input.Margin = new Padding(3, 5, 3, 3);
            Header.Controls.Add(input);
        }
        Header.Controls.Add(new Label
        {
            Text = "Retener",
            AutoSize = true,
            ForeColor = ModernTheme.Ink,
            Margin = new Padding(4, 12, 0, 0)
        });
        Header.Controls.Add(_retentionDays);
        StyleButton(_saveRetention);
        StyleButton(_exportExcel);
        StyleButton(_exportPdf);
        Header.Controls.Add(_saveRetention);
        Header.Controls.Add(_exportExcel);
        Header.Controls.Add(_exportPdf);
        _from.ValueChanged += async (_, _) => await RefreshModuleAsync();
        _to.ValueChanged += async (_, _) => await RefreshModuleAsync();
        foreach (var input in new[] { _actor, _action, _entity, _terminal, _search })
        {
            input.TextChanged += async (_, _) => await RefreshModuleAsync();
        }
        _nextObservation.TextChanged += (_, _) => AuditExecutionContext.SetObservation(_nextObservation.Text);
        _exportExcel.Click += async (_, _) => await ExportAsync(pdf: false);
        _exportPdf.Click += async (_, _) => await ExportAsync(pdf: true);
        _saveRetention.Click += async (_, _) => await SaveRetentionAsync();
        Grid.CellFormatting += (_, args) =>
        {
            if (args.RowIndex < 0 || Grid.Rows[args.RowIndex].Tag is not string action)
            {
                return;
            }
            var color = action.Contains("CREATED", StringComparison.OrdinalIgnoreCase)
                || action.Contains("OPENED", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(42, 96, 76)
                : action.Contains("DELETED", StringComparison.OrdinalIgnoreCase)
                    || action.Contains("VOID", StringComparison.OrdinalIgnoreCase)
                    || action.Contains("DEACTIVATED", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(112, 50, 54)
                    : Color.FromArgb(108, 88, 38);
            Grid.Rows[args.RowIndex].DefaultCellStyle.BackColor = color;
        };
    }

    protected override async Task LoadDataAsync()
    {
        var fromLocal = _from.Value.Date;
        var toLocalExclusive = _to.Value.Date.AddDays(1);
        if (toLocalExclusive <= fromLocal)
        {
            Status.Text = "El rango de fechas no es válido.";
            _filtered = [];
            Grid.Rows.Clear();
            return;
        }
        var fromUtc = new DateTimeOffset(fromLocal, TimeZoneInfo.Local.GetUtcOffset(fromLocal)).UtcDateTime;
        var toUtc = new DateTimeOffset(toLocalExclusive, TimeZoneInfo.Local.GetUtcOffset(toLocalExclusive)).UtcDateTime;
        var actor = _actor.Text.Trim();
        var action = _action.Text.Trim();
        var entity = _entity.Text.Trim();
        var terminal = _terminal.Text.Trim();
        var search = _search.Text.Trim();
        await using var db = new TiendaDbContext(Options);
        if (!_retentionLoaded)
        {
            _retentionDays.Value = await AuditRetentionService.GetRetentionDaysAsync(db);
            var removed = await AuditRetentionService.PruneExpiredAsync(db);
            _retentionLoaded = true;
            if (removed > 0)
            {
                Status.Text = $"Se eliminaron automáticamente {removed} registro(s) vencidos por retención.";
            }
        }
        var query = db.AuditEntries.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= fromUtc && item.OccurredAtUtc < toUtc);
        if (actor.Length > 0) query = query.Where(item => item.Actor.Contains(actor));
        if (action.Length > 0) query = query.Where(item => item.Action.Contains(action));
        if (entity.Length > 0) query = query.Where(item => item.EntityType.Contains(entity)
            || item.EntityId.ToString().Contains(entity));
        if (terminal.Length > 0) query = query.Where(item => item.TerminalId != null && item.TerminalId.Contains(terminal));
        if (search.Length > 0) query = query.Where(item => item.Reason.Contains(search)
            || item.Action.Contains(search) || item.Actor.Contains(search)
            || item.EntityType.Contains(search) || (item.PreviousValue != null && item.PreviousValue.Contains(search))
            || (item.NewValue != null && item.NewValue.Contains(search))
            || (item.Observation != null && item.Observation.Contains(search)));
        _filtered = await query.OrderByDescending(item => item.OccurredAtUtc).Take(10_000).ToListAsync();
        Grid.Rows.Clear();
        foreach (var entry in _filtered)
        {
            var row = Grid.Rows.Add(
                DateTime.SpecifyKind(entry.OccurredAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                entry.Actor,
                entry.Action,
                $"{entry.EntityType} · {entry.EntityId:N}",
                entry.PreviousValue ?? "—",
                entry.NewValue ?? entry.Reason,
                entry.Observation ?? "—",
                entry.TerminalId ?? "—");
            Grid.Rows[row].Tag = entry.Action;
            Grid.Rows[row].Cells["Previous"].ToolTipText = entry.PreviousValue ?? string.Empty;
            Grid.Rows[row].Cells["Current"].ToolTipText = entry.NewValue ?? entry.Reason;
            Grid.Rows[row].Cells["Observation"].ToolTipText = entry.Observation ?? string.Empty;
        }
        Status.Text = $"Registros de auditoría: {_filtered.Count} · máximo 10.000 por consulta. Logs inmutables.";
    }

    private async Task SaveRetentionAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(Options);
            await AuditRetentionService.SaveRetentionDaysAsync(db, decimal.ToInt32(_retentionDays.Value));
            var removed = await AuditRetentionService.PruneExpiredAsync(db);
            _retentionLoaded = true;
            await RefreshModuleAsync();
            Status.Text = $"Retención guardada: {(int)_retentionDays.Value} días. Registros vencidos eliminados: {removed}.";
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar la retención: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar la retención: {exception.Message}";
        }
    }

    private async Task ExportAsync(bool pdf)
    {
        if (_filtered.Count == 0)
        {
            Status.Text = "No hay registros filtrados para exportar.";
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = pdf ? "Exportar auditoría a PDF" : "Exportar auditoría a Excel",
            Filter = pdf ? "Documento PDF (*.pdf)|*.pdf" : "Libro Excel (*.xlsx)|*.xlsx",
            DefaultExt = pdf ? "pdf" : "xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"auditoria-{DateTime.Now:yyyyMMdd-HHmmss}.{(pdf ? "pdf" : "xlsx")}"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var bytes = pdf ? AuditExport.CreatePdf(_filtered) : AuditExport.CreateExcel(_filtered);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Status.Text = $"Auditoría exportada: {dialog.FileName}";
        }
        catch (IOException exception)
        {
            Status.Text = $"No se pudo guardar la exportación: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            Status.Text = $"No hay permiso para guardar la exportación: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            Status.Text = $"No se pudo generar la exportación: {exception.Message}";
        }
    }
}

internal static class AuditExport
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static byte[] CreateExcel(IReadOnlyList<AuditEntry> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            AddXml(archive, "[Content_Types].xml", new XElement("Types",
                new XAttribute("xmlns", "http://schemas.openxmlformats.org/package/2006/content-types"),
                new XElement("Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement("Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement("Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement("Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
            AddXml(archive, "_rels/.rels", new XElement(PackageRelationships + "Relationships",
                new XElement(PackageRelationships + "Relationship", new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));
            AddXml(archive, "xl/workbook.xml", new XElement(Main + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", Relationships),
                new XElement(Main + "sheets",
                    new XElement(Main + "sheet", new XAttribute("name", "Auditoría"), new XAttribute("sheetId", "1"), new XAttribute(Relationships + "id", "rId1")))));
            AddXml(archive, "xl/_rels/workbook.xml.rels", new XElement(PackageRelationships + "Relationships",
                new XElement(PackageRelationships + "Relationship", new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml"))));
            var headers = new[] { "Fecha y hora", "Usuario", "Acción", "Entidad", "Valor anterior", "Valor nuevo", "Observación", "Terminal / caja" };
            var rows = new List<string[]> { headers };
            rows.AddRange(entries.Select(entry => new[]
            {
                DateTime.SpecifyKind(entry.OccurredAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                entry.Actor, entry.Action, $"{entry.EntityType} · {entry.EntityId:N}",
                entry.PreviousValue ?? string.Empty, entry.NewValue ?? entry.Reason,
                entry.Observation ?? string.Empty, entry.TerminalId ?? string.Empty
            }));
            var sheetData = new XElement(Main + "sheetData",
                rows.Select((row, index) => new XElement(Main + "row", new XAttribute("r", index + 1),
                    row.Select((value, column) => new XElement(Main + "c",
                        new XAttribute("r", $"{ColumnName(column + 1)}{index + 1}"),
                        new XAttribute("t", "inlineStr"),
                        new XElement(Main + "is", new XElement(Main + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"), value)))))));
            AddXml(archive, "xl/worksheets/sheet1.xml", new XElement(Main + "worksheet", sheetData));
        }
        return output.ToArray();
    }

    public static byte[] CreatePdf(IReadOnlyList<AuditEntry> entries)
    {
        var lines = entries.SelectMany(CreatePdfEntryLines).ToArray();
        var pages = lines.Chunk(34).ToArray();
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Array.Empty<byte>()
        };
        var pageIds = new List<int>();
        var fontId = 3;
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            var stream = CreatePageStream(pages[pageIndex], pageIndex + 1, pages.Length);
            var contentId = objects.Count + 1;
            objects.Add(Encoding.ASCII.GetBytes($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream"));
            var pageId = objects.Count + 1;
            pageIds.Add(pageId);
            objects.Add(Encoding.ASCII.GetBytes(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>"));
        }
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");
        using var output = new MemoryStream();
        output.Write("%PDF-1.4\n"u8);
        var offsets = new long[objects.Count + 1];
        for (var index = 0; index < objects.Count; index++)
        {
            offsets[index + 1] = output.Position;
            output.Write(Encoding.ASCII.GetBytes($"{index + 1} 0 obj\n"));
            output.Write(objects[index]);
            output.Write("\nendobj\n"u8);
        }
        var xrefOffset = output.Position;
        output.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        for (var index = 1; index < offsets.Length; index++)
            output.Write(Encoding.ASCII.GetBytes($"{offsets[index]:0000000000} 00000 n \n"));
        output.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
        return output.ToArray();
    }

    private static string CreatePageStream(IReadOnlyList<string> entries, int page, int totalPages)
    {
        var builder = new StringBuilder("BT /F1 8 Tf 30 570 Td (TIENDA MIRIAM - AUDITORIA DE CAMBIOS) Tj 0 -16 Td ");
        builder.Append($"(Página {page}/{totalPages}) Tj 0 -20 Td ");
        foreach (var line in entries)
        {
            builder.Append($"({EscapePdf(line)}) Tj 0 -15 Td ");
        }
        builder.Append("ET");
        return builder.ToString();
    }

    private static IEnumerable<string> CreatePdfEntryLines(AuditEntry entry)
    {
        var text = AsciiSafe(
            $"{entry.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} | {entry.Actor} | {entry.Action} | "
            + $"{entry.EntityType}/{entry.EntityId:N} | Anterior: {entry.PreviousValue} | "
            + $"Nuevo: {entry.NewValue ?? entry.Reason} | Observación: {entry.Observation} | "
            + $"Terminal/caja: {entry.TerminalId}");
        const int maxLength = 125;
        while (text.Length > maxLength)
        {
            var splitAt = text.LastIndexOf(' ', maxLength, maxLength - 25);
            if (splitAt <= 0) splitAt = maxLength;
            yield return text[..splitAt];
            text = "  " + text[(splitAt + 1)..];
        }
        yield return text;
    }

    private static string AsciiSafe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "—"
        : new string(value.Select(character => character is >= ' ' and <= '~' ? character : '?').ToArray());
    private static string EscapePdf(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    private static string ColumnName(int index) => index <= 26
        ? ((char)('A' + index - 1)).ToString()
        : $"{ColumnName((index - 1) / 26)}{(char)('A' + (index - 1) % 26)}";

    private static void AddXml(ZipArchive archive, string path, XElement document)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        document.Save(stream);
    }
}
