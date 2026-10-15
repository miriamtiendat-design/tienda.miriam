using System.Globalization;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Pos;

internal sealed record StockItemOption(Guid Id, string Label, string Unit);
internal sealed record RecipeUnitOption(string Value);
internal sealed record StockLinkOption(Guid? Id, string Label);
internal sealed record DepartmentOption(ProductDepartment Value, string Label);

internal sealed class ProductDepartmentDialog : Form
{
    private readonly ComboBox _department = new()
        { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    public ProductDepartment? SelectedDepartment { get; private set; }

    public ProductDepartmentDialog(ProductDepartment currentDepartment)
    {
        Text = "Cambiar categoría";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 170);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        _department.DataSource = new[]
        {
            new DepartmentOption(ProductDepartment.RawIngredient, "Insumos Crudos"),
            new DepartmentOption(ProductDepartment.PreparedDirect, "Preparados del Día (Receta Directa)"),
            new DepartmentOption(ProductDepartment.FrozenBatch, "Congelados / Por Lote"),
            new DepartmentOption(ProductDepartment.Retail, "Artículos de Tienda / Reventa"),
            new DepartmentOption(ProductDepartment.Service, "Servicios")
        };
        _department.DisplayMember = nameof(DepartmentOption.Label);
        _department.ValueMember = nameof(DepartmentOption.Value);
        _department.SelectedValue = ProductDepartment.Retail;
        _department.SelectedValue = currentDepartment;

        var save = new Button { Text = "Guardar categoría", AutoSize = true };
        DataModuleWindow.StyleButton(save);
        save.Click += (_, _) =>
        {
            SelectedDepartment = ((DepartmentOption)_department.SelectedItem!).Value;
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        DataModuleWindow.StyleButton(cancel);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = BackColor
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = "Selecciona una de las cinco categorías de negocio.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.Muted
        }, 0, 0);
        layout.Controls.Add(_department, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }
}

