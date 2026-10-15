using System.Globalization;
using Microsoft.Data.Sqlite;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed record ProductionProductOption(Guid Id, Guid StockItemId, string Name, string Unit)
{
    public override string ToString() => $"{Name} · {Unit}";
}

internal sealed class ProductionEditorDialog : Form
{
    private readonly ComboBox _product = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _quantity = new() { Dock = DockStyle.Fill, PlaceholderText = "Ej. 80" };
    private readonly Label _capacity = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        ForeColor = ModernTheme.OrangeDark,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly DataGridView _ingredients = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        BackgroundColor = ModernTheme.Surface,
        BorderStyle = BorderStyle.None
    };
    private readonly Func<Guid, Task<ProductionCapacityResponse>> _loadCapacity;

    public RecordProductionRequest? Request { get; private set; }

    public ProductionEditorDialog(
        IReadOnlyList<ProductionProductOption> products,
        IReadOnlyList<StockItemOption> ingredients,
        Func<Guid, Task<ProductionCapacityResponse>> loadCapacity)
    {
        _loadCapacity = loadCapacity;
        Text = "Registrar producción por lote";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(800, 600);
        Size = new Size(900, 700);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        _product.DataSource = products.ToList();
        _product.SelectedIndexChanged += async (_, _) => await RefreshCapacityAsync();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = ModernTheme.Canvas
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(details, "Producto terminado", _product, 0);
        AddField(details, "Cantidad obtenida", _quantity, 1);
        details.Controls.Add(_capacity, 0, 2);
        details.SetColumnSpan(_capacity, 2);
        layout.Controls.Add(details, 0, 0);

        var sectionTitle = new Label
        {
            Text = "Insumos crudos utilizados · la cantidad se expresa en la unidad base del inventario",
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.Ink,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(sectionTitle, 0, 1);

        _ingredients.EnableHeadersVisualStyles = false;
        _ingredients.ColumnHeadersDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        _ingredients.ColumnHeadersDefaultCellStyle.ForeColor = ModernTheme.Purple;
        _ingredients.DefaultCellStyle.BackColor = ModernTheme.Surface;
        _ingredients.DefaultCellStyle.ForeColor = ModernTheme.Ink;
        _ingredients.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "StockItemId",
            HeaderText = "Insumo",
            DataSource = ingredients.ToList(),
            DisplayMember = nameof(StockItemOption.Label),
            ValueMember = nameof(StockItemOption.Id),
            ValueType = typeof(Guid),
            FillWeight = 66,
            FlatStyle = FlatStyle.Flat
        });
        _ingredients.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "QuantityConsumed",
            HeaderText = "Cantidad consumida",
            FillWeight = 34
        });
        _ingredients.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_ingredients.IsCurrentCellDirty)
            {
                _ingredients.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = "Registrar lote y actualizar stock", AutoSize = true, Height = 40 };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, Height = 40, DialogResult = DialogResult.Cancel };
        DataModuleWindow.StyleButton(save);
        DataModuleWindow.StyleButton(cancel);
        save.Click += (_, _) => Save();
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += async (_, _) => await RefreshCapacityAsync();
    }

    private static void AddField(TableLayoutPanel layout, string text, Control input, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.Ink,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private async Task RefreshCapacityAsync()
    {
        if (_product.SelectedItem is not ProductionProductOption selected)
        {
            _capacity.Text = "Selecciona un producto terminado.";
            return;
        }
        _capacity.Text = "Calculando capacidad según el último lote…";
        try
        {
            var result = await _loadCapacity(selected.Id);
            if (result.MaximumAdditionalQuantity is not decimal max)
            {
                _capacity.Text =
                    $"Stock terminado: {result.FinishedOnHand:0.######} {result.FinishedUnit}. Registra un primer lote para establecer la proporción de producción.";
                return;
            }
            var byIngredient = string.Join("; ", result.Ingredients.Select(item =>
                $"{item.Name}: {item.OnHand:0.######} {item.Unit} → {item.PossibleFinishedUnits:0} unidades"));
            _capacity.Text =
                $"Tienes {result.FinishedOnHand:0.######} {result.FinishedUnit} terminados; puedes producir {max:0} más según el último lote. {byIngredient}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            _capacity.Text = exception.Message;
        }
        catch (SqliteException exception)
        {
            _capacity.Text = $"No se pudo consultar la capacidad local: {exception.Message}";
        }
    }

    private void Save()
    {
        _ingredients.EndEdit();
        if (_product.SelectedItem is not ProductionProductOption product
            || !TryReadDecimal(_quantity.Text, out var quantity)
            || quantity <= 0 || decimal.Round(quantity, 6) != quantity)
        {
            MessageBox.Show(this, "Selecciona el producto terminado e ingresa una cantidad positiva de hasta 6 decimales.",
                "Revisar producción", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var used = new List<ProductionIngredientInput>();
        var selectedIds = new HashSet<Guid>();
        foreach (DataGridViewRow row in _ingredients.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }
            var itemValue = row.Cells["StockItemId"].Value;
            var quantityText = Convert.ToString(row.Cells["QuantityConsumed"].Value) ?? string.Empty;
            if (itemValue is null && string.IsNullOrWhiteSpace(quantityText))
            {
                continue;
            }
            if (itemValue is not Guid stockItemId
                || !TryReadDecimal(quantityText, out var consumed)
                || consumed <= 0 || decimal.Round(consumed, 6) != consumed)
            {
                MessageBox.Show(this, "Cada insumo debe tener una cantidad consumida positiva de hasta 6 decimales.",
                    "Revisar insumos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (stockItemId == product.StockItemId)
            {
                MessageBox.Show(this, "El stock del producto terminado no puede seleccionarse como insumo crudo.",
                    "Revisar insumos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!selectedIds.Add(stockItemId))
            {
                MessageBox.Show(this, "Cada insumo debe aparecer una sola vez; suma las cantidades en una línea.",
                    "Revisar insumos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            used.Add(new ProductionIngredientInput(stockItemId, consumed));
        }
        if (used.Count == 0)
        {
            MessageBox.Show(this, "Agrega al menos un insumo crudo utilizado.", "Revisar producción",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Request = new RecordProductionRequest(Guid.NewGuid(), product.Id, quantity, used);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool TryReadDecimal(string text, out decimal value)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        return decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out value)
            || decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out value);
    }
}
