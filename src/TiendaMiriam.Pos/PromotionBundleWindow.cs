using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed class PromotionBundleWindow : Form
{
    private sealed record ProductOption(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly string _actor;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly NumericUpDown _price = new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = 2,
        Minimum = 0,
        Maximum = 9999999,
        ThousandsSeparator = true
    };
    private readonly ComboBox _products = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly NumericUpDown _quantity = new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = 3,
        Minimum = 0.001m,
        Maximum = 999999,
        Value = 1
    };
    private readonly DateTimePicker _startsAt = new() { Dock = DockStyle.Fill, ShowCheckBox = true };
    private readonly DateTimePicker _endsAt = new() { Dock = DockStyle.Fill, ShowCheckBox = true };
    private readonly DataGridView _components = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly DataGridView _history = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };

    public PromotionBundleWindow(DbContextOptions<TiendaDbContext> options, string actor)
    {
        _options = options;
        _actor = actor;
        Text = "Promociones y combos";
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(900, 650);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        _components.Columns.Add("Product", "Producto");
        _components.Columns.Add("Quantity", "Cantidad");
        _history.Columns.Add("Name", "Promoción");
        _history.Columns.Add("Price", "Precio Bs");
        _history.Columns.Add("State", "Estado");
        _history.Columns.Add("Sold", "Veces vendida");
        _history.Columns.Add("Revenue", "Ingresos Bs");
        _history.Columns.Add("Discount", "Descuento Bs");
        _history.Columns.Add("Created", "Creada");
        _history.Columns.Add("Deleted", "Eliminada");

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = ModernTheme.Canvas
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 285));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 6,
            BackColor = ModernTheme.Surface,
            Padding = new Padding(12)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 5; row++)
        {
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        }
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddEditorField(editor, "Nombre del combo", _name, 0, 1);
        AddEditorField(editor, "Precio especial", _price, 0, 3);
        AddEditorField(editor, "Producto", _products, 1, 1);
        AddEditorField(editor, "Cantidad", _quantity, 1, 3);
        AddEditorField(editor, "Inicia (opcional)", _startsAt, 2, 1);
        AddEditorField(editor, "Termina (opcional)", _endsAt, 2, 3);

        var addComponent = new Button { Text = "Agregar producto", Dock = DockStyle.Fill };
        ModernTheme.StyleButton(addComponent, false);
        addComponent.Click += (_, _) => AddComponent();
        editor.Controls.Add(addComponent, 3, 3);
        var save = new Button { Text = "Crear promoción", Dock = DockStyle.Fill };
        ModernTheme.StyleButton(save, true);
        save.Click += async (_, _) => await SaveAsync();
        editor.Controls.Add(save, 2, 3);
        var componentLabel = new Label
        {
            Text = "Productos incluidos",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        editor.Controls.Add(componentLabel, 0, 3);
        editor.SetColumnSpan(componentLabel, 2);
        editor.Controls.Add(_components, 0, 4);
        editor.SetColumnSpan(_components, 4);
        editor.SetRowSpan(_components, 2);

        var historyHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = ModernTheme.Canvas
        };
        historyHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        historyHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var historyActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var deactivate = new Button { Text = "Eliminar promoción seleccionada", Width = 260, Height = 36 };
        ModernTheme.StyleButton(deactivate, false);
        deactivate.Click += async (_, _) => await DeleteSelectedAsync();
        var toggle = new Button { Text = "Activar / desactivar", Width = 190, Height = 36 };
        ModernTheme.StyleButton(toggle, false);
        toggle.Click += async (_, _) => await ToggleSelectedAsync();
        var refresh = new Button { Text = "Actualizar historial", Width = 180, Height = 36 };
        ModernTheme.StyleButton(refresh, false);
        refresh.Click += async (_, _) => await RefreshDataAsync();
        historyActions.Controls.Add(deactivate);
        historyActions.Controls.Add(toggle);
        historyActions.Controls.Add(refresh);
        historyHost.Controls.Add(historyActions, 0, 0);
        historyHost.Controls.Add(_history, 0, 1);
        root.Controls.Add(editor, 0, 0);
        root.Controls.Add(historyHost, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
        Shown += async (_, _) => await RefreshDataAsync();
    }

    private void AddComponent()
    {
        if (_products.SelectedItem is not ProductOption product)
        {
            _status.Text = "Selecciona un producto para agregarlo al combo.";
            return;
        }
        if (_components.Rows.Cast<DataGridViewRow>()
            .Any(row => row.Tag is ProductOption existing && existing.Id == product.Id))
        {
            _status.Text = $"{product.Name} ya está incluido en el combo.";
            return;
        }
        var index = _components.Rows.Add(product.Name, _quantity.Value.ToString("0.###"));
        _components.Rows[index].Tag = product;
    }

    private async Task SaveAsync()
    {
        try
        {
            var components = _components.Rows.Cast<DataGridViewRow>()
                .Select(row => new PromotionBundleComponentInput(
                    ((ProductOption)row.Tag!).Id,
                    decimal.Parse(Convert.ToString(row.Cells[1].Value) ?? "0")))
                .ToArray();
            var request = new CreatePromotionBundleRequest(
                _name.Text.Trim(),
                _price.Value,
                components,
                _startsAt.Checked ? new DateTimeOffset(_startsAt.Value) : null,
                _endsAt.Checked ? new DateTimeOffset(_endsAt.Value) : null);
            await using var db = new TiendaDbContext(_options);
            await new PromotionBundleService(db).UpsertAsync(
                request, _actor, enqueueForSync: true, CancellationToken.None);
            _name.Clear();
            _price.Value = 0;
            _components.Rows.Clear();
            await RefreshDataAsync();
            _status.Text = "Promoción creada y agregada a la cola de sincronización.";
        }
        catch (InvalidOperationException exception) { _status.Text = exception.Message; }
        catch (KeyNotFoundException exception) { _status.Text = exception.Message; }
        catch (DbUpdateException exception) { _status.Text = $"No se pudo guardar: {exception.GetBaseException().Message}"; }
        catch (SqliteException exception) { _status.Text = $"Error local: {exception.Message}"; }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_history.CurrentRow?.Tag is not PromotionBundleResponse bundle)
        {
            _status.Text = "Selecciona una promoción del historial.";
            return;
        }
        if (bundle.IsDeleted)
        {
            _status.Text = "La promoción ya está eliminada.";
            return;
        }
        var answer = MessageBox.Show(
            this,
            $"La promoción '{bundle.Name}' dejará de aparecer en el POS. Su historial se conservará.",
            "Eliminar promoción",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new PromotionBundleService(db).DeleteAsync(
                bundle.Id, _actor, enqueueForSync: true, CancellationToken.None);
            await RefreshDataAsync();
            _status.Text = "Promoción desactivada y cambio agregado a la cola de sincronización.";
        }
        catch (KeyNotFoundException exception) { _status.Text = exception.Message; }
        catch (InvalidOperationException exception) { _status.Text = exception.Message; }
        catch (DbUpdateException exception) { _status.Text = $"No se pudo eliminar: {exception.GetBaseException().Message}"; }
        catch (SqliteException exception) { _status.Text = $"Error local: {exception.Message}"; }
    }

    private async Task ToggleSelectedAsync()
    {
        if (_history.CurrentRow?.Tag is not PromotionBundleResponse bundle || bundle.IsDeleted)
        {
            _status.Text = "Selecciona una promoción que todavía no esté eliminada.";
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new PromotionBundleService(db).SetActiveAsync(
                bundle.Id, !bundle.IsActive, _actor, enqueueForSync: true, CancellationToken.None);
            await RefreshDataAsync();
            _status.Text = bundle.IsActive
                ? "Promoción desactivada; su historial se conserva."
                : "Promoción activada y lista para el POS.";
        }
        catch (KeyNotFoundException exception) { _status.Text = exception.Message; }
        catch (InvalidOperationException exception) { _status.Text = exception.Message; }
        catch (DbUpdateException exception) { _status.Text = $"No se pudo cambiar el estado: {exception.GetBaseException().Message}"; }
        catch (SqliteException exception) { _status.Text = $"Error local: {exception.Message}"; }
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            var products = await db.Products.AsNoTracking()
                .Where(product => product.IsActive)
                .OrderBy(product => product.Name)
                .Select(product => new ProductOption(product.Id, product.Name))
                .ToListAsync();
            _products.DataSource = products;
            _products.DisplayMember = nameof(ProductOption.Name);
            var bundles = await new PromotionBundleService(db).GetAllAsync(CancellationToken.None);
            var sales = await new PromotionBundleService(db).GetSalesReportAsync(null, null, CancellationToken.None);
            var salesById = sales.ToDictionary(item => item.PromotionBundleId);
            _history.Rows.Clear();
            foreach (var bundle in bundles)
            {
                salesById.TryGetValue(bundle.Id, out var totals);
                var row = _history.Rows[_history.Rows.Add(
                    bundle.Name,
                    bundle.Price.ToString("N2"),
                    bundle.IsDeleted ? "ELIMINADA" : bundle.IsActive ? "ACTIVA" : "INACTIVA",
                    totals?.TimesSold ?? 0,
                    (totals?.Revenue ?? 0).ToString("N2"),
                    (totals?.DiscountGiven ?? 0).ToString("N2"),
                    bundle.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy"),
                    bundle.DeletedAt?.ToLocalTime().ToString("dd/MM/yyyy") ?? "—")];
                row.Tag = bundle;
            }
            _status.Text = "Cambios pendientes se sincronizarán automáticamente cuando haya conexión.";
        }
        catch (DbUpdateException exception) { _status.Text = $"No se pudo leer el historial: {exception.GetBaseException().Message}"; }
        catch (SqliteException exception) { _status.Text = $"Error local: {exception.Message}"; }
    }

    private static void AddEditorField(
        TableLayoutPanel layout, string label, Control control, int row, int controlColumn)
    {
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.Ink
        };
        layout.Controls.Add(labelControl, controlColumn - 1, row);
        layout.Controls.Add(control, controlColumn, row);
    }
}