internal sealed class InventoryEditorDialog : Form
{
    private readonly IReadOnlyList<StockItemOption> _stockItems;
    private readonly bool _editingRecipe;
    private readonly bool _editingProduct;
    private TableLayoutPanel? _layout;
    private int _recipeRow;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, PlaceholderText = "Ej. Salchipapa" };
    private readonly TextBox _sku = new() { Dock = DockStyle.Fill, PlaceholderText = "Se genera automáticamente si queda vacío" };
    private readonly TextBox _barcode = new() { Dock = DockStyle.Fill };
    private readonly TextBox _printStation = new() { Dock = DockStyle.Fill, PlaceholderText = "Opcional · ej. Cocina" };
    private readonly TextBox _price = MoneyInput();
    private readonly TextBox _serviceUnitCost = MoneyInput();
    private readonly ComboBox _kind = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _department = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _stockLink = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DataGridView _components = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        BackgroundColor = ModernTheme.Surface,
        BorderStyle = BorderStyle.None
    };
    private readonly Label _recipeHint = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = ModernTheme.Muted,
        Text = "Indica cuánto de cada insumo consume una porción vendible.",
        TextAlign = ContentAlignment.MiddleLeft
    };

    public CreateProductRequest? ProductRequest { get; private set; }
    public UpdateRecipeRequest? RecipeRequest { get; private set; }

    public InventoryEditorDialog(
        IReadOnlyList<StockItemOption> stockItems,
        Product? existingProduct = null,
        IReadOnlyList<RecipeComponentInput>? existingComponents = null,
        bool editProduct = false,
        ProductDetails? existingDetails = null)
    {
        _stockItems = stockItems;
        _editingProduct = existingProduct is not null && editProduct;
        _editingRecipe = existingProduct is not null && !editProduct;
        Text = _editingRecipe ? $"Editar receta · {existingProduct!.Name}"
            : _editingProduct ? $"Editar producto · {existingProduct!.Name}" : "Nuevo producto";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 590);
        Size = new Size(860, 680);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = _editingRecipe ? 4 : 12,
            BackColor = ModernTheme.Canvas
        };
        _layout = root;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (!_editingRecipe)
        {
            AddField(root, "Nombre del producto", _name, 0);
            AddField(root, "Código / SKU", _sku, 1);
            AddField(root, "Código de barras", _barcode, 2);
            AddField(root, "Precio de venta (Bs)", _price, 3);
            AddField(root, "Tipo de producto", _kind, 4);
            AddField(root, "Categoría", _department, 5);
            AddField(root, "Existencia enlazada", _stockLink, 6);
            AddField(root, "Estación de impresión", _printStation, 7);
            AddField(root, "Costo unitario del servicio (Bs)", _serviceUnitCost, 8);
            _kind.Items.AddRange(["Producto de tienda", "Producto preparado"]);
            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (_, _) =>
            {
                if (_kind.SelectedIndex == 1 && _department.DataSource is not null)
                {
                    _department.SelectedValue = ProductDepartment.PreparedDirect;
                }
                else if (_kind.SelectedIndex == 0
                    && _department.SelectedValue is ProductDepartment department
                    && department == ProductDepartment.PreparedDirect)
                {
                    _department.SelectedValue = ProductDepartment.Retail;
                }
                UpdateRecipeControls();
            };
            _department.DataSource = new[]
            {
                new DepartmentOption(ProductDepartment.RawIngredient, "Insumos Crudos"),
                new DepartmentOption(ProductDepartment.PreparedDirect, "Preparados del Día (Receta Directa)"),
                new DepartmentOption(ProductDepartment.FrozenBatch, "Congelados / Por Lote"),
                new DepartmentOption(ProductDepartment.Retail, "Artículos de Tienda / Reventa"),
                new DepartmentOption(ProductDepartment.Service, "Servicios")
            };
            _department.DisplayMember = nameof(DepartmentOption.Label);
            _department.ValueMember = nameof(DepartmentOption.Value);
            _stockLink.DataSource = new[] { new StockLinkOption(null, "Sin enlace (solo servicio)") }
                .Concat(stockItems.Select(item => new StockLinkOption(item.Id, item.Label)))
                .ToList();
            _stockLink.DisplayMember = nameof(StockLinkOption.Label);
            _stockLink.ValueMember = nameof(StockLinkOption.Id);
            _department.SelectedValueChanged += (_, _) =>
            {
                if (_department.SelectedValue is ProductDepartment.Service
                    && _stockLink.DataSource is not null)
                {
                    _stockLink.SelectedIndex = 0;
                }
                UpdateRecipeControls();
            };
            if (_editingProduct)
            {
                _name.Text = existingProduct!.Name;
                _sku.Text = existingProduct.Sku;
                _barcode.Text = existingProduct.Barcode ?? string.Empty;
                _price.Text = existingProduct.Price.ToString("0.####", CultureInfo.CurrentCulture);
                _serviceUnitCost.Text = (existingDetails?.ServiceUnitCost ?? 0m)
                    .ToString("0.####", CultureInfo.CurrentCulture);
                _kind.SelectedIndex = existingProduct.IsRecipe ? 1 : 0;
                if (existingProduct.StockItemId is Guid linkedStockId)
                {
                    _stockLink.SelectedValue = linkedStockId;
                }
                else
                {
                    _stockLink.SelectedIndex = 0;
                }
                if (existingDetails is not null)
                {
                    _department.SelectedValue = existingDetails.Department;
                }
            }
        }
        else
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.Controls.Add(new Label
            {
                Text = "Receta por porción",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = ModernTheme.OrangeDark,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            root.SetColumnSpan(root.GetControlFromPosition(0, 0)!, 2);
        }

        _recipeHint.Text = _editingRecipe
            ? "La unidad inicial es la del insumo. Puedes usar kg/g o l/ml; el sistema convierte al guardar."
            : "Indica el consumo por porción; kg/g y l/ml se convierten automáticamente.";
        _components.EnableHeadersVisualStyles = false;
        _components.ColumnHeadersDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        _components.ColumnHeadersDefaultCellStyle.ForeColor = ModernTheme.Purple;
        _components.DefaultCellStyle.BackColor = ModernTheme.Surface;
        _components.DefaultCellStyle.ForeColor = ModernTheme.Ink;
        _components.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "StockItemId",
            HeaderText = "Insumo",
            DataSource = stockItems.ToList(),
            DisplayMember = nameof(StockItemOption.Label),
            ValueMember = nameof(StockItemOption.Id),
            ValueType = typeof(Guid),
            FillWeight = 65,
            FlatStyle = FlatStyle.Flat
        });
        _components.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Quantity",
            HeaderText = "Cantidad por porción",
            FillWeight = 25
        });
        _components.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "RecipeUnit",
            HeaderText = "Unidad",
            DisplayMember = nameof(RecipeUnitOption.Value),
            ValueMember = nameof(RecipeUnitOption.Value),
            ValueType = typeof(string),
            FillWeight = 20,
            FlatStyle = FlatStyle.Flat
        });
        _components.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_components.IsCurrentCellDirty)
            {
                _components.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _components.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _components.Columns[e.ColumnIndex].Name == "StockItemId")
            {
                ConfigureRecipeUnitCell(_components.Rows[e.RowIndex], null);
            }
        };

        if (_editingProduct)
        {
            _department.SelectedValue = existingDetails?.Department
                ?? (existingProduct!.IsRecipe ? ProductDepartment.PreparedDirect : ProductDepartment.Retail);
            _printStation.Text = existingDetails?.PrintStation ?? string.Empty;
        }

        var recipeRow = _editingRecipe ? 1 : 9;
        _recipeRow = recipeRow;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(_recipeHint, 0, recipeRow);
        root.SetColumnSpan(_recipeHint, 2);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_components, 0, recipeRow + 1);
        root.SetColumnSpan(_components, 2);

        if (existingComponents is not null)
        {
            foreach (var component in existingComponents)
            {
                var ingredient = stockItems.SingleOrDefault(item => item.Id == component.StockItemId);
                var rowIndex = _components.Rows.Add(
                    component.StockItemId,
                    component.Quantity.ToString("0.######", CultureInfo.CurrentCulture));
                ConfigureRecipeUnitCell(
                    _components.Rows[rowIndex],
                    ingredient?.Unit);
            }
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button
        {
            Text = _editingRecipe ? "Guardar receta" : _editingProduct ? "Guardar cambios" : "Crear producto",
            Width = 150,
            Height = 40
        };
        DataModuleWindow.StyleButton(save);
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancelar", Width = 120, Height = 40, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(actions, 0, recipeRow + 2);
        root.SetColumnSpan(actions, 2);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
        UpdateRecipeControls();
    }

    private static TextBox MoneyInput() => new() { Dock = DockStyle.Fill, Text = "0", PlaceholderText = "0.00" };

    private static void AddField(TableLayoutPanel root, string label, Control input, int row)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.Ink,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        root.Controls.Add(input, 1, row);
    }

    private void UpdateRecipeControls()
    {
        var isPrepared = _editingRecipe || _kind.SelectedIndex == 1;
        _components.Enabled = isPrepared;
        _stockLink.Enabled = !_editingRecipe && !isPrepared
            && _department.SelectedValue is not ProductDepartment.Service;
        _recipeHint.Visible = isPrepared;
        _components.Visible = isPrepared;
        if (!_editingRecipe && _layout is not null && _layout.RowStyles.Count > _recipeRow + 1)
        {
            var isService = _department.SelectedValue is ProductDepartment.Service;
            _layout.RowStyles[8].Height = isService ? 44 : 0;
            _layout.RowStyles[8].SizeType = SizeType.Absolute;
            _serviceUnitCost.Enabled = isService;
            _layout.RowStyles[_recipeRow].Height = isPrepared ? 34 : 0;
            _layout.RowStyles[_recipeRow].SizeType = SizeType.Absolute;
            _layout.RowStyles[_recipeRow + 1].Height = isPrepared ? 100 : 0;
            _layout.RowStyles[_recipeRow + 1].SizeType = isPrepared ? SizeType.Percent : SizeType.Absolute;
        }
    }

    private void Save()
    {
        try
        {
            var components = ReadComponents();
            if (_editingRecipe)
            {
                if (components.Count == 0)
                {
                    throw new InvalidOperationException("Agrega al menos un insumo a la receta.");
                }
                RecipeRequest = new UpdateRecipeRequest(components);
            }
            else
            {
                var name = _name.Text.Trim();
                if (name.Length == 0)
                {
                    throw new InvalidOperationException("Escribe el nombre del producto.");
                }
                var isRecipe = _kind.SelectedIndex == 1;
                if (!TryReadDecimal(_price.Text, out var price) || price < 0 || decimal.Round(price, 4) != price)
                {
                    throw new InvalidOperationException("Ingresa un precio no negativo de hasta 4 decimales.");
                }
                if (!TryReadDecimal(_serviceUnitCost.Text, out var serviceUnitCost)
                    || serviceUnitCost < 0 || decimal.Round(serviceUnitCost, 4) != serviceUnitCost)
                {
                    throw new InvalidOperationException("Ingresa un costo de servicio no negativo de hasta 4 decimales.");
                }
                if (isRecipe && components.Count == 0)
                {
                    throw new InvalidOperationException("Un producto preparado necesita al menos un insumo.");
                }
                var department = ((DepartmentOption)_department.SelectedItem!).Value;
                var stockLink = _stockLink.SelectedItem as StockLinkOption;
                if (!isRecipe && department != ProductDepartment.Service && stockLink?.Id is null)
                {
                    throw new InvalidOperationException(
                        "Enlaza el producto con su existencia para controlar stock y calcular su costo. Los servicios pueden quedar sin existencia.");
                }
                var sku = _sku.Text.Trim();
                if (sku.Length == 0)
                {
                    sku = $"TM-{Guid.NewGuid():N}"[..11].ToUpperInvariant();
                }
                ProductRequest = new CreateProductRequest(
                    sku,
                    name,
                    price,
                    isRecipe,
                    isRecipe ? null : stockLink?.Id,
                    department,
                    isRecipe ? components : [],
                    string.IsNullOrWhiteSpace(_barcode.Text) ? null : _barcode.Text.Trim(),
                    string.IsNullOrWhiteSpace(_printStation.Text) ? null : _printStation.Text.Trim(),
                    department == ProductDepartment.Service ? serviceUnitCost : 0m);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Revisar datos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private List<RecipeComponentInput> ReadComponents()
    {
        _components.EndEdit();
        var result = new List<RecipeComponentInput>();
        var usedStockIds = new HashSet<Guid>();
        var stockById = _stockItems.ToDictionary(item => item.Id);
        foreach (DataGridViewRow row in _components.Rows)
        {
            if (row.IsNewRow)
            {
                continue;
            }
            if (row.Cells["StockItemId"].Value is not Guid stockItemId
                || !decimal.TryParse(Convert.ToString(row.Cells["Quantity"].Value, CultureInfo.CurrentCulture),
                    NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity)
                || quantity <= 0 || decimal.Round(quantity, 6) != quantity
                || row.Cells["RecipeUnit"].Value is not string recipeUnit
                || !stockById.TryGetValue(stockItemId, out var stockItem))
            {
                throw new InvalidOperationException(
                    "Completa el insumo, la cantidad positiva (hasta 6 decimales) y su unidad en cada línea.");
            }
            if (!usedStockIds.Add(stockItemId))
            {
                throw new InvalidOperationException("Cada insumo debe aparecer una sola vez; suma sus cantidades en una línea.");
            }
            decimal quantityInStockUnit;
            try
            {
                quantityInStockUnit = StockUnitConversion.Convert(quantity, recipeUnit, stockItem.Unit);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"La unidad de receta «{recipeUnit}» no es compatible con «{stockItem.Unit}» para {stockItem.Label}.",
                    exception);
            }
            if (quantityInStockUnit <= 0 || decimal.Round(quantityInStockUnit, 6) != quantityInStockUnit)
            {
                throw new InvalidOperationException(
                    $"La cantidad convertida de {stockItem.Label} debe ser positiva y admitir hasta 6 decimales en {stockItem.Unit}.");
            }
            result.Add(new RecipeComponentInput(stockItemId, quantityInStockUnit));
        }
        return result;
    }

    private void ConfigureRecipeUnitCell(DataGridViewRow row, string? preferredUnit)
    {
        if (row.IsNewRow
            || row.Cells["StockItemId"].Value is not Guid stockItemId)
        {
            return;
        }
        var stockItem = _stockItems.FirstOrDefault(item => item.Id == stockItemId);
        if (stockItem is null)
        {
            return;
        }
        var unitCell = (DataGridViewComboBoxCell)row.Cells["RecipeUnit"];
        var availableUnits = StockUnitConversion.GetCompatibleUnits(stockItem.Unit)
            .Select(unit => new RecipeUnitOption(unit))
            .ToList();
        unitCell.DataSource = availableUnits;
        unitCell.DisplayMember = nameof(RecipeUnitOption.Value);
        unitCell.ValueMember = nameof(RecipeUnitOption.Value);
        var selectedUnit = preferredUnit is not null
            && availableUnits.Any(item => string.Equals(item.Value, preferredUnit, StringComparison.OrdinalIgnoreCase))
                ? availableUnits.First(item => string.Equals(item.Value, preferredUnit, StringComparison.OrdinalIgnoreCase)).Value
                : stockItem.Unit;
        unitCell.Value = selectedUnit;
    }

    private static bool TryReadDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
        || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}

internal sealed class StockItemEditorDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _unit = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDown,
        Text = "unidad",
        Items = { "unidad", "kg", "g", "l", "ml" }
    };
    private readonly TextBox _opening = new() { Dock = DockStyle.Fill, Text = "0" };
    private readonly TextBox _reorder = new() { Dock = DockStyle.Fill, Text = "0" };
    private readonly TextBox _unitCost = new() { Dock = DockStyle.Fill, Text = "0" };
    private readonly TextBox _reason = new() { Dock = DockStyle.Fill, Text = "Corrección de inventario" };
    private readonly Guid? _stockItemId;

    public CreateStockItemRequest? Request { get; private set; }
    public UpdateStockItemRequest? UpdateRequest { get; private set; }

    public StockItemEditorDialog(StockItem? stockItem = null)
    {
        _stockItemId = stockItem?.Id;
        Text = stockItem is null ? "Nuevo insumo" : $"Editar insumo · {stockItem.Name}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(540, stockItem is null ? 310 : 365);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        if (stockItem is not null)
        {
            _name.Text = stockItem.Name;
            if (!_unit.Items.Contains(stockItem.Unit))
            {
                _unit.Items.Add(stockItem.Unit);
            }
            _unit.Text = stockItem.Unit;
            _opening.Text = Format(stockItem.OnHand);
            _reorder.Text = Format(stockItem.ReorderAt);
            _unitCost.Text = Format(stockItem.UnitCost);
            _unit.Enabled = false;
            _unit.AccessibleDescription =
                "Unidad base fijada al crear el insumo; las recetas admiten conversión entre kg/g y l/ml.";
        }
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = stockItem is null ? 6 : 7,
            BackColor = ModernTheme.Canvas
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(layout, "Nombre del insumo", _name, 0);
        AddField(layout, stockItem is null ? "Unidad base" : "Unidad base (fija)", _unit, 1);
        AddField(layout, stockItem is null ? "Existencia inicial" : "Existencia", _opening, 2);
        AddField(layout, "Alerta de stock bajo", _reorder, 3);
        AddField(layout, "Costo por unidad (Bs)", _unitCost, 4);
        if (stockItem is not null)
        {
            AddField(layout, "Motivo del ajuste", _reason, 5);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = stockItem is null ? "Guardar insumo" : "Guardar cambios", Width = 145, Height = 40 };
        DataModuleWindow.StyleButton(save);
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancelar", Width = 110, Height = 40, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        var actionRow = stockItem is null ? 5 : 6;
        layout.Controls.Add(actions, 0, actionRow);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.Ink,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        var unit = _unit.Text.Trim();
        if (name.Length == 0 || unit.Length == 0)
        {
            MessageBox.Show(this, "El nombre y la unidad son obligatorios.", "Revisar datos",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!TryReadDecimal(_opening.Text, out var opening)
            || !TryReadDecimal(_reorder.Text, out var reorder)
            || !TryReadDecimal(_unitCost.Text, out var unitCost)
            || opening < 0 || reorder < 0 || unitCost < 0
            || decimal.Round(opening, 6) != opening || decimal.Round(reorder, 6) != reorder
            || decimal.Round(unitCost, 4) != unitCost)
        {
            MessageBox.Show(this, "Ingresa cantidades no negativas (hasta 6 decimales) y un costo válido (hasta 4 decimales).",
                "Revisar datos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_stockItemId is Guid id)
        {
            if (string.IsNullOrWhiteSpace(_reason.Text))
            {
                MessageBox.Show(this, "Escribe el motivo del ajuste de inventario.", "Revisar datos",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _reason.Focus();
                return;
            }
            UpdateRequest = new UpdateStockItemRequest(name, unit, opening, reorder, unitCost, _reason.Text.Trim());
        }
        else
        {
            Request = new CreateStockItemRequest(name, unit, opening, reorder, unitCost);
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool TryReadDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
        || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.CurrentCulture);
}
