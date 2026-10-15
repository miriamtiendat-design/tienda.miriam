using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Drawing.Printing;
using System.Drawing.Drawing2D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed class MainForm : Form
{
    private sealed record CartDisplayRow(
        Guid ProductId,
        string Name,
        string Quantity,
        string UnitCost,
        string UnitPrice,
        string GrossMargin,
        string GrossMarginPercent,
        string Amount,
        string Notes);

    private static readonly JsonSerializerOptions CloudJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly LocalUserIdentity _user;
    private Guid? _cashRegisterSessionId;
    private readonly string _terminalId = Environment.GetEnvironmentVariable("TIENDA_TERMINAL_ID")
        ?? Environment.MachineName;
    private readonly TextBox _search = new() { PlaceholderText = "Buscar por nombre o código...", Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _categoryPanel = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = false,
        Padding = new Padding(4),
        BackColor = ModernTheme.SurfaceMuted
    };
    private readonly TextBox _kitchenNotes = new()
    {
        Dock = DockStyle.Fill,
        MaxLength = 160,
        PlaceholderText = "Nota de cocina (opcional)"
    };
    private readonly TextBox _namedOrder = new()
    {
        Dock = DockStyle.Fill,
        MaxLength = 80,
        PlaceholderText = "Nombre de la orden (opcional)"
    };
    private readonly ComboBox _fulfillmentMode = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Button _tableButton = new()
    {
        Text = "MESA: MOSTRADOR",
        Dock = DockStyle.Fill,
        Margin = new Padding(2)
    };
    private Button? _priceListButton;
    private readonly FlowLayoutPanel _productGrid = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = false,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(3),
        BackColor = ModernTheme.SurfaceMuted
    };
    private readonly Button _receiptToggle = new()
    {
        Text = "RECIBO: SÍ",
        Width = 150,
        Height = 42,
        AccessibleName = "Cambiar si se imprime el recibo"
    };
    private readonly Label _change = new()
    {
        Dock = DockStyle.Fill,
        Text = "VUELTO  0,00 Bs",
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = ModernTheme.Success,
        Font = new Font("Segoe UI", 19, FontStyle.Bold)
    };
    private readonly Label _cashReceived = new()
    {
        Dock = DockStyle.Fill,
        Text = "RECIBIDO\n0,00 Bs",
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = ModernTheme.Muted,
        Font = new Font("Segoe UI", 9, FontStyle.Bold)
    };
    private readonly Label _cartCount = new()
    {
        Dock = DockStyle.Fill,
        Text = "0 artículos",
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = ModernTheme.Muted,
        Font = new Font("Segoe UI", 9, FontStyle.Regular)
    };
    private readonly DataGridView _cartGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly Label _total = new() { Text = "Total: 0,00 Bs", AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold) };
    private readonly Label _status = new() { Text = "Modo local disponible", AutoSize = true };
    private readonly Label _saleSuccess = new()
    {
        Text = "✓  Venta completada",
        Size = new Size(360, 64),
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.FromArgb(238, 16, 86, 61),
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 22, FontStyle.Bold),
        Visible = false,
        Enabled = false
    };
    private readonly Dictionary<Guid, (Product Product, decimal Quantity)> _cart = [];
    private readonly Dictionary<Guid, string?> _cartNotes = [];
    private readonly Dictionary<Guid, decimal?> _unitCosts = [];
    private readonly Dictionary<Guid, decimal?> _availableStock = [];
    private readonly Dictionary<Guid, byte[]?> _productImages = [];
    private readonly Dictionary<Guid, Guid> _cartBundleIds = [];
    private readonly Dictionary<Guid, Color> _productColors = [];
    private readonly Dictionary<Guid, ProductDepartment> _productDepartments = [];
    private readonly Dictionary<Guid, string?> _productStations = [];
    private List<CustomPaymentType> _customPaymentTypes = [];
    private List<PriceList> _priceLists = [];
    private readonly Dictionary<(Guid PriceListId, Guid ProductId), decimal> _priceOverrides = [];
    private List<Promotion> _promotions = [];
    private List<PromotionBundle> _promotionBundles = [];
    private readonly Dictionary<Guid, Guid?> _cartPromotions = [];
    private Guid? _selectedPriceListId;
    private List<Product> _products = [];
    private List<Product> _visibleProducts = [];
    private ProductDepartment? _selectedDepartment;
    private bool _showPromotionBundles;
    private Button? _promotionToggle;
    private Guid? _selectedCustomerId;
    private bool _renderingTiles;
    private decimal _cashTendered;
    private bool _printReceipt = true;
    private string? _selectedTable;
    private Guid? _currentHeldSaleId;
    private int _currentHeldVersion;
    private FulfillmentMode _selectedFulfillmentMode = FulfillmentMode.Counter;
    private Guid? _selectedProductId;
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 15_000 };
    private readonly System.Windows.Forms.Timer _saleSuccessTimer = new() { Interval = 380 };
    private readonly System.Windows.Forms.Timer _saleSuccessAnimationTimer = new() { Interval = 16 };
    private DateTime _saleSuccessAnimationStarted;
    private bool _syncing;
    private bool _animationsEnabled = true;
    private string? _cachedCloudToken;
    private DateTimeOffset _cloudTokenExpiresAt;

    public MainForm(DbContextOptions<TiendaDbContext> options, LocalUserIdentity user)
    {
        _options = options;
        _user = user;
        Text = "Tienda Miriam - Caja";
        Width = 1100;
        Height = 720;
        MinimumSize = new Size(900, 600);
        WindowState = FormWindowState.Maximized;
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        KeyPreview = true;
        BuildLayout();
        VisibleChanged += async (_, _) =>
        {
            if (Visible)
            {
                await LoadAnimationPreferenceAsync();
                _syncTimer.Start();
                await RefreshCatalogSafelyAsync();
            }
        };
        _search.TextChanged += async (_, _) => await FilterCatalogAsync();
        KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter && ActiveControl == _search)
            {
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
                AddScannedItem();
                return;
            }

            if (eventArgs.KeyCode == Keys.F10)
            {
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
                await CompleteSaleAsync(CartTotal());
                return;
            }
            if (eventArgs.KeyCode != Keys.F9)
            {
                return;
            }
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            if (_cart.Count == 0)
            {
                await ResumeHeldSaleAsync();
            }
            else
            {
                await HoldCurrentSaleAsync();
            }
        };
        _productGrid.Resize += (_, _) => RenderProductGrid();
        Resize += (_, _) => RenderProductGrid();
        _syncTimer.Tick += async (_, _) =>
        {
            try
            {
                await TrySyncAsync();
            }
            catch (DbUpdateException exception)
            {
                _status.Text = $"No se pudo actualizar la cola local: {exception.GetBaseException().Message}";
            }
            catch (SqliteException exception)
            {
                _status.Text = $"Error en la base local: {exception.Message}";
            }
        };
        _saleSuccessTimer.Tick += (_, _) =>
        {
            _saleSuccessTimer.Stop();
            _saleSuccessAnimationTimer.Stop();
            _saleSuccess.Visible = false;
        };
        _saleSuccessAnimationTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(
                (DateTime.UtcNow - _saleSuccessAnimationStarted).TotalMilliseconds / 260d,
                0d,
                1d);
            var scale = progress < 0.5d
                ? 18f + (28f - 18f) * (float)(progress * 2d)
                : 28f - (28f - 22f) * (float)((progress - 0.5d) * 2d);
            var previous = _saleSuccess.Font;
            _saleSuccess.Font = new Font("Segoe UI", scale, FontStyle.Bold);
            previous.Dispose();
            if (progress >= 1d)
            {
                _saleSuccessAnimationTimer.Stop();
            }
        };
        FormClosed += (_, _) =>
        {
            _syncTimer.Stop();
            _syncTimer.Dispose();
            _saleSuccessTimer.Stop();
            _saleSuccessTimer.Dispose();
            _saleSuccessAnimationTimer.Stop();
            _saleSuccessAnimationTimer.Dispose();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(root);

        var pageHeader = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Canvas };
        pageHeader.Controls.Add(new Label
        {
            Text = "PUNTO DE VENTA",
            AutoSize = true,
            Location = new Point(4, 1),
            ForeColor = ModernTheme.OrangeDark,
            Font = new Font("Segoe UI", 16, FontStyle.Bold)
        });
        pageHeader.Controls.Add(new Label
        {
            Text = "Agrega productos y cobra en efectivo o QR",
            AutoSize = true,
            Location = new Point(190, 13),
            ForeColor = ModernTheme.Muted,
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        });
        root.Controls.Add(pageHeader, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ModernTheme.Surface,
            Padding = new Padding(2)
        };
        AddToolbarButton(toolbar, "CLIENTE", false, async () => await ChooseCustomerAsync());
        _promotionToggle = AddToolbarButton(toolbar, "PROMOCIONES", false, async () =>
        {
            _showPromotionBundles = !_showPromotionBundles;
            _promotionToggle!.Text = _showPromotionBundles ? "PRODUCTOS" : "PROMOCIONES";
            await FilterCatalogAsync();
        });
        AddToolbarButton(toolbar, "NUEVA VENTA", false, async () => await StartNewSaleAsync());
        AddToolbarButton(toolbar, "ESPERA · F9", false, async () => await HoldCurrentSaleAsync());
        AddToolbarButton(toolbar, "COBRAR · F10", true, async () => await CompleteSaleAsync(CartTotal()));
        _priceListButton = AddToolbarButton(toolbar, "LISTA BASE", false, async () => await ChoosePriceListAsync());
        AddToolbarButton(toolbar, "QR RÁPIDO", false,
            async () => await CompleteSaleAsync(CartTotal(), PaymentMethod.Transfer));
        AddToolbarButton(toolbar, "NOTA COCINA", false, () => { _kitchenNotes.Focus(); return Task.CompletedTask; });
        root.Controls.Add(toolbar, 0, 1);

        var searchBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, BackColor = BackColor };
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        _search.Font = new Font("Segoe UI", 14, FontStyle.Regular);
        _search.Margin = new Padding(2, 4, 8, 4);
        ModernTheme.StyleInput(_search);
        searchBar.Controls.Add(_search, 0, 0);
        StyleButton(_tableButton, false);
        _tableButton.Click += async (_, _) => await ShowDiningTablePlanAsync();
        searchBar.Controls.Add(_tableButton, 1, 0);
        _namedOrder.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        _namedOrder.Margin = new Padding(2, 4, 6, 4);
        ModernTheme.StyleInput(_namedOrder);
        searchBar.Controls.Add(_namedOrder, 2, 0);
        _fulfillmentMode.Items.AddRange([
            new FulfillmentOption(FulfillmentMode.Counter, "MOSTRADOR"),
            new FulfillmentOption(FulfillmentMode.DineIn, "COMER AQUÍ"),
            new FulfillmentOption(FulfillmentMode.Takeaway, "PARA LLEVAR")
        ]);
        _fulfillmentMode.SelectedIndex = 0;
        _fulfillmentMode.Margin = new Padding(2, 4, 6, 4);
        ModernTheme.StyleInput(_fulfillmentMode);
        _fulfillmentMode.SelectedValueChanged += (_, _) =>
        {
            if (_fulfillmentMode.SelectedItem is FulfillmentOption option)
            {
                _selectedFulfillmentMode = option.Mode;
            }
        };
        searchBar.Controls.Add(_fulfillmentMode, 3, 0);
        _kitchenNotes.Font = new Font("Segoe UI", 11, FontStyle.Regular);
        _kitchenNotes.Margin = new Padding(2, 4, 8, 4);
        ModernTheme.StyleInput(_kitchenNotes);
        searchBar.Controls.Add(_kitchenNotes, 4, 0);
        root.Controls.Add(searchBar, 0, 2);

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = BackColor,
            Margin = Padding.Empty
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        workspace.Controls.Add(_categoryPanel, 0, 0);
        workspace.Controls.Add(_productGrid, 1, 0);
        BuildCategoryPanel();
        BuildCheckoutPanel(workspace);
        root.Controls.Add(workspace, 0, 3);

        _status.ForeColor = ModernTheme.OrangeDark;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        root.Controls.Add(_status, 0, 4);
        Controls.Add(_saleSuccess);
        Resize += (_, _) => CenterSaleSuccess();
        CenterSaleSuccess();
    }

    private async Task LoadAnimationPreferenceAsync()
    {
        try
        {
            var settings = await IdleScreenSettingsStore.LoadAsync(_options);
            _animationsEnabled = settings.AnimationsEnabled;
            VisualAnimationPreferences.Enabled = settings.AnimationsEnabled;
        }
        catch (InvalidDataException exception)
        {
            _animationsEnabled = false;
            _status.Text = $"Configuración de animación inválida: {exception.Message}";
        }
        catch (DbUpdateException exception)
        {
            _animationsEnabled = false;
            _status.Text = $"No se pudo cargar la preferencia visual: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _animationsEnabled = false;
            _status.Text = $"Error SQLite al cargar la preferencia visual: {exception.Message}";
        }
    }

    private void CenterSaleSuccess() =>
        _saleSuccess.Left = Math.Max(0, (ClientSize.Width - _saleSuccess.Width) / 2);

    private void ShowSaleSuccess()
    {
        if (!_animationsEnabled)
        {
            return;
        }
        CenterSaleSuccess();
        _saleSuccess.Visible = true;
        _saleSuccess.BringToFront();
        _saleSuccessAnimationStarted = DateTime.UtcNow;
        _saleSuccessAnimationTimer.Stop();
        _saleSuccessAnimationTimer.Start();
        _saleSuccessTimer.Stop();
        _saleSuccessTimer.Start();
    }

    private void BuildCategoryPanel()
    {
        AddCategoryButton("TODO", null, ModernTheme.Orange);
        AddCategoryButton("INSUMOS", ProductDepartment.RawIngredient, ModernTheme.Blue);
        AddCategoryButton("PREPARADOS", ProductDepartment.PreparedDirect, ModernTheme.Red);
        AddCategoryButton("CONGELADOS", ProductDepartment.FrozenBatch, ModernTheme.Purple);
        AddCategoryButton("TIENDA", ProductDepartment.Retail, ModernTheme.Orange);
        AddCategoryButton("SERVICIOS", ProductDepartment.Service, ModernTheme.Green);
    }

    private void AddCategoryButton(string title, ProductDepartment? department, Color color)
    {
        var button = new Button
        {
            Text = title,
            Width = 128,
            Height = 56,
            Margin = new Padding(2),
            BackColor = color,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        ModernTheme.StyleButton(button, color, Color.White, color);
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) =>
        {
            _selectedDepartment = department;
            _ = FilterCatalogAsync();
        };
        _categoryPanel.Controls.Add(button);
    }

    private static Button AddToolbarButton(FlowLayoutPanel toolbar, string title, bool primary, Func<Task> action)
    {
        var button = new Button
        {
            Text = title,
            Width = 112,
            Height = 36,
            Margin = new Padding(2),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        ModernTheme.StyleButton(button, primary);
        button.FlatAppearance.BorderSize = 0;
        button.Click += async (_, _) => await action();
        toolbar.Controls.Add(button);
        return button;
    }

    private void BuildCheckoutPanel(TableLayoutPanel workspace)
    {
        _cartGrid.BackgroundColor = ModernTheme.Surface;
        _cartGrid.GridColor = ModernTheme.Border;
        _cartGrid.DefaultCellStyle.BackColor = ModernTheme.Surface;
        _cartGrid.DefaultCellStyle.ForeColor = ModernTheme.Ink;
        _cartGrid.DefaultCellStyle.SelectionBackColor = ModernTheme.Blue;
        _cartGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        _cartGrid.AlternatingRowsDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        _cartGrid.ColumnHeadersDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        _cartGrid.ColumnHeadersDefaultCellStyle.ForeColor = ModernTheme.Purple;
        _cartGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = ModernTheme.SurfaceMuted;
        _cartGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        _cartGrid.EnableHeadersVisualStyles = false;
        _cartGrid.ScrollBars = ScrollBars.None;
        _cartGrid.RowHeadersVisible = false;
        _cartGrid.ColumnHeadersHeight = 36;
        _cartGrid.RowTemplate.Height = 32;
        _cartGrid.ColumnHeadersHeight = 42;
        _cartGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        _cartGrid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _cartGrid.DefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _cartGrid.DefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PRODUCTO", DataPropertyName = "Name", FillWeight = 150, MinimumWidth = 55 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CANT.", DataPropertyName = "Quantity", FillWeight = 50, MinimumWidth = 28 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "COSTO/U.", DataPropertyName = "UnitCost", FillWeight = 68, MinimumWidth = 32 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "VENTA/U.", DataPropertyName = "UnitPrice", FillWeight = 68, MinimumWidth = 32 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "GANANCIA/U.", DataPropertyName = "GrossMargin", FillWeight = 80, MinimumWidth = 34 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "MARGEN", DataPropertyName = "GrossMarginPercent", FillWeight = 55, MinimumWidth = 35 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "TOTAL", DataPropertyName = "Amount", FillWeight = 75, MinimumWidth = 42 });
        _cartGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "NOTA",
            DataPropertyName = nameof(CartDisplayRow.Notes),
            FillWeight = 105,
            MinimumWidth = 70
        });
        _cartGrid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0
                && _cartGrid.Rows[eventArgs.RowIndex].DataBoundItem is CartDisplayRow row)
            {
                EditProductNote(row.ProductId);
            }
        };

        var checkout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(10, 8, 8, 8),
            BackColor = ModernTheme.Surface
        };
        checkout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        checkout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var cartSection = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = ModernTheme.Surface
        };
        cartSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cartSection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        cartSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        cartSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        cartSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var cartTitle = new Label
        {
            Text = "DETALLE DE VENTA  ·  IMPORTES EN Bs",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.Ink,
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        cartSection.Controls.Add(cartTitle, 0, 0);
        cartSection.Controls.Add(_cartCount, 1, 0);
        var productNoteButton = new Button
        {
            Text = "NOTA PARA EL PRODUCTO SELECCIONADO",
            Dock = DockStyle.Fill,
            Margin = new Padding(2)
        };
        StyleButton(productNoteButton, false);
        productNoteButton.Click += (_, _) =>
        {
            if (_cartGrid.CurrentRow?.DataBoundItem is CartDisplayRow row)
            {
                EditProductNote(row.ProductId);
            }
            else
            {
                _status.Text = "Selecciona un producto de la comanda para agregarle una nota.";
            }
        };
        cartSection.Controls.Add(productNoteButton, 0, 1);
        cartSection.SetColumnSpan(productNoteButton, 2);
        cartSection.Controls.Add(_cartGrid, 0, 2);
        cartSection.SetColumnSpan(_cartGrid, 2);
        checkout.Controls.Add(cartSection, 0, 0);

        _total.Dock = DockStyle.Fill;
        _total.TextAlign = ContentAlignment.MiddleRight;
        _total.ForeColor = ModernTheme.OrangeDark;
        _total.Font = new Font("Segoe UI", 21, FontStyle.Bold);
        checkout.Controls.Add(_total, 0, 1);

        var changePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(230, 242, 235),
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 2, 0, 2)
        };
        changePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        changePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        changePanel.Controls.Add(_change, 0, 0);
        changePanel.Controls.Add(_cashReceived, 1, 0);
        checkout.Controls.Add(changePanel, 0, 2);

        var bills = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = Padding.Empty };
        for (var index = 0; index < 5; index++)
        {
            bills.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        }
        foreach (var bill in new[] { 10m, 20m, 50m, 100m, 200m })
        {
            var button = new Button { Text = $"{bill:0}", Dock = DockStyle.Fill, Margin = new Padding(2), Tag = bill };
            StyleButton(button, false);
            button.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            button.AccessibleName = $"Efectivo entregado {PosFormatting.Money(bill)}";
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) =>
            {
                _cashTendered = (decimal)button.Tag!;
                UpdateCashChangeStatus();
            };
            bills.Controls.Add(button);
        }
        checkout.Controls.Add(bills, 0, 3);

        var held = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        held.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        held.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var hold = new Button { Text = "ESPERA (F9)", Dock = DockStyle.Fill, Margin = new Padding(2) };
        StyleButton(hold, false);
        hold.Click += async (_, _) => await HoldCurrentSaleAsync();
        var resume = new Button { Text = "RECUPERAR", Dock = DockStyle.Fill, Margin = new Padding(2) };
        StyleButton(resume, false);
        resume.Click += async (_, _) => await ResumeHeldSaleAsync();
        held.Controls.Add(hold, 0, 0);
        held.Controls.Add(resume, 1, 0);
        checkout.Controls.Add(held, 0, 4);

        var mainPayments = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        mainPayments.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        mainPayments.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var cashButton = AddPaymentButton(mainPayments, "EFECTIVO", true,
            async () => await CompleteSaleAsync(_cashTendered), 0, 0);
        ModernTheme.StyleButton(cashButton, ModernTheme.Green, Color.White, ModernTheme.Green);
        cashButton.AccessibleName = "Cobrar en efectivo";
        cashButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        var qrButton = AddPaymentButton(mainPayments, "PAGAR CON QR", true,
            async () => await CompleteSaleAsync(CartTotal(), PaymentMethod.Transfer), 1, 0);
        ModernTheme.StyleButton(qrButton, ModernTheme.Blue, Color.White, ModernTheme.Blue);
        qrButton.AccessibleName = "Cobrar mediante QR";
        qrButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        checkout.Controls.Add(mainPayments, 0, 5);

        var otherPayments = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        for (var index = 0; index < 3; index++)
        {
            otherPayments.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        }
        AddPaymentButton(otherPayments, "MONEDERO", false,
            async () => await CompleteCustomerSaleAsync(PaymentMethod.CustomerWallet), 0, 0);
        AddPaymentButton(otherPayments, "FIAR", false,
            async () => await CompleteCustomerSaleAsync(PaymentMethod.Credit), 1, 0);
        AddPaymentButton(otherPayments, "PAGO MIXTO", false,
            async () => await SplitPaymentAsync(), 2, 0);
        checkout.Controls.Add(otherPayments, 0, 6);

        _receiptToggle.Dock = DockStyle.Fill;
        _receiptToggle.Margin = new Padding(2);
        StyleButton(_receiptToggle, true);
        _receiptToggle.Click += (_, _) =>
        {
            _printReceipt = !_printReceipt;
            _receiptToggle.Text = _printReceipt ? "RECIBO: SÍ  ·  IMPRIMIR" : "RECIBO: NO  ·  OMITIR";
            ModernTheme.StyleButton(
                _receiptToggle,
                _printReceipt ? ModernTheme.Green : ModernTheme.SurfaceMuted,
                _printReceipt ? Color.White : ModernTheme.Ink,
                _printReceipt ? ModernTheme.Green : ModernTheme.Border);
        };
        checkout.Controls.Add(_receiptToggle, 0, 7);

        var colorButton = new Button { Text = "COLOR DE TARJETA", Dock = DockStyle.Fill, Margin = new Padding(2) };
        StyleButton(colorButton, false);
        colorButton.Click += (_, _) =>
        {
            if (_selectedProductId is not Guid productId)
            {
                _status.Text = "Primero selecciona una tarjeta de producto.";
                return;
            }
            _ = ChooseProductColorAsync(productId);
        };
        checkout.Controls.Add(colorButton, 0, 8);
        workspace.Controls.Add(checkout, 2, 0);
    }

    private Button AddPaymentButton(
        TableLayoutPanel panel,
        string text,
        bool primary,
        Func<Task> onClick,
        int column,
        int row)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Margin = new Padding(2) };
        StyleButton(button, primary);
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        button.Click += async (_, _) => await onClick();
        panel.Controls.Add(button, column, row);
        return button;
    }

    private void UpdateCashChangeStatus()
    {
        UpdateCashSummary();
        _cashReceived.Text = $"RECIBIDO\n{PosFormatting.Money(_cashTendered)}";
        _status.Text = _cashTendered > 0
            ? $"Efectivo recibido: {PosFormatting.Money(_cashTendered)}"
            : "Selecciona un billete para calcular el vuelto.";
    }

    private void UpdateCashSummary()
    {
        var difference = _cashTendered - CartTotal();
        if (_cashTendered > 0 && difference < 0)
        {
            _change.Text = $"FALTA  {PosFormatting.Money(-difference)}";
            _change.ForeColor = ModernTheme.Danger;
            return;
        }

        _change.Text = $"VUELTO  {PosFormatting.Money(Math.Max(0, difference))}";
        _change.ForeColor = ModernTheme.Success;
    }

    private async Task LoadCatalogAsync()
    {
        await using var db = new TiendaDbContext(_options);
        _products = await db.Products.AsNoTracking()
            .Where(product => product.IsActive)
            .OrderBy(product => product.Name)
            .ToListAsync();
        _productDepartments.Clear();
        _productStations.Clear();
        foreach (var detail in await db.ProductDetails.AsNoTracking().ToListAsync())
        {
            _productDepartments[detail.ProductId] = detail.Department;
            _productStations[detail.ProductId] = detail.PrintStation;
        }
        _customPaymentTypes = await db.CustomPaymentTypes.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync();
        _priceLists = await db.PriceLists.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync();
        _priceOverrides.Clear();
        foreach (var entry in await db.ProductListPrices.AsNoTracking().ToListAsync())
        {
            _priceOverrides[(entry.PriceListId, entry.ProductId)] = entry.Price;
        }
        _promotions = await db.Promotions.AsNoTracking()
            .Where(item => item.IsActive)
            .ToListAsync();
        _promotionBundles = await db.PromotionBundles.AsNoTracking()
            .Include(bundle => bundle.Components)
            .OrderBy(bundle => bundle.Name)
            .ToListAsync();
        var stockItems = await db.StockItems.AsNoTracking()
            .ToDictionaryAsync(item => item.Id);
        var recipeComponents = await db.RecipeComponents.AsNoTracking()
            .ToListAsync();
        var recipesByProduct = recipeComponents
            .GroupBy(component => component.ProductId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var productDetails = await db.ProductDetails.AsNoTracking()
            .ToDictionaryAsync(item => item.ProductId);
        _unitCosts.Clear();
        _availableStock.Clear();
        foreach (var product in _products)
        {
            _unitCosts[product.Id] =
                ProductFinancialCalculator.GetUnitCost(
                    product, stockItems, recipesByProduct,
                    productDetails.GetValueOrDefault(product.Id) is
                        { Department: ProductDepartment.Service } details
                        ? details.ServiceUnitCost : null);
            _availableStock[product.Id] =
                ProductStockCalculator.GetAvailableQuantity(product, stockItems, recipesByProduct);
        }
        _productImages.Clear();
        foreach (var photo in await db.ProductPhotos.AsNoTracking().ToListAsync())
        {
            _productImages[photo.ProductId] = photo.Data;
        }
        _productColors.Clear();
        foreach (var asset in await db.BusinessAssets.AsNoTracking()
                     .Where(item => item.Key.StartsWith("product-color-")).ToListAsync())
        {
            var idText = asset.Key["product-color-".Length..];
            if (Guid.TryParseExact(idText, "N", out var productId)
                && ColorTranslator.FromHtml(System.Text.Encoding.UTF8.GetString(asset.Data)) is var color
                && color.A != 0)
            {
                _productColors[productId] = color;
            }
        }
        await FilterCatalogAsync();
    }

    private Task FilterCatalogAsync()
    {
        var query = _search.Text.Trim();
        if (_showPromotionBundles)
        {
            RenderProductGrid();
            var count = _promotionBundles.Count(bundle =>
                !bundle.IsDeleted && bundle.IsActive
                &&
                (bundle.StartsAtUtc is null || bundle.StartsAtUtc <= DateTime.UtcNow)
                && (bundle.EndsAtUtc is null || bundle.EndsAtUtc > DateTime.UtcNow)
                && bundle.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            _status.Text = count == 0
                ? "No hay promociones activas para esa búsqueda."
                : $"{count} promociones activas · toca una tarjeta para agregar";
            return Task.CompletedTask;
        }
        _visibleProducts = _products
            .Where(product => product.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || product.Sku.Contains(query, StringComparison.OrdinalIgnoreCase)
                || product.Barcode?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Where(product => _selectedDepartment is null
                || _productDepartments.GetValueOrDefault(product.Id, ProductDepartment.Retail) == _selectedDepartment)
            .ToList();
        RenderProductGrid();
        if (_products.Count == 0)
        {
            _status.Text = "Sin catálogo local. Carga los productos antes de operar.";
        }
        else if (_visibleProducts.Count > 0)
        {
            _status.Text = $"{_visibleProducts.Count} productos visibles · toca una tarjeta para agregar";
        }
        else
        {
            _status.Text = "No se encontraron productos con esa búsqueda.";
        }
        return Task.CompletedTask;
    }

    private void RenderProductGrid()
    {
        if (_renderingTiles || _productGrid.IsDisposed || _productGrid.ClientSize.Width <= 0
            || _productGrid.ClientSize.Height <= 0)
        {
            return;
        }
        _renderingTiles = true;
        _productGrid.SuspendLayout();
        try
        {
            foreach (Control oldTile in _productGrid.Controls.Cast<Control>().ToArray())
            {
                oldTile.Dispose();
            }
            _productGrid.Controls.Clear();
            _selectedProductId = null;
            var visibleBundles = _showPromotionBundles
                ? _promotionBundles.Where(bundle =>
                    !bundle.IsDeleted && bundle.IsActive
                    &&
                    (bundle.StartsAtUtc is null || bundle.StartsAtUtc <= DateTime.UtcNow)
                    && (bundle.EndsAtUtc is null || bundle.EndsAtUtc > DateTime.UtcNow)
                    && bundle.Name.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    .ToList()
                : [];
            var tileCount = _showPromotionBundles ? visibleBundles.Count : _visibleProducts.Count;
            if (tileCount == 0)
            {
                return;
            }

            const int gap = 5;
            var availableWidth = Math.Max(1, _productGrid.ClientSize.Width - _productGrid.Padding.Horizontal);
            var availableHeight = Math.Max(1, _productGrid.ClientSize.Height - _productGrid.Padding.Vertical);
            var idealColumns = (int)Math.Ceiling(Math.Sqrt(
                tileCount * availableWidth / (double)availableHeight / 1.22d));
            var columns = Math.Clamp(idealColumns, 1, tileCount);
            var rows = (int)Math.Ceiling(tileCount / (double)columns);
            var tileWidth = Math.Max(1, (availableWidth - columns * gap) / columns);
            var tileHeight = Math.Max(1, (availableHeight - rows * gap) / rows);

            if (_showPromotionBundles)
            {
                foreach (var bundle in visibleBundles)
                {
                    var product = _products.FirstOrDefault(item =>
                        item.Id == bundle.Components.FirstOrDefault()?.ProductId);
                    if (product is null)
                    {
                        continue;
                    }
                    var tile = new ProductTile(
                        ProductAtPrice(new Product
                        {
                            Id = product.Id,
                            Sku = product.Sku,
                            Name = bundle.Name,
                            Price = bundle.Price,
                            IsActive = true,
                            StockItemId = product.StockItemId,
                            IsRecipe = product.IsRecipe
                        }, bundle.Price),
                        tileWidth,
                        tileHeight,
                        _productImages.GetValueOrDefault(product.Id),
                        _productColors.GetValueOrDefault(product.Id, DefaultProductColor(product)),
                        "COMBO",
                        _animationsEnabled);
                    tile.Margin = new Padding(gap / 2);
                    tile.Click += (_, _) =>
                    {
                        AddPromotionBundle(bundle, product);
                        if (_search.TextLength != 0)
                        {
                            _search.Clear();
                        }
                    };
                    _productGrid.Controls.Add(tile);
                }
            }
            else
            {
                foreach (var product in _visibleProducts)
                {
                    var quote = QuoteProductPrice(product, DateTime.UtcNow);
                    var displayedProduct = ProductAtPrice(product, quote.Price);
                    var tile = new ProductTile(
                        displayedProduct,
                        tileWidth,
                        tileHeight,
                        _productImages.GetValueOrDefault(product.Id),
                        _productColors.GetValueOrDefault(product.Id, DefaultProductColor(product)),
                        _availableStock.GetValueOrDefault(product.Id) is decimal available
                            ? available <= 0 ? "AGOTADO" : $"DISP. {available:0.##}"
                            : product.IsRecipe ? "RECETA N/D" : "SIN CONTROL",
                        _animationsEnabled);
                    tile.Margin = new Padding(gap / 2);
                    tile.Click += (_, _) =>
                    {
                        _selectedProductId = product.Id;
                        foreach (var otherTile in _productGrid.Controls.OfType<ProductTile>())
                        {
                            otherTile.IsSelected = ReferenceEquals(otherTile, tile);
                            otherTile.Invalidate();
                        }
                        AddProduct(product);
                        if (_search.TextLength != 0)
                        {
                            _search.Clear();
                        }
                    };
                    tile.ColorRequested += async (_, _) => await ChooseProductColorAsync(product.Id);
                    _productGrid.Controls.Add(tile);
                }
            }
        }
        finally
        {
            _productGrid.ResumeLayout(true);
            _renderingTiles = false;
        }
    }

    private void AddProduct(Product product, decimal quantity = 1m)
    {
        if (_cart.TryGetValue(product.Id, out var current))
        {
            _cart[product.Id] = (current.Product, current.Quantity + quantity);
        }
        else
        {
            var quote = QuoteProductPrice(product, DateTime.UtcNow);
            _cart[product.Id] = (ProductAtPrice(product, quote.Price), quantity);
            _cartPromotions[product.Id] = quote.PromotionId;
        }
        RefreshCart();
        UpdateCashChangeStatus();
        if (_animationsEnabled)
        {
            _productGrid.Controls.OfType<ProductTile>()
                .FirstOrDefault(tile => tile.ProductId == product.Id)?.TriggerPop();
        }
    }

    private void AddPromotionBundle(PromotionBundle bundle, Product representative)
    {
        var cartKey = Guid.NewGuid();
        var product = new Product
        {
            Id = representative.Id,
            Sku = representative.Sku,
            Name = bundle.Name,
            Price = bundle.Price,
            IsActive = true,
            StockItemId = representative.StockItemId,
            IsRecipe = representative.IsRecipe
        };
        _cart[cartKey] = (product, 1m);
        _cartBundleIds[cartKey] = bundle.Id;
        _cartNotes[cartKey] = null;
        RefreshCart();
        UpdateCashChangeStatus();
        if (_animationsEnabled)
        {
            _productGrid.Controls.OfType<ProductTile>()
                .FirstOrDefault(tile => tile.ProductId == representative.Id)?.TriggerPop();
        }
    }

    private void RestoreCartLines(
        IReadOnlyList<SaleLineRequest> lines,
        IReadOnlyDictionary<Guid, Product> products)
    {
        foreach (var line in lines)
        {
            var current = products[line.ProductId];
            var bundle = line.PromotionBundleId is Guid bundleId
                ? _promotionBundles.SingleOrDefault(item => item.Id == bundleId)
                    ?? throw new InvalidOperationException("No se puede recuperar: falta un combo del catálogo local.")
                : null;
            var key = bundle is null ? current.Id : Guid.NewGuid();
            var product = new Product
            {
                Id = current.Id,
                Sku = current.Sku,
                Name = bundle?.Name ?? current.Name,
                Price = line.UnitPriceAtSale,
                IsActive = current.IsActive,
                Barcode = current.Barcode,
                StockItemId = current.StockItemId,
                IsRecipe = current.IsRecipe
            };
            _cart[key] = (product, line.Quantity);
            if (bundle is not null)
            {
                _cartBundleIds[key] = bundle.Id;
            }
            _cartPromotions[key] = line.AppliedPromotionId;
            _cartNotes[key] = string.IsNullOrWhiteSpace(line.Notes) ? null : line.Notes.Trim();
        }
    }

    private decimal? GetBundleUnitCost(Guid bundleId)
    {
        var bundle = _promotionBundles.SingleOrDefault(item => item.Id == bundleId);
        if (bundle is null || bundle.Components.Count == 0)
        {
            return null;
        }
        decimal total = 0m;
        foreach (var component in bundle.Components)
        {
            if (_unitCosts.GetValueOrDefault(component.ProductId) is not decimal componentCost)
            {
                return null;
            }
            total += componentCost * component.Quantity;
        }
        return total;
    }

    private void EditProductNote(Guid productId)
    {
        if (!_cart.TryGetValue(productId, out var cartItem))
        {
            return;
        }
        using var dialog = new Form
        {
            Text = $"Nota · {cartItem.Product.Name}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 220),
            BackColor = ModernTheme.Canvas,
            ForeColor = ModernTheme.Ink
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            RowCount = 3,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(new Label
        {
            Text = "Instrucción para este producto (máximo 160 caracteres)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.Ink
        }, 0, 0);
        var note = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            MaxLength = 160,
            Text = _cartNotes.GetValueOrDefault(productId) ?? string.Empty,
            PlaceholderText = "Ej. Sin cebolla, bien cocido",
            ScrollBars = ScrollBars.Vertical
        };
        ModernTheme.StyleInput(note);
        layout.Controls.Add(note, 0, 1);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = "Guardar nota", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        StyleButton(save, true);
        StyleButton(cancel, false);
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _cartNotes[productId] = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim();
        RefreshCart();
        _status.Text = string.IsNullOrWhiteSpace(note.Text)
            ? $"Nota quitada de {cartItem.Product.Name}."
            : $"Nota guardada para {cartItem.Product.Name}.";
    }

    private ProductPriceQuote QuoteProductPrice(Product product, DateTime occurredAtUtc)
    {
        decimal? listPrice = _selectedPriceListId is Guid listId
            && _priceOverrides.TryGetValue((listId, product.Id), out var preferredPrice)
                ? preferredPrice
                : null;
        return ProductPricingCalculator.GetQuote(
            product.Price,
            listPrice,
            _promotions.Where(item => item.ProductId == product.Id),
            occurredAtUtc);
    }

    private static Product ProductAtPrice(Product product, decimal price) => new()
    {
        Id = product.Id,
        Sku = product.Sku,
        Name = product.Name,
        Barcode = product.Barcode,
        Price = price,
        IsActive = product.IsActive,
        StockItemId = product.StockItemId,
        IsRecipe = product.IsRecipe
    };

    private void AddScannedItem()
    {
        var scanned = _search.Text.Trim();
        if (scanned.Length == 0)
        {
            return;
        }

        Product? product = null;
        var quantity = 1m;
        if (ScaleBarcodeParser.TryParseEan13(scanned, out var itemCode, out var weighedQuantity))
        {
            product = _products.SingleOrDefault(item => item.Sku == itemCode
                || item.Barcode?.EndsWith(itemCode, StringComparison.Ordinal) == true);
            quantity = weighedQuantity;
        }
        else
        {
            product = _products.SingleOrDefault(item => item.Barcode == scanned || item.Sku == scanned);
            product ??= _visibleProducts.Count == 1 ? _visibleProducts[0] : null;
        }

        if (product is null)
        {
            _status.Text = $"Código no reconocido: {scanned}.";
            return;
        }
        AddProduct(product, quantity);
        _search.Clear();
        _status.Text = ScaleBarcodeParser.TryParseEan13(scanned, out _, out _)
            ? $"Peso leído: {quantity:0.###} kg · {product.Name}"
            : $"Código leído: {product.Name}";
    }

    private async Task RefreshCatalogSafelyAsync()
    {
        try
        {
            await LoadCatalogAsync();
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo actualizar el catálogo local: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error al actualizar el catálogo SQLite: {exception.Message}";
        }
    }

    private async Task ChooseProductColorAsync(Guid productId)
    {
        var product = _products.SingleOrDefault(item => item.Id == productId);
        if (product is null)
        {
            _status.Text = "El producto seleccionado ya no está en el catálogo.";
            return;
        }
        using var dialog = new ColorDialog
        {
            FullOpen = true,
            Color = _productColors.GetValueOrDefault(productId, DefaultProductColor(product))
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var assetKey = $"product-color-{productId:N}";
            await using var db = new TiendaDbContext(_options);
            var asset = await db.BusinessAssets.SingleOrDefaultAsync(item => item.Key == assetKey);
            if (asset is null)
            {
                db.BusinessAssets.Add(new BusinessAsset
                {
                    Key = assetKey,
                    ContentType = "text/plain",
                    Data = System.Text.Encoding.UTF8.GetBytes(ColorTranslator.ToHtml(dialog.Color)),
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                asset.Data = System.Text.Encoding.UTF8.GetBytes(ColorTranslator.ToHtml(dialog.Color));
                asset.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            _productColors[productId] = dialog.Color;
            RenderProductGrid();
            _status.Text = $"Color de tarjeta actualizado: {product.Name}.";
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo guardar el color: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite guardando el color: {exception.Message}";
        }
    }

    private static Color DefaultProductColor(Product product) => product.IsRecipe
        ? ModernTheme.Red
        : product.Name.Contains("jabón", StringComparison.CurrentCultureIgnoreCase)
            || product.Name.Contains("papel", StringComparison.CurrentCultureIgnoreCase)
                ? ModernTheme.Blue
                : ModernTheme.OrangeDark;

    private const string DiningTablesAssetKey = "dining-table-names";
    private static readonly string[] DefaultDiningTables = ["Mesa 1", "Mesa 2", "Mesa 3", "Mesa 4", "Mesa 5", "Mesa 6"];

    private sealed record DiningTablePlanCard(
        string TableName, string State, decimal Total, DateTime StartedAtUtc, bool IsOccupied, bool CheckoutRequested);

    private async Task ShowDiningTablePlanAsync()
    {
        try
        {
            await TrySyncAsync();
            var tables = await LoadDiningTablesAsync();
            await using var db = new TiendaDbContext(_options);
            var heldTables = await db.HeldSales.AsNoTracking()
                    .Where(item => item.DiningTable != null && !item.ClosePending && !item.DeletePending)
                    .ToListAsync();
            var form = new Form
            {
                Text = "Plano visual de mesas",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = true,
                MinimumSize = new Size(760, 500),
                ClientSize = new Size(920, 640),
                BackColor = ModernTheme.SurfaceMuted,
                ForeColor = ModernTheme.Ink
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 3,
                BackColor = ModernTheme.SurfaceMuted
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            var heading = new Label
            {
                Text = "MESAS  ·  Verde: libre     Rojo: ocupada     Amarillo: por cobrar",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var grid = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(6),
                BackColor = ModernTheme.SurfaceMuted
            };
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var close = new Button { Text = "Cerrar", Width = 110, Height = 42, DialogResult = DialogResult.Cancel };
            var configure = new Button { Text = "Configurar mesas", Width = 150, Height = 42 };
            var combine = new Button { Text = "Combinar mesas", Width = 150, Height = 42 };
            var transfer = new Button { Text = "Transferir mesa", Width = 150, Height = 42 };
            foreach (var button in new[] { close, configure, combine, transfer })
            {
                StyleButton(button, button == close);
                actions.Controls.Add(button);
            }

            foreach (var table in tables)
            {
                var heldOrder = heldTables.SingleOrDefault(order =>
                    string.Equals(order.DiningTable, table, StringComparison.CurrentCultureIgnoreCase));
                var isCurrentCart = _cart.Count > 0
                    && string.Equals(table, _selectedTable, StringComparison.CurrentCultureIgnoreCase);
                var isOccupied = heldOrder is not null || isCurrentCart;
                var card = new DiningTablePlanCard(
                    table,
                    !isOccupied ? "LIBRE" : heldOrder?.CheckoutRequested == true ? "POR COBRAR" : "OCUPADA",
                    isCurrentCart ? CartTotal() : heldOrder is null ? 0m : GetHeldSaleTotal(heldOrder),
                    heldOrder?.StartedAtUtc ?? heldOrder?.UpdatedAtUtc ?? DateTime.UtcNow,
                    isOccupied,
                    heldOrder?.CheckoutRequested == true);
                var cardLayout = new TableLayoutPanel
                {
                    Size = new Size(254, 176),
                    ColumnCount = 1,
                    RowCount = 2,
                    Margin = new Padding(8),
                    Padding = new Padding(3),
                    BackColor = ModernTheme.Surface
                };
                cardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                cardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                var open = new Button
                {
                    Text = FormatDiningTableCard(card),
                    Dock = DockStyle.Fill,
                    Tag = card,
                    Font = new Font("Segoe UI", 12, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = !isOccupied ? ModernTheme.Success
                        : card.CheckoutRequested ? Color.FromArgb(224, 178, 63)
                        : ModernTheme.Red,
                    ForeColor = card.CheckoutRequested ? ModernTheme.Ink : Color.White,
                    AccessibleName = $"{table}, {card.State}, {FormatMoney(card.Total)}"
                };
                open.FlatAppearance.BorderSize = 0;
                open.Click += async (_, _) =>
                {
                    await ChangeDiningTableAsync(table);
                    if (string.Equals(_selectedTable, table, StringComparison.CurrentCultureIgnoreCase))
                    {
                        form.Close();
                    }
                };
                var checkout = new Button
                {
                    Text = card.CheckoutRequested ? "RETIRAR DE COBRO" : "MARCAR POR COBRAR",
                    Dock = DockStyle.Fill,
                    Enabled = isOccupied
                };
                StyleButton(checkout, false);
                checkout.Click += async (_, _) =>
                {
                    if (await SetTableCheckoutRequestedAsync(table, !card.CheckoutRequested))
                    {
                        form.Close();
                        await ShowDiningTablePlanAsync();
                    }
                };
                cardLayout.Controls.Add(open, 0, 0);
                cardLayout.Controls.Add(checkout, 0, 1);
                grid.Controls.Add(cardLayout);
            }

            configure.Click += async (_, _) =>
            {
                form.Close();
                await ConfigureDiningTablesSafelyAsync();
                await ShowDiningTablePlanAsync();
            };
            transfer.Click += async (_, _) =>
            {
                if (await ShowTableMoveDialogAsync(combineTables: false))
                {
                    form.Close();
                    await ShowDiningTablePlanAsync();
                }
            };
            combine.Click += async (_, _) =>
            {
                if (await ShowTableMoveDialogAsync(combineTables: true))
                {
                    form.Close();
                    await ShowDiningTablePlanAsync();
                }
            };
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(grid, 0, 1);
            layout.Controls.Add(actions, 0, 2);
            form.Controls.Add(layout);
            form.CancelButton = close;
            using var timer = new System.Windows.Forms.Timer { Interval = 30_000 };
            timer.Tick += (_, _) =>
            {
                foreach (var button in grid.Controls.OfType<TableLayoutPanel>()
                    .SelectMany(card => card.Controls.OfType<Button>())
                    .Where(button => button.Tag is DiningTablePlanCard))
                {
                    var card = (DiningTablePlanCard)button.Tag!;
                    button.Text = FormatDiningTableCard(card);
                }
            };
            timer.Start();
            form.FormClosed += (_, _) => timer.Stop();
            form.ShowDialog(this);
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo cargar la configuración de mesas: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite al cargar las mesas: {exception.Message}";
        }
        catch (JsonException exception)
        {
            _status.Text = $"La configuración de mesas está dañada: {exception.Message}";
        }
    }

    private static string FormatDiningTableCard(DiningTablePlanCard card)
    {
        var elapsed = DateTime.UtcNow - card.StartedAtUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        return card.IsOccupied
            ? $"{card.TableName}\n{card.State}\n{FormatMoney(card.Total)}\n{elapsed.TotalHours:0}:{elapsed.Minutes:00} transcurridos"
            : $"{card.TableName}\nLIBRE";
    }

    private decimal GetHeldSaleTotal(HeldSale held) =>
        (JsonSerializer.Deserialize<List<SaleLineRequest>>(held.Payload) ?? [])
        .Sum(line => line.UnitPriceAtSale * line.Quantity);

    private async Task<bool> SetTableCheckoutRequestedAsync(string table, bool checkoutRequested)
    {
        if (_cart.Count > 0 && string.Equals(_selectedTable, table, StringComparison.CurrentCultureIgnoreCase)
            && !await SaveCurrentSaleAsHeldAsync())
        {
            return false;
        }
        try
        {
            await using var db = new TiendaDbContext(_options);
            var held = await db.HeldSales.SingleOrDefaultAsync(item =>
                item.DiningTable == table && !item.ClosePending && !item.DeletePending);
            if (held is null)
            {
                _status.Text = $"No hay una comanda abierta en {table}.";
                return false;
            }
            held.CheckoutRequested = checkoutRequested;
            MarkHeldSaleForSync(held);
            await db.SaveChangesAsync();
            await TrySyncAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _status.Text = $"La orden de {table} cambió en otra caja. Actualiza las mesas e inténtalo de nuevo.";
            return false;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo actualizar el estado de {table}: {exception.GetBaseException().Message}";
            return false;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite al actualizar {table}: {exception.Message}";
            return false;
        }
    }

    private async Task<bool> ShowTableMoveDialogAsync(bool combineTables)
    {
        if (_cart.Count > 0 && !await SaveCurrentSaleAsHeldAsync())
        {
            return false;
        }
        try
        {
            await using var db = new TiendaDbContext(_options);
            var tables = await LoadDiningTablesAsync();
            var occupied = await db.HeldSales.AsNoTracking()
                .Where(item => item.DiningTable != null && !item.ClosePending && !item.DeletePending)
                .ToListAsync();
            var occupiedNames = occupied.Select(item => item.DiningTable!)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var sources = tables.Where(occupiedNames.Contains).ToArray();
            if (sources.Length == 0 || combineTables && sources.Length < 2)
            {
                _status.Text = combineTables
                    ? "Se necesitan dos mesas ocupadas para combinarlas."
                    : "No hay mesas ocupadas para transferir.";
                return false;
            }

            using var dialog = new Form
            {
                Text = combineTables ? "Combinar mesas" : "Transferir mesa",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(440, 210),
                BackColor = ModernTheme.Surface,
                ForeColor = ModernTheme.Ink
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 3
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var source = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            source.Items.AddRange(sources);
            source.SelectedIndex = 0;
            var destination = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            void RefreshDestinations()
            {
                var sourceTable = source.SelectedItem as string;
                destination.Items.Clear();
                var available = combineTables
                    ? sources.Where(item => !string.Equals(item, sourceTable, StringComparison.CurrentCultureIgnoreCase))
                    : tables.Where(item => !occupiedNames.Contains(item));
                destination.Items.AddRange(available.Cast<object>().ToArray());
                if (destination.Items.Count > 0)
                {
                    destination.SelectedIndex = 0;
                }
            }
            source.SelectedIndexChanged += (_, _) => RefreshDestinations();
            RefreshDestinations();
            layout.Controls.Add(new Label
            {
                Text = combineTables ? "Mesa que se agregará" : "Mesa de origen",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            layout.Controls.Add(source, 1, 0);
            layout.Controls.Add(new Label
            {
                Text = combineTables ? "Cuenta que conservará" : "Mesa de destino",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 1);
            layout.Controls.Add(destination, 1, 1);
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var save = new Button
            {
                Text = combineTables ? "Combinar" : "Transferir",
                Width = 120,
                Height = 38,
                DialogResult = DialogResult.OK
            };
            var cancel = new Button { Text = "Cancelar", Width = 100, Height = 38, DialogResult = DialogResult.Cancel };
            StyleButton(save, true);
            StyleButton(cancel, false);
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 2);
            layout.SetColumnSpan(actions, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) != DialogResult.OK
                || source.SelectedItem is not string sourceTableName
                || destination.SelectedItem is not string destinationTableName)
            {
                return false;
            }
            return combineTables
                ? await CombineDiningTablesAsync(sourceTableName, destinationTableName)
                : await TransferDiningTableAsync(sourceTableName, destinationTableName);
        }
        catch (JsonException exception)
        {
            _status.Text = $"No se pudo leer la lista de mesas: {exception.Message}";
            return false;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo cargar el plano de mesas: {exception.GetBaseException().Message}";
            return false;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite al cargar las mesas: {exception.Message}";
            return false;
        }
    }

    private async Task<bool> TransferDiningTableAsync(string sourceTable, string destinationTable)
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable);
            var source = await db.HeldSales.SingleOrDefaultAsync(item =>
                item.DiningTable == sourceTable && !item.ClosePending && !item.DeletePending);
            if (source is null)
            {
                _status.Text = $"La mesa {sourceTable} ya no tiene una comanda abierta.";
                return false;
            }
            if (await db.HeldSales.AnyAsync(item => item.DiningTable == destinationTable
                && !item.ClosePending && !item.DeletePending))
            {
                _status.Text = $"La mesa {destinationTable} ya está ocupada. Actualiza el plano antes de transferir.";
                return false;
            }
            source.DiningTable = destinationTable;
            MarkHeldSaleForSync(source);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await TrySyncAsync();
            _status.Text = $"La comanda se transfirió de {sourceTable} a {destinationTable}.";
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _status.Text = "La comanda cambió en otra caja. Actualiza las mesas e inténtalo de nuevo.";
            return false;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo transferir la mesa: {exception.GetBaseException().Message}";
            return false;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite al transferir la mesa: {exception.Message}";
            return false;
        }
    }

    private async Task<bool> CombineDiningTablesAsync(string sourceTable, string destinationTable)
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new DiningTableService(db).CombineAsync(
                sourceTable, destinationTable, _user.Username);
            await TrySyncAsync();
            _status.Text = $"Se combinaron {sourceTable} y {destinationTable}; la cuenta quedó en {destinationTable}.";
            return true;
        }
        catch (JsonException exception)
        {
            _status.Text = $"No se pudo combinar la comanda: {exception.Message}";
            return false;
        }
        catch (InvalidOperationException exception)
        {
            _status.Text = exception.Message;
            return false;
        }
        catch (DbUpdateConcurrencyException)
        {
            _status.Text = "Una comanda cambió en otra caja. Actualiza las mesas e inténtalo de nuevo.";
            return false;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo combinar la mesa: {exception.GetBaseException().Message}";
            return false;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite al combinar mesas: {exception.Message}";
            return false;
        }
    }

    private static void MarkHeldSaleForSync(HeldSale held)
    {
        if (!held.SyncPending)
        {
            held.ExpectedVersion = held.Version;
            held.Version++;
        }
        held.SyncPending = true;
        held.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task<List<string>> LoadDiningTablesAsync()
    {
        await using var db = new TiendaDbContext(_options);
        var asset = await db.BusinessAssets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == DiningTablesAssetKey);
        if (asset is null)
        {
            return [.. DefaultDiningTables];
        }
        var tables = JsonSerializer.Deserialize<List<string>>(asset.Data)
            ?? throw new JsonException("La lista de mesas está vacía.");
        if (tables.Count is < 1 or > 24 || tables.Any(name =>
            string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40)
            || tables.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() != tables.Count)
        {
            throw new JsonException("La lista debe tener entre 1 y 24 nombres únicos de hasta 40 caracteres.");
        }
        return tables.Select(name => name.Trim()).ToList();
    }

    private async Task SaveDiningTablesAsync(IReadOnlyList<string> tables)
    {
        await using var db = new TiendaDbContext(_options);
        var asset = await db.BusinessAssets.SingleOrDefaultAsync(item => item.Key == DiningTablesAssetKey);
        var data = JsonSerializer.SerializeToUtf8Bytes(tables);
        if (asset is null)
        {
            db.BusinessAssets.Add(new BusinessAsset
            {
                Key = DiningTablesAssetKey,
                ContentType = "application/json",
                Data = data,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            asset.ContentType = "application/json";
            asset.Data = data;
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task ConfigureDiningTablesAsync()
    {
        var tables = await LoadDiningTablesAsync();
        using var dialog = new Form
        {
            Text = "Configurar mesas",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(430, 360),
            BackColor = BackColor,
            ForeColor = ForeColor
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var list = new ListBox { Dock = DockStyle.Fill, DataSource = tables };
        var entry = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Nombre de mesa" };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Guardar", Width = 95, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Width = 95, DialogResult = DialogResult.Cancel };
        var remove = new Button { Text = "Quitar", Width = 95 };
        var add = new Button { Text = "Agregar", Width = 95 };
        foreach (var button in new[] { save, cancel, remove, add })
        {
            StyleButton(button, button == save);
            actions.Controls.Add(button);
        }
        add.Click += (_, _) =>
        {
            var name = entry.Text.Trim();
            if (name.Length == 0 || name.Length > 40
                || tables.Contains(name, StringComparer.CurrentCultureIgnoreCase) || tables.Count >= 24)
            {
                _status.Text = "Usa un nombre único de hasta 40 caracteres y máximo 24 mesas.";
                return;
            }
            tables.Add(name);
            list.DataSource = null;
            list.DataSource = tables.ToArray();
            entry.Clear();
        };
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string selected)
            {
                return;
            }
            if (_cart.Count > 0 && string.Equals(_selectedTable, selected, StringComparison.CurrentCultureIgnoreCase))
            {
                _status.Text = $"{selected} tiene una orden activa y no se puede quitar.";
                return;
            }
            try
            {
                await using var db = new TiendaDbContext(_options);
                if (await db.HeldSales.AnyAsync(item => item.DiningTable == selected))
                {
                    _status.Text = $"{selected} tiene una orden abierta y no se puede quitar.";
                    return;
                }
                tables.Remove(selected);
                list.DataSource = null;
                list.DataSource = tables.ToArray();
            }
            catch (DbUpdateException exception)
            {
                _status.Text = $"No se pudo comprobar la mesa: {exception.GetBaseException().Message}";
            }
            catch (SqliteException exception)
            {
                _status.Text = $"Error SQLite al comprobar la mesa: {exception.Message}";
            }
        };
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(entry, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        if (tables.Count == 0)
        {
            _status.Text = "Debe quedar al menos una mesa configurada.";
            return;
        }
        await SaveDiningTablesAsync(tables);
        if (_selectedTable is not null && !tables.Contains(_selectedTable, StringComparer.CurrentCultureIgnoreCase))
        {
            _selectedTable = null;
        }
        UpdateTableButton();
    }

    private async Task ConfigureDiningTablesSafelyAsync()
    {
        try
        {
            await ConfigureDiningTablesAsync();
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo guardar la configuración de mesas: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error SQLite al guardar las mesas: {exception.Message}";
        }
        catch (JsonException exception)
        {
            _status.Text = $"No se puede configurar la lista de mesas: {exception.Message}";
        }
    }

    private async Task ChangeDiningTableAsync(string? table)
    {
        if (string.Equals(_selectedTable, table, StringComparison.CurrentCultureIgnoreCase)
            && (_cart.Count > 0 || table is null))
        {
            return;
        }
        if (_cart.Count != 0 && !await SaveCurrentSaleAsHeldAsync())
        {
            return;
        }

        _selectedTable = table;
        _currentHeldSaleId = null;
        _currentHeldVersion = 0;
        _selectedFulfillmentMode = table is null ? FulfillmentMode.Counter : FulfillmentMode.DineIn;
        _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
            .First(option => option.Mode == _selectedFulfillmentMode);
        _cashTendered = 0m;
        _kitchenNotes.Clear();
        _namedOrder.Clear();
        _selectedCustomerId = null;
        _selectedPriceListId = null;
        _priceListButton!.Text = "LISTA: BASE";
        if (table is not null)
        {
            try
            {
                await using var db = new TiendaDbContext(_options);
                var held = await db.HeldSales
                    .Where(item => item.DiningTable == table && !item.ClosePending && !item.DeletePending)
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .FirstOrDefaultAsync();
                if (held is not null)
                {
                    _currentHeldSaleId = held.Id;
                    _currentHeldVersion = held.Version;
                    var lines = JsonSerializer.Deserialize<List<SaleLineRequest>>(held.Payload)
                        ?? throw new JsonException("La orden de mesa guardada no contiene productos.");
                    var products = _products.ToDictionary(item => item.Id);
                    if (lines.Any(line => !products.ContainsKey(line.ProductId)))
                    {
                        throw new InvalidOperationException("No se pudo abrir la mesa: falta un producto del catálogo local.");
                    }
                    _cart.Clear();
                    _cartBundleIds.Clear();
                    _cartPromotions.Clear();
                    _cartNotes.Clear();
                    RestoreCartLines(lines, products);
                    _namedOrder.Text = held.NamedOrder ?? string.Empty;
                    _selectedFulfillmentMode = held.FulfillmentMode;
                    _selectedPriceListId = held.PriceListId;
                    _selectedCustomerId = held.CustomerId;
                    _priceListButton!.Text = _priceLists.FirstOrDefault(item => item.Id == held.PriceListId) is { } tablePriceList
                        ? $"LISTA: {tablePriceList.Name}"
                        : "LISTA: BASE";
                    _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
                        .First(option => option.Mode == held.FulfillmentMode);
                    _kitchenNotes.Text = held.KitchenNotes ?? string.Empty;
                }
            }
            catch (JsonException exception)
            {
                _status.Text = $"No se pudo abrir la orden de mesa: {exception.Message}";
            }
            catch (InvalidOperationException exception)
            {
                _status.Text = exception.Message;
            }
            catch (DbUpdateException exception)
            {
                _status.Text = $"No se pudo recuperar la orden: {exception.GetBaseException().Message}";
            }
            catch (SqliteException exception)
            {
                _status.Text = $"Error SQLite al abrir la mesa: {exception.Message}";
            }
        }
        UpdateTableButton();
        RefreshCart();
        UpdateCashChangeStatus();
    }

    private void UpdateTableButton() => _tableButton.Text = _selectedTable is null
        ? _selectedFulfillmentMode == FulfillmentMode.Takeaway ? "PARA LLEVAR" : "MOSTRADOR"
        : $"MESA: {_selectedTable.ToUpperInvariant()}";

    private string GetHeldOrderDescription(HeldSale held)
    {
        var lines = JsonSerializer.Deserialize<List<SaleLineRequest>>(held.Payload) ?? [];
        var items = lines.Select(line =>
        {
            var name = _products.FirstOrDefault(product => product.Id == line.ProductId)?.Name ?? "Artículo";
            return $"{line.Quantity:0.##}× {name}";
        }).Take(3);
        var itemSummary = string.Join(", ", items);
        if (lines.Count > 3)
        {
            itemSummary += $", +{lines.Count - 3}";
        }
        var customerLabel = string.IsNullOrWhiteSpace(held.NamedOrder) ? itemSummary : $"{held.NamedOrder}: {itemSummary}";
        if (!string.IsNullOrWhiteSpace(held.KitchenNotes))
        {
            customerLabel += $" · Nota: {held.KitchenNotes}";
        }
        return customerLabel.Length > 88 ? customerLabel[..85] + "…" : customerLabel;
    }

    private async Task HoldCurrentSaleAsync()
    {
        if (await SaveCurrentSaleAsHeldAsync())
        {
            _selectedTable = null;
            _currentHeldSaleId = null;
            _currentHeldVersion = 0;
            _selectedFulfillmentMode = FulfillmentMode.Counter;
            _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
                .First(option => option.Mode == FulfillmentMode.Counter);
            _selectedPriceListId = null;
            _priceListButton!.Text = "LISTA: BASE";
            _namedOrder.Clear();
            _cashTendered = 0m;
            _kitchenNotes.Clear();
            UpdateTableButton();
        }
    }

    private async Task StartNewSaleAsync()
    {
        if (_cart.Count > 0 && !await SaveCurrentSaleAsHeldAsync())
        {
            return;
        }
        _selectedTable = null;
        _selectedFulfillmentMode = FulfillmentMode.Counter;
        _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
            .First(option => option.Mode == FulfillmentMode.Counter);
        _selectedPriceListId = null;
        _priceListButton!.Text = "LISTA: BASE";
        _selectedCustomerId = null;
        _namedOrder.Clear();
        _cashTendered = 0m;
        _kitchenNotes.Clear();
        UpdateTableButton();
        UpdateCashChangeStatus();
        _status.Text = "Nueva venta lista.";
    }

    private async Task ChooseCustomerAsync()
    {
        try
        {
            var customer = await SelectCustomerAsync();
            if (customer is null)
            {
                return;
            }
            _selectedCustomerId = customer.Id;
            _status.Text = $"Cliente seleccionado: {customer.Name}. Se usará al cobrar con monedero o fiado.";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"No se pudieron consultar clientes locales: {exception.Message}";
        }
    }

    private Task ChoosePriceListAsync()
    {
        if (_cart.Count != 0)
        {
            _status.Text = "Vacía o guarda la venta antes de cambiar la lista de precios.";
            return Task.CompletedTask;
        }
        var choices = new List<PriceListChoice> { new(null, "Precio base BOB") };
        choices.AddRange(_priceLists.Select(item => new PriceListChoice(item.Id, item.Name)));
        using var dialog = new Form
        {
            Text = "Lista de precios",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 300),
            BackColor = BackColor,
            ForeColor = ForeColor
        };
        var list = new ListBox { Dock = DockStyle.Fill, DataSource = choices };
        list.DisplayMember = nameof(PriceListChoice.Name);
        list.SelectedItem = choices.FirstOrDefault(item => item.Id == _selectedPriceListId) ?? choices[0];
        var select = new Button { Text = "Aplicar", Dock = DockStyle.Bottom, Height = 42, DialogResult = DialogResult.OK };
        StyleButton(select, true);
        dialog.Controls.Add(list);
        dialog.Controls.Add(select);
        dialog.AcceptButton = select;
        if (dialog.ShowDialog(this) == DialogResult.OK && list.SelectedItem is PriceListChoice choice)
        {
            _selectedPriceListId = choice.Id;
            _priceListButton!.Text = choice.Id is null ? "LISTA: BASE" : $"LISTA: {choice.Name}";
            _ = FilterCatalogAsync();
        }
        return Task.CompletedTask;
    }

    private async Task<bool> SaveCurrentSaleAsHeldAsync()
    {
        if (_cart.Count == 0)
        {
            _status.Text = "No hay productos para dejar en espera.";
            return false;
        }
        var held = new HeldSale
        {
            Id = _currentHeldSaleId ?? Guid.NewGuid(),
            TerminalId = _terminalId,
            Payload = JsonSerializer.Serialize(_cart.Select(pair =>
                {
                    var item = pair.Value;
                    return new SaleLineRequest(
                    item.Product.Id,
                    item.Quantity,
                    item.Product.Price,
                    AppliedPromotionId: _cartPromotions.GetValueOrDefault(pair.Key),
                    Notes: _cartNotes.GetValueOrDefault(pair.Key),
                    PromotionBundleId: _cartBundleIds.TryGetValue(pair.Key, out var bundleId) ? bundleId : null);
                }).ToArray()),
            DiningTable = _selectedTable,
            NamedOrder = string.IsNullOrWhiteSpace(_namedOrder.Text) ? null : _namedOrder.Text.Trim(),
            FulfillmentMode = _selectedFulfillmentMode,
            PriceListId = _selectedPriceListId,
            CustomerId = _selectedCustomerId,
            KitchenNotes = string.IsNullOrWhiteSpace(_kitchenNotes.Text) ? null : _kitchenNotes.Text.Trim(),
            UpdatedAtUtc = DateTime.UtcNow,
            StartedAtUtc = DateTime.UtcNow,
            Version = _currentHeldVersion,
            ExpectedVersion = 0,
            SyncPending = _selectedTable is not null
        };
        try
        {
            await using var db = new TiendaDbContext(_options);
            var existing = await db.HeldSales.SingleOrDefaultAsync(item => item.Id == held.Id);
            if (existing is not null && _selectedTable is not null)
            {
                held.ExpectedVersion = existing.SyncPending
                    ? existing.ExpectedVersion
                    : _currentHeldVersion;
                held.Version = existing.SyncPending
                    ? existing.Version
                    : _currentHeldVersion + 1;
            }
            else if (_selectedTable is not null)
            {
                held.Version = 1;
                held.ExpectedVersion = 0;
            }
            if (existing is null)
            {
                db.HeldSales.Add(held);
            }
            else
            {
                if (existing.Version != _currentHeldVersion)
                {
                    _status.Text = "La orden cambió en otra caja. Actualiza las mesas y vuelve a cargarla.";
                    return false;
                }
                held.StartedAtUtc = existing.StartedAtUtc ?? held.StartedAtUtc;
                held.CheckoutRequested = existing.CheckoutRequested;
                existing.TerminalId = held.TerminalId;
                existing.Payload = held.Payload;
                existing.DiningTable = held.DiningTable;
                existing.NamedOrder = held.NamedOrder;
                existing.FulfillmentMode = held.FulfillmentMode;
                existing.PriceListId = held.PriceListId;
                existing.CustomerId = held.CustomerId;
                existing.KitchenNotes = held.KitchenNotes;
                existing.StartedAtUtc = held.StartedAtUtc;
                existing.UpdatedAtUtc = held.UpdatedAtUtc;
                existing.SyncPending = held.SyncPending;
                existing.Version = held.Version;
                existing.ExpectedVersion = held.ExpectedVersion;
                existing.ClosePending = false;
                existing.CheckoutRequested = held.CheckoutRequested;
            }
            await db.SaveChangesAsync();
            if (held.DiningTable is not null)
            {
                await TrySyncAsync();
            }
            _cart.Clear();
            _cartBundleIds.Clear();
            _cartPromotions.Clear();
            _cartNotes.Clear();
            _cashTendered = 0m;
            _selectedTable = null;
            _currentHeldSaleId = null;
            _currentHeldVersion = 0;
            _selectedFulfillmentMode = FulfillmentMode.Counter;
            _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
                .First(option => option.Mode == FulfillmentMode.Counter);
            _selectedPriceListId = null;
            _priceListButton!.Text = "LISTA: BASE";
            _selectedCustomerId = null;
            _namedOrder.Clear();
            _kitchenNotes.Clear();
            UpdateTableButton();
            RefreshCart();
            UpdateCashChangeStatus();
            _status.Text = $"Venta {held.Id.ToString()[..8]} guardada en espera local (F9 para recuperar).";
            return true;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo guardar la venta en espera: {exception.GetBaseException().Message}";
            return false;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error guardando la venta en espera: {exception.Message}";
            return false;
        }
    }

    private async Task ResumeHeldSaleAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            var heldSales = await db.HeldSales.AsNoTracking()
                .Where(item => !item.ClosePending && !item.DeletePending
                    && (item.TerminalId == _terminalId || item.DiningTable != null))
                .OrderBy(item => item.UpdatedAtUtc)
                .ToListAsync();
            if (heldSales.Count == 0)
            {
                _status.Text = "No hay ventas en espera en esta caja.";
                return;
            }

            using var picker = new Form
            {
                Text = "Recuperar venta en espera",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(500, 360),
                BackColor = BackColor,
                ForeColor = ForeColor
            };
            var list = new ListBox { Dock = DockStyle.Fill, DataSource = heldSales };
            list.Format += (_, eventArgs) =>
            {
                if (eventArgs.ListItem is HeldSale held)
                {
                    eventArgs.Value = $"{held.NamedOrder ?? held.DiningTable ?? "Mostrador"} · {held.UpdatedAtUtc.ToLocalTime():dd/MM HH:mm} · {held.Id.ToString()[..8]}";
                }
            };
            var restore = new Button { Text = "Recuperar seleccionada", Dock = DockStyle.Bottom, Height = 44 };
            StyleButton(restore, true);
            restore.DialogResult = DialogResult.OK;
            picker.Controls.Add(list);
            picker.Controls.Add(restore);
            picker.AcceptButton = restore;
            if (picker.ShowDialog(this) != DialogResult.OK || list.SelectedItem is not HeldSale selected)
            {
                return;
            }

            var lines = JsonSerializer.Deserialize<List<SaleLineRequest>>(selected.Payload)
                ?? throw new InvalidOperationException("La venta guardada está vacía o dañada.");
            var productsById = _products.ToDictionary(item => item.Id);
            var missing = lines.Where(line => !productsById.ContainsKey(line.ProductId)).ToArray();
            if (missing.Length > 0)
            {
                _status.Text = "No se puede recuperar: falta un producto del catálogo local.";
                return;
            }
            _cart.Clear();
            _cartBundleIds.Clear();
            _cartPromotions.Clear();
            _cartNotes.Clear();
            RestoreCartLines(lines, productsById);
            _selectedTable = selected.DiningTable;
            _currentHeldSaleId = selected.Id;
            _currentHeldVersion = selected.Version;
            _namedOrder.Text = selected.NamedOrder ?? string.Empty;
            _selectedFulfillmentMode = selected.FulfillmentMode;
            _selectedPriceListId = selected.PriceListId;
            _selectedCustomerId = selected.CustomerId;
            _priceListButton!.Text = _priceLists.FirstOrDefault(item => item.Id == selected.PriceListId) is { } heldPriceList
                ? $"LISTA: {heldPriceList.Name}"
                : "LISTA: BASE";
            _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
                .First(option => option.Mode == selected.FulfillmentMode);
            _cashTendered = 0m;
            _kitchenNotes.Text = selected.KitchenNotes ?? string.Empty;
            UpdateTableButton();
            RefreshCart();
            UpdateCashChangeStatus();
            _status.Text = "Venta recuperada. Revisa precios y disponibilidad antes de cobrar.";
        }
        catch (JsonException exception)
        {
            _status.Text = $"No se pudo leer la venta en espera: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            _status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo recuperar la venta: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error leyendo ventas en espera: {exception.Message}";
        }
    }

    private decimal CartTotal() => decimal.Round(
        _cart.Values.Sum(item => item.Product.Price * item.Quantity),
        2,
        MidpointRounding.AwayFromZero);

    private static string FormatMoney(decimal amount) => PosFormatting.Money(amount);

    private static string FormatMoneyNumber(decimal amount) =>
        FormatMoney(amount).Replace(" Bs", string.Empty, StringComparison.Ordinal);

    private void RefreshCart()
    {
        _cartGrid.DataSource = _cart.Select(pair =>
            {
                var item = pair.Value;
                var isBundle = _cartBundleIds.TryGetValue(pair.Key, out var bundleId);
                var cost = isBundle ? GetBundleUnitCost(bundleId) : _unitCosts.GetValueOrDefault(item.Product.Id);
                return new CartDisplayRow(
                pair.Key,
                item.Product.Name,
                item.Quantity.ToString("0.####"),
                cost is decimal costPerUnit
                    ? FormatMoneyNumber(costPerUnit)
                    : "Sin costo",
                FormatMoneyNumber(item.Product.Price),
                cost is decimal unitCost
                    ? FormatMoneyNumber(item.Product.Price - unitCost)
                    : "N/D",
                ProductFinancialCalculator
                    .GetGrossMarginPercent(cost, item.Product.Price) is decimal marginPercent
                    ? $"{marginPercent:0.00}%"
                    : "N/D",
                FormatMoneyNumber(item.Product.Price * item.Quantity),
                _cartNotes.GetValueOrDefault(pair.Key) ?? string.Empty);
            })
            .ToList();
        var total = CartTotal();
        var itemCount = _cart.Values.Sum(item => item.Quantity);
        _cartCount.Text = $"{itemCount:0.####} art.";
        _cartCount.AccessibleName = $"{itemCount:0.####} artículos en la venta";
        _total.Text = $"TOTAL  {FormatMoney(total)}";
        UpdateCashSummary();
        _cashReceived.Text = $"RECIBIDO\n{PosFormatting.Money(_cashTendered)}";
    }

    private static void StyleButton(Button button, bool primary)
    {
        ModernTheme.StyleButton(button, primary);
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    }

    private async Task CompleteCustomerSaleAsync(PaymentMethod method)
    {
        try
        {
            var customerId = _selectedCustomerId;
            if (customerId is null)
            {
                var customer = await SelectCustomerAsync();
                if (customer is null)
                {
                    return;
                }
                customerId = customer.Id;
                _selectedCustomerId = customer.Id;
            }
            await CompleteSaleAsync(
                method == PaymentMethod.CustomerWallet ? CartTotal() : 0m,
                method,
                customerId);
        }
        catch (SqliteException exception)
        {
            _status.Text = $"No se pudieron consultar clientes locales: {exception.Message}";
        }
    }

    private async Task SplitPaymentAsync()
    {
        var options = new List<PaymentOption>
        {
            new(PaymentMethod.Cash, null, "Efectivo"),
            new(PaymentMethod.Transfer, null, "QR")
        };
        options.AddRange(_customPaymentTypes.Select(item =>
            new PaymentOption(PaymentMethod.Custom, item.Id, item.Name)));
        using var dialog = new Form
        {
            Text = "Pago dividido · BOB",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(500, 205),
            BackColor = BackColor,
            ForeColor = ForeColor
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var methods = new[] { new ComboBox(), new ComboBox() };
        var amounts = new[] { new NumericUpDown(), new NumericUpDown() };
        for (var index = 0; index < 2; index++)
        {
            methods[index].Dock = DockStyle.Fill;
            methods[index].DropDownStyle = ComboBoxStyle.DropDownList;
            methods[index].DataSource = options.ToList();
            methods[index].DisplayMember = nameof(PaymentOption.Name);
            methods[index].BackColor = ModernTheme.Surface;
            methods[index].ForeColor = ModernTheme.Ink;
            amounts[index].Dock = DockStyle.Fill;
            amounts[index].DecimalPlaces = 2;
            amounts[index].Maximum = 1_000_000m;
            amounts[index].ThousandsSeparator = true;
            amounts[index].Value = index == 0 ? CartTotal() : 0m;
            layout.Controls.Add(methods[index], 0, index);
            layout.Controls.Add(amounts[index], 1, index);
        }
        layout.Controls.Add(new Label
        {
            Text = $"TOTAL A CUBRIR: {FormatMoney(CartTotal())} Bs",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.OrangeDark,
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        }, 0, 2);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        var accept = new Button { Text = "COBRAR", Width = 110, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "CANCELAR", Width = 110, DialogResult = DialogResult.Cancel };
        StyleButton(accept, true);
        StyleButton(cancel, false);
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var paymentLines = Enumerable.Range(0, 2)
            .Where(index => amounts[index].Value > 0)
            .Select(index =>
            {
                var option = (PaymentOption)methods[index].SelectedItem!;
                return new SalePaymentRequest(option.Method, amounts[index].Value, option.PaymentTypeId);
            })
            .ToArray();
        if (paymentLines.Length < 2 || paymentLines.Sum(item => item.Amount) != CartTotal())
        {
            _status.Text = "Los pagos deben incluir al menos dos importes y sumar exactamente el total en BOB.";
            return;
        }
        await CompleteSaleAsync(CartTotal(), PaymentMethod.Cash, _selectedCustomerId, paymentLines);
    }

    private async Task<CustomerAccount?> SelectCustomerAsync()
    {
        await using var db = new TiendaDbContext(_options);
        var customers = await db.Customers.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .ToListAsync();
        if (customers.Count == 0)
        {
            _status.Text = "Sin clientes sincronizados. Conéctate y actualiza el catálogo/maestros.";
            return null;
        }
        using var picker = new Form
        {
            Text = "Seleccionar cliente",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(440, 340),
            BackColor = BackColor,
            ForeColor = ForeColor
        };
        var list = new ListBox { Dock = DockStyle.Fill, DataSource = customers };
        list.DisplayMember = nameof(CustomerAccount.Name);
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = 44;
        list.DrawItem += (_, args) =>
        {
            args.DrawBackground();
            if (args.Index >= 0 && args.Index < customers.Count)
            {
                var item = customers[args.Index];
                var text = $"{item.Name}  |  Monedero {FormatMoney(item.WalletBalance)} Bs  |  Fiado {FormatMoney(item.CreditBalance)}/{FormatMoney(item.CreditLimit)} Bs";
                using var brush = new SolidBrush(args.ForeColor);
                args.Graphics?.DrawString(text, Font, brush, args.Bounds);
            }
            args.DrawFocusRectangle();
        };
        var select = new Button { Text = "Continuar", Dock = DockStyle.Bottom, Height = 44, DialogResult = DialogResult.OK };
        StyleButton(select, true);
        picker.Controls.Add(list);
        picker.Controls.Add(select);
        picker.AcceptButton = select;
        return picker.ShowDialog(this) == DialogResult.OK ? list.SelectedItem as CustomerAccount : null;
    }

    private async Task CompleteSaleAsync(
        decimal amountTendered,
        PaymentMethod method = PaymentMethod.Cash,
        Guid? customerId = null,
        IReadOnlyList<SalePaymentRequest>? paymentLines = null)
    {
        if (_cart.Count == 0)
        {
            _status.Text = "Agrega productos antes de cobrar.";
            return;
        }

        if (!await EnsureCashSessionAsync())
        {
            return;
        }

        var request = new CreateSaleRequest(
            Guid.NewGuid(),
            _terminalId,
            _cart.Select(pair => new SaleLineRequest(
                pair.Value.Product.Id,
                pair.Value.Quantity,
                pair.Value.Product.Price,
                AppliedPromotionId: _cartPromotions.GetValueOrDefault(pair.Key),
                Notes: _cartNotes.GetValueOrDefault(pair.Key),
                PromotionBundleId: _cartBundleIds.TryGetValue(pair.Key, out var bundleId) ? bundleId : null)).ToArray(),
            method,
            amountTendered,
            DateTime.UtcNow,
            customerId,
            _selectedTable,
            string.IsNullOrWhiteSpace(_kitchenNotes.Text) ? null : _kitchenNotes.Text.Trim(),
            string.IsNullOrWhiteSpace(_namedOrder.Text) ? null : _namedOrder.Text.Trim(),
            _selectedFulfillmentMode,
            paymentLines,
            _selectedPriceListId,
            _cashRegisterSessionId,
            _currentHeldSaleId,
            _currentHeldVersion);
        SaleResult result;
        try
        {
            await using var db = new TiendaDbContext(_options);
            result = await new SaleService(db).CreateAsync(request, enqueueForSync: true, CancellationToken.None);
        }

        catch (InvalidOperationException exception)
        {
            _status.Text = exception.Message;
            return;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se confirmó la venta local: {exception.GetBaseException().Message}";
            return;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error en la base local: {exception.Message}";
            return;
        }

        _cart.Clear();
        _cartBundleIds.Clear();
        _cartPromotions.Clear();
        _cartNotes.Clear();
        _cashTendered = 0m;
        _selectedTable = null;
        _currentHeldSaleId = null;
        _currentHeldVersion = 0;
        _selectedFulfillmentMode = FulfillmentMode.Counter;
        _fulfillmentMode.SelectedItem = _fulfillmentMode.Items.Cast<FulfillmentOption>()
            .First(option => option.Mode == FulfillmentMode.Counter);
        _selectedPriceListId = null;
        _priceListButton!.Text = "LISTA: BASE";
        _namedOrder.Clear();
        _selectedCustomerId = null;
        _kitchenNotes.Clear();
        UpdateTableButton();
        RefreshCart();
        UpdateCashChangeStatus();
        _status.Text = $"Venta completada · Vuelto {FormatMoney(result.Change)}";
        ShowSaleSuccess();
        _search.Focus();
        await RefreshCatalogSafelyAsync();
        var printErrors = new List<string>();
        if (_printReceipt)
        {
            try
            {
                await using var db = new TiendaDbContext(_options);
                var saleLines = await db.SaleLines.AsNoTracking()
                    .Where(line => line.SaleId == result.SaleId)
                    .ToListAsync();
                var lineIds = saleLines.Select(line => line.Id).ToArray();
                var financialSnapshots = await db.SaleLineFinancialSnapshots.AsNoTracking()
                    .Where(snapshot => lineIds.Contains(snapshot.SaleLineId))
                    .ToDictionaryAsync(snapshot => snapshot.SaleLineId);
                var financialByProduct = saleLines.Where(line => line.PromotionBundleId is null)
                    .GroupBy(line => line.ProductId)
                    .ToDictionary(group => group.Key, group => financialSnapshots.GetValueOrDefault(group.First().Id));
                var financialByBundle = saleLines.Where(line => line.PromotionBundleId is not null)
                    .GroupBy(line => line.PromotionBundleId!.Value)
                    .ToDictionary(group => group.Key, group => financialSnapshots.GetValueOrDefault(group.First().Id));
                WindowsPrinter.Print(
                    Environment.GetEnvironmentVariable("TIENDA_THERMAL_PRINTER"),
                    BuildReceiptLines(
                        request,
                        result,
                        _products,
                        _promotionBundles.ToDictionary(bundle => bundle.Id, bundle => bundle.Name),
                        financialByProduct,
                        financialByBundle));
            }
            catch (InvalidPrinterException exception)
            {
                printErrors.Add($"ticket: {exception.Message}");
            }
            catch (Win32Exception exception)
            {
                printErrors.Add($"ticket: {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                printErrors.Add($"ticket: {exception.Message}");
            }
            catch (DbUpdateException exception)
            {
                printErrors.Add($"ticket financiero: {exception.GetBaseException().Message}");
            }
            catch (SqliteException exception)
            {
                printErrors.Add($"ticket financiero, base local: {exception.Message}");
            }
        }
        var kitchenLines = new List<(string? Station, string ProductName, decimal Quantity, string? Notes)>();
        foreach (var line in request.Lines!)
        {
            if (line.PromotionBundleId is Guid bundleId)
            {
                var bundle = _promotionBundles.SingleOrDefault(item => item.Id == bundleId);
                foreach (var component in bundle?.Components ?? [])
                {
                    var product = _products.SingleOrDefault(item => item.Id == component.ProductId);
                    if (product?.IsRecipe == true)
                    {
                        kitchenLines.Add((
                            _productStations.GetValueOrDefault(product.Id),
                            product.Name,
                            component.Quantity * line.Quantity,
                            line.Notes));
                    }
                }
            }
            else if (_products.SingleOrDefault(product => product.Id == line.ProductId) is { } product
                && (product.IsRecipe || !string.IsNullOrWhiteSpace(line.Notes)))
            {
                kitchenLines.Add((
                    _productStations.GetValueOrDefault(product.Id),
                    product.Name,
                    line.Quantity,
                    line.Notes));
            }
        }
        var kitchenGroups = kitchenLines
            .GroupBy(line => string.IsNullOrWhiteSpace(line.Station)
                ? "KITCHEN"
                : line.Station.Trim().ToUpperInvariant());
        foreach (var stationGroup in kitchenGroups)
        {
            try
            {
                var stationKey = new string(stationGroup.Key
                    .Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
                var printerName = Environment.GetEnvironmentVariable($"TIENDA_{stationKey}_PRINTER");
                if (string.IsNullOrWhiteSpace(printerName) && stationKey == "KITCHEN")
                {
                    printerName = Environment.GetEnvironmentVariable("TIENDA_KITCHEN_PRINTER");
                }
                WindowsPrinter.Print(
                    printerName,
                    [
                        $"TIENDA MIRIAM - {stationGroup.Key}",
                        $"Comanda {result.SaleId.ToString()[..8]}",
                        DateTime.Now.ToString("g"),
                        string.IsNullOrWhiteSpace(request.NamedOrder)
                            ? string.IsNullOrWhiteSpace(request.DiningTable) ? request.FulfillmentMode.ToString() : request.DiningTable
                            : request.NamedOrder,
                        string.IsNullOrWhiteSpace(request.KitchenNotes) ? string.Empty : $"NOTA: {request.KitchenNotes}",
                        "------------------------------",
                        .. stationGroup.SelectMany(line =>
                        {
                            var itemLine = $"{line.Quantity:0.####} x {line.ProductName}";
                            return string.IsNullOrWhiteSpace(line.Notes)
                                ? new[] { itemLine }
                                : new[] { itemLine, $"NOTA: {line.Notes.Trim()}" };
                        })
                    ]);
            }
            catch (InvalidPrinterException exception)
            {
                printErrors.Add($"comanda: {exception.Message}");
            }
            catch (Win32Exception exception)
            {
                printErrors.Add($"comanda: {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                printErrors.Add($"comanda: {exception.Message}");
            }
        }
        try
        {
            await TrySyncAsync();
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"Venta guardada; no se pudo actualizar la cola de sincronización: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Venta guardada; error accediendo a la base local: {exception.Message}";
        }
        if (printErrors.Count != 0)
        {
            _status.Text += $" | Impresión pendiente: {string.Join("; ", printErrors)}";
        }
    }

    private async Task<bool> EnsureCashSessionAsync()
    {
        var terminalId = _terminalId;
        try
        {
            await using var db = new TiendaDbContext(_options);
            var current = await new CashRegisterService(db).GetOpenSummaryAsync(terminalId);
            if (current is not null)
            {
                _cashRegisterSessionId = current.Id;
                return true;
            }

            using var opening = new OpeningCashDialog();
            if (opening.ShowDialog(this) != DialogResult.OK)
            {
                _status.Text = "Abre una sesión de caja para poder cobrar ventas.";
                return false;
            }

            await using var openDb = new TiendaDbContext(_options);
            var opened = await new CashRegisterService(openDb).OpenAsync(terminalId, _user.Username, opening.Amount);
            _cashRegisterSessionId = opened.Id;
            return true;
        }
        catch (InvalidOperationException exception)
        {
            _status.Text = $"No se pudo validar la sesión de caja: {exception.Message}";
            return false;
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo abrir la caja: {exception.GetBaseException().Message}";
            return false;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error al validar la caja local: {exception.Message}";
            return false;
        }
    }

    private static IReadOnlyList<string> BuildReceiptLines(
        CreateSaleRequest request,
        SaleResult result,
        IReadOnlyList<Product> products,
        IReadOnlyDictionary<Guid, string> bundleNames,
        IReadOnlyDictionary<Guid, SaleLineFinancialSnapshot?> financialByProduct,
        IReadOnlyDictionary<Guid, SaleLineFinancialSnapshot?> financialByBundle)
    {
        var lines = new List<string>
        {
            "TIENDA MIRIAM",
            "Santa Cruz - Bolivia",
            $"Venta: {result.SaleId:N}",
            DateTime.Now.ToString("g"),
            "------------------------------"
        };
        if (!string.IsNullOrWhiteSpace(request.DiningTable))
        {
            lines.Add($"MESA: {request.DiningTable}");
        }
        if (!string.IsNullOrWhiteSpace(request.NamedOrder))
        {
            lines.Add($"ORDEN: {request.NamedOrder}");
        }
        lines.Add($"MODALIDAD: {request.FulfillmentMode switch
        {
            FulfillmentMode.DineIn => "Comer aquí",
            FulfillmentMode.Takeaway => "Para llevar",
            _ => "Mostrador"
        }}");
        if (request.Payments is { Count: > 1 })
        {
            foreach (var payment in request.Payments)
            {
                var method = payment.Method switch
                {
                    PaymentMethod.Cash => "Efectivo",
                    PaymentMethod.Transfer => "QR",
                    PaymentMethod.Custom => "Personalizado",
                    _ => payment.Method.ToString()
                };
                lines.Add($"{method}: {FormatMoney(payment.Amount)} Bs");
            }
        }
        foreach (var line in request.Lines!)
        {
            var name = line.PromotionBundleId is Guid bundleId
                ? bundleNames.GetValueOrDefault(bundleId) ?? "Combo promocional"
                : products.SingleOrDefault(product => product.Id == line.ProductId)?.Name
                    ?? line.ProductId.ToString("N");
            var lineTotal = decimal.Round(line.Quantity * line.UnitPriceAtSale, 2, MidpointRounding.AwayFromZero);
            lines.Add($"{line.Quantity:0.####} x {FormatMoney(line.UnitPriceAtSale)} = {FormatMoney(lineTotal)} Bs");
            lines.Add(name);
            if (!string.IsNullOrWhiteSpace(line.Notes))
            {
                lines.Add($"NOTA: {line.Notes.Trim()}");
            }
            var financial = line.PromotionBundleId is Guid saleBundleId
                ? financialByBundle.GetValueOrDefault(saleBundleId)
                : financialByProduct.GetValueOrDefault(line.ProductId);
            if (financial is not null
                && financial?.UnitCost is decimal unitCost
                && financial.GrossMargin is decimal grossMargin)
            {
                lines.Add($"Costo/u.: {FormatMoney(unitCost)} Bs | Utilidad: {FormatMoney(grossMargin)} Bs");
                lines.Add($"Margen bruto: {financial.GrossMarginPercent:0.00}%");
            }
            else
            {
                lines.Add("Costo sin configurar; utilidad no disponible.");
            }
        }
        lines.Add("------------------------------");
        lines.Add($"TOTAL: {FormatMoney(result.Total)} Bs");
        lines.Add($"PAGO: {request.PaymentMethod switch
        {
            PaymentMethod.Cash => "Efectivo",
            PaymentMethod.Transfer => "QR",
            PaymentMethod.CustomerWallet => "Monedero",
            PaymentMethod.Credit => "Fiado",
            _ => request.PaymentMethod.ToString()
        }}");
        lines.Add($"RECIBIDO: {FormatMoney(request.AmountTendered)} Bs");
        lines.Add($"CAMBIO: {FormatMoney(result.Change)} Bs");
        lines.Add("Gracias por su compra");
        return lines;
    }

    private async Task TrySyncAsync()
    {
        if (_syncing)
        {
            return;
        }
        var apiUrl = Environment.GetEnvironmentVariable("TIENDA_CLOUD_API_URL");
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return;
        }
        var allowInsecureLoopback = string.Equals(
            Environment.GetEnvironmentVariable("TIENDA_ALLOW_INSECURE_LOCAL_API"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!CloudApiUrlPolicy.TryValidate(apiUrl, allowInsecureLoopback, out var apiUri)
            || apiUri is null)
        {
            _status.Text = "La URL de sincronización debe usar HTTPS. HTTP sólo se admite para loopback local con TIENDA_ALLOW_INSECURE_LOCAL_API=true.";
            return;
        }

        _syncing = true;
        try
        {
            using var client = new HttpClient { BaseAddress = apiUri, Timeout = TimeSpan.FromSeconds(8) };
            var accessToken = await GetCloudAccessTokenAsync(client);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _status.Text = "Configura el usuario de caja para sincronizar con la nube.";
                return;
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            await using var db = new TiendaDbContext(_options);
            await PromotionBundleOutboxSync.SyncPendingAsync(client, db);
            var pendingProduction = await db.StockProductionBatches
                .Where(batch => batch.SyncPending)
                .OrderBy(batch => batch.OccurredAtUtc)
                .ToListAsync();
            foreach (var batch in pendingProduction)
            {
                var ingredients = await db.StockProductionLines.AsNoTracking()
                    .Where(line => line.ProductionBatchId == batch.Id)
                    .OrderBy(line => line.StockItemId)
                    .Select(line => new ProductionIngredientInput(line.StockItemId, line.QuantityConsumed))
                    .ToListAsync();
                var request = new RecordProductionRequest(
                    batch.Id, batch.ProductId, batch.QuantityProduced, ingredients);
                using var response = await client.PostAsJsonAsync("/api/sync/production", request);
                if (!response.IsSuccessStatusCode)
                {
                    _status.Text =
                        $"No se pudo sincronizar el lote de producción {batch.Id} (HTTP {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}";
                    return;
                }
                batch.SyncPending = false;
                await db.SaveChangesAsync();
            }
            var queued = await db.SyncOutbox.Where(item => !item.IsDelivered).ToListAsync();
            foreach (var batch in queued.Chunk(500))
            {
                using var knownResponse = await client.PostAsJsonAsync(
                    "/api/sync/sales/status",
                    new SyncSaleStatusRequest(batch.Select(item => item.SaleId).ToArray()));
                if (!knownResponse.IsSuccessStatusCode)
                {
                    _status.Text = $"No se pudo reconciliar la cola de ventas (HTTP {(int)knownResponse.StatusCode}).";
                    return;
                }
                var knownIds = await knownResponse.Content.ReadFromJsonAsync<Guid[]>() ?? [];
                var known = knownIds.ToHashSet();
                foreach (var item in batch.Where(item => known.Contains(item.SaleId)))
                {
                    item.IsDelivered = true;
                    item.LastError = null;
                    await RemoveClosedHeldOrderAsync(db, item);
                }
            }
            await db.SaveChangesAsync();
            var pendingSaleIds = await db.SyncOutbox.Where(item => !item.IsDelivered)
                .Select(item => item.SaleId)
                .ToListAsync();
            using (var catalogResponse = await client.GetAsync("/api/catalog"))
            {
                if (!catalogResponse.IsSuccessStatusCode)
                {
                    _status.Text = $"Catálogo no sincronizado (HTTP {(int)catalogResponse.StatusCode}); ventas locales permanecen guardadas.";
                    return;
                }
                var snapshot = await catalogResponse.Content.ReadFromJsonAsync<CatalogSnapshotResponse>();
                if (snapshot is null)
                {
                    throw new InvalidOperationException("La API devolvió un catálogo vacío o inválido.");
                }
                await ApplyCatalogSnapshotAsync(db, snapshot, pendingSaleIds);
            }
            if (!await SyncDiningTablesAsync(client, db))
            {
                return;
            }
            await CustomerChangeOutboxSync.SyncPendingAsync(
                client, db, CustomerSyncChangeKind.ProfileUpsert);
            var pending = await db.SyncOutbox.Where(item => !item.IsDelivered)
                .OrderBy(item => item.CreatedAtUtc)
                .Take(20)
                .ToListAsync();
            foreach (var item in pending)
            {
                try
                {
                    using var payload = JsonDocument.Parse(item.Payload);
                    using var response = await client.PostAsJsonAsync("/api/sync/sales", payload.RootElement);
                    if (!response.IsSuccessStatusCode)
                    {
                        item.LastError = $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
                        item.Attempts++;
                        continue;
                    }

                    item.IsDelivered = true;
                    item.LastError = null;
                    await RemoveClosedHeldOrderAsync(db, item);
                }
                catch (HttpRequestException exception)
                {
                    item.LastError = exception.Message;
                    item.Attempts++;
                    break;
                }
                catch (TaskCanceledException exception)
                {
                    item.LastError = $"Tiempo de espera agotado: {exception.Message}";
                    item.Attempts++;
                    break;
                }
                catch (JsonException exception)
                {
                    item.LastError = $"Outbox inválido: {exception.Message}";
                    item.Attempts++;
                }
            }
            await db.SaveChangesAsync();
            await CustomerChangeOutboxSync.SyncPendingAsync(
                client, db, CustomerSyncChangeKind.CreditPayment);
            var waiting = await db.SyncOutbox.CountAsync(item => !item.IsDelivered);
            if (waiting == 0)
            {
                await SyncCustomersAsync(client, db);
                await SyncPaymentTypesAsync(client, db);
            }
            HttpResponseMessage statusResponse;
            try
            {
                statusResponse = await client.PostAsJsonAsync(
                    "/api/sync/status",
                    new TerminalStatusUpdate(_terminalId, waiting));
            }
            catch (HttpRequestException exception)
            {
                _status.Text = $"No se pudo actualizar el estado remoto: {exception.Message}";
                return;
            }
            catch (TaskCanceledException exception)
            {
                _status.Text = $"Tiempo de espera agotado al actualizar el estado remoto: {exception.Message}";
                return;
            }
            using (statusResponse)
            {
                if (!statusResponse.IsSuccessStatusCode)
                {
                    _status.Text = $"No se pudo actualizar el estado de la caja (HTTP {(int)statusResponse.StatusCode}).";
                    return;
                }
            }
            var pendingProductionCount = await db.StockProductionBatches.CountAsync(batch => batch.SyncPending);
            _status.Text = waiting == 0 && pendingProductionCount == 0
                ? "Caja sincronizada con la nube"
                : $"Pendientes de sincronización: {waiting} ventas y {pendingProductionCount} lotes de producción";
            await LoadCatalogAsync();
        }
        catch (HttpRequestException exception)
        {
            _status.Text = $"Venta guardada localmente; nube no disponible: {exception.Message}";
        }
        catch (TaskCanceledException exception)
        {
            _status.Text = $"Venta guardada localmente; conexión agotó el tiempo de espera: {exception.Message}";
        }
        catch (JsonException exception)
        {
            _status.Text = $"Venta guardada localmente; respuesta remota inválida: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            _status.Text = $"Venta guardada localmente; sincronización detenida: {exception.Message}";
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"Venta local guardada; sincronización falló: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Venta local guardada; error en SQLite: {exception.Message}";
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task<bool> SyncDiningTablesAsync(HttpClient client, TiendaDbContext db)
    {
        var pending = await db.HeldSales
            .Where(item => item.DiningTable != null && item.SyncPending && !item.DeletePending)
            .OrderBy(item => item.UpdatedAtUtc)
            .ToListAsync();
        foreach (var local in pending)
        {
            var request = new HoldSaleRequest(
                local.Id,
                _terminalId,
                local.Payload,
                local.DiningTable,
                local.KitchenNotes,
                local.NamedOrder,
                local.FulfillmentMode,
                local.PriceListId,
                local.CustomerId,
                local.ExpectedVersion,
                local.StartedAtUtc is null
                    ? null
                    : new DateTimeOffset(local.StartedAtUtc.Value, TimeSpan.Zero),
                local.CheckoutRequested);
            using var response = await client.PutAsJsonAsync(
                $"/api/held-sales/{local.Id}", request, CloudJsonOptions);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _status.Text = $"Conflicto en {local.DiningTable}: otra caja actualizó la orden. La copia local se conserva; recarga la mesa antes de continuar.";
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                _status.Text = $"No se sincronizó la orden de {local.DiningTable} (HTTP {(int)response.StatusCode}); la copia local se conserva.";
                return false;
            }

            var updated = await response.Content.ReadFromJsonAsync<HeldSaleResponse>(CloudJsonOptions)
                ?? throw new InvalidOperationException("La API guardó la orden pero no devolvió su versión compartida.");
            ApplyHeldSale(local, updated);
            local.SyncPending = false;
        }

        var pendingDeletions = await db.HeldSales
            .Where(item => item.DeletePending)
            .OrderBy(item => item.UpdatedAtUtc)
            .ToListAsync();
        foreach (var local in pendingDeletions)
        {
            var terminal = Uri.EscapeDataString(local.TerminalId);
            var expectedVersion = local.SyncPending ? local.ExpectedVersion : local.Version;
            using var response = await client.DeleteAsync(
                $"/api/held-sales/{local.Id}?terminalId={terminal}&expectedVersion={expectedVersion}");
            if (!response.IsSuccessStatusCode
                && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                _status.Text = "No se eliminó una comanda combinada en la nube (HTTP "
                    + (int)response.StatusCode + "); se reintentará.";
                return false;
            }
            db.HeldSales.Remove(local);
            await db.SaveChangesAsync();
        }

        var query = Uri.EscapeDataString(_terminalId);
        using var listResponse = await client.GetAsync($"/api/held-sales?terminalId={query}");
        if (!listResponse.IsSuccessStatusCode)
        {
            _status.Text = $"No se pudo actualizar el plano de mesas compartido (HTTP {(int)listResponse.StatusCode}).";
            return false;
        }
        var remote = await listResponse.Content.ReadFromJsonAsync<List<HeldSaleResponse>>(CloudJsonOptions) ?? [];
        var remoteTables = remote.Where(item => item.DiningTable is not null)
            .ToDictionary(item => item.Id);
        var localTables = await db.HeldSales
            .Where(item => item.DiningTable != null && !item.SyncPending && !item.DeletePending)
            .ToListAsync();
        foreach (var local in localTables)
        {
            if (!remoteTables.ContainsKey(local.Id))
            {
                db.HeldSales.Remove(local);
            }
        }
        foreach (var shared in remoteTables.Values)
        {
            var local = await db.HeldSales.SingleOrDefaultAsync(item => item.Id == shared.Id);
            if (local is null)
            {
                db.HeldSales.Add(new HeldSale
                {
                    Id = shared.Id,
                    TerminalId = shared.TerminalId,
                    Payload = shared.Payload,
                    DiningTable = shared.DiningTable,
                    KitchenNotes = shared.KitchenNotes,
                    NamedOrder = shared.NamedOrder,
                    FulfillmentMode = shared.FulfillmentMode,
                    PriceListId = shared.PriceListId,
                    CustomerId = shared.CustomerId,
                    UpdatedAtUtc = shared.UpdatedAt.UtcDateTime,
                    StartedAtUtc = shared.StartedAt?.UtcDateTime,
                    Version = shared.Version,
                    SyncPending = false,
                    CheckoutRequested = shared.CheckoutRequested
                });
            }
            else if (!local.SyncPending && shared.Version >= local.Version)
            {
                ApplyHeldSale(local, shared);
            }
        }
        await db.SaveChangesAsync();
        return true;
    }

    private static void ApplyHeldSale(HeldSale local, HeldSaleResponse remote)
    {
        local.TerminalId = remote.TerminalId;
        local.Payload = remote.Payload;
        local.DiningTable = remote.DiningTable;
        local.KitchenNotes = remote.KitchenNotes;
        local.NamedOrder = remote.NamedOrder;
        local.FulfillmentMode = remote.FulfillmentMode;
        local.PriceListId = remote.PriceListId;
        local.CustomerId = remote.CustomerId;
        local.UpdatedAtUtc = remote.UpdatedAt.UtcDateTime;
        local.StartedAtUtc = remote.StartedAt?.UtcDateTime;
        local.Version = remote.Version;
        local.ExpectedVersion = remote.Version;
        local.CheckoutRequested = remote.CheckoutRequested;
    }

    private static async Task RemoveClosedHeldOrderAsync(TiendaDbContext db, SyncOutboxItem outboxItem)
    {
        var request = JsonSerializer.Deserialize<CreateSaleRequest>(outboxItem.Payload);
        if (request?.HeldSaleId is not Guid heldSaleId)
        {
            return;
        }

        var held = await db.HeldSales.SingleOrDefaultAsync(item => item.Id == heldSaleId && item.ClosePending);
        if (held is not null)
        {
            db.HeldSales.Remove(held);
        }
    }

    private static async Task ApplyCatalogSnapshotAsync(
        TiendaDbContext db, CatalogSnapshotResponse snapshot, IReadOnlyCollection<Guid> pendingSaleIds)
    {
        var productIds = snapshot.Products.Select(item => item.Id).ToArray();
        if (snapshot.Products.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Sku)
            || string.IsNullOrWhiteSpace(item.Name) || item.Price < 0))
        {
            throw new InvalidOperationException("El catálogo remoto contiene productos inválidos.");
        }
        var localProducts = await db.Products.ToDictionaryAsync(item => item.Id);
        foreach (var product in snapshot.Products)
        {
            if (localProducts.TryGetValue(product.Id, out var local))
            {
                local.Sku = product.Sku;
                local.Name = product.Name;
                local.Barcode = product.Barcode;
                local.Price = product.Price;
                local.IsActive = product.IsActive;
                local.StockItemId = product.StockItemId;
                local.IsRecipe = product.IsRecipe;
            }
            else
            {
                db.Products.Add(new Product
                {
                    Id = product.Id,
                    Sku = product.Sku,
                    Name = product.Name,
                    Barcode = product.Barcode,
                    Price = product.Price,
                    IsActive = product.IsActive,
                    StockItemId = product.StockItemId,
                    IsRecipe = product.IsRecipe
                });
            }
            var details = await db.ProductDetails.SingleOrDefaultAsync(item => item.ProductId == product.Id);
            if (details is null)
            {
                db.ProductDetails.Add(new ProductDetails
                {
                    ProductId = product.Id,
                    Department = product.Department,
                    PrintStation = product.PrintStation
                });
            }
            else
            {
                details.Department = product.Department;
                details.PrintStation = product.PrintStation;
            }
        }

        var localStock = await db.StockItems.ToDictionaryAsync(item => item.Id);
        var pendingProductionBatchIds = await db.StockProductionBatches.AsNoTracking()
            .Where(batch => batch.SyncPending)
            .Select(batch => batch.Id)
            .ToListAsync();
        var pendingStockDeltas = await db.StockMovements.AsNoTracking()
            .Where(movement => pendingSaleIds.Contains(movement.SaleId)
                || movement.ProductionBatchId != null
                    && pendingProductionBatchIds.Contains(movement.ProductionBatchId.Value))
            .GroupBy(movement => movement.StockItemId)
            .Select(group => new { StockItemId = group.Key, Delta = group.Sum(movement => movement.QuantityDelta) })
            .ToDictionaryAsync(item => item.StockItemId, item => item.Delta);
        foreach (var item in snapshot.StockItems)
        {
            var localQuantity = item.OnHand + pendingStockDeltas.GetValueOrDefault(item.Id);
            if (localStock.TryGetValue(item.Id, out var local))
            {
                local.Name = item.Name;
                local.Unit = item.Unit;
                local.ReorderAt = item.ReorderAt;
                local.UnitCost = item.UnitCost;
                local.OnHand = localQuantity;
            }
            else
            {
                db.StockItems.Add(new StockItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    Unit = item.Unit,
                    ReorderAt = item.ReorderAt,
                    UnitCost = item.UnitCost,
                    OnHand = localQuantity
                });
            }
        }
        var pendingLotDeltas = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.StockLotId != null
                && (pendingSaleIds.Contains(movement.SaleId)
                    || movement.ProductionBatchId != null
                        && pendingProductionBatchIds.Contains(movement.ProductionBatchId.Value)))
            .GroupBy(movement => movement.StockLotId!.Value)
            .Select(group => new { LotId = group.Key, Delta = group.Sum(movement => movement.QuantityDelta) })
            .ToDictionaryAsync(item => item.LotId, item => item.Delta);
        var localLots = await db.StockLots.ToDictionaryAsync(item => item.Id);
        foreach (var lot in snapshot.Lots ?? [])
        {
            if (!snapshot.StockItems.Any(item => item.Id == lot.StockItemId)
                || lot.ReceivedQuantity < 0 || lot.RemainingQuantity < 0
                || lot.RemainingQuantity > lot.ReceivedQuantity)
            {
                throw new InvalidOperationException("El catálogo contiene un lote de inventario inválido.");
            }
            var remaining = Math.Max(0m, lot.RemainingQuantity + pendingLotDeltas.GetValueOrDefault(lot.Id));
            if (localLots.TryGetValue(lot.Id, out var local))
            {
                local.StockItemId = lot.StockItemId;
                local.LotCode = lot.LotCode;
                local.ReceivedQuantity = lot.ReceivedQuantity;
                local.RemainingQuantity = remaining;
                local.UnitCost = lot.UnitCost;
                local.ReceivedAtUtc = lot.ReceivedAt.UtcDateTime;
                local.ExpiresAtUtc = lot.ExpiresAt?.UtcDateTime;
            }
            else
            {
                db.StockLots.Add(new StockLot
                {
                    Id = lot.Id,
                    StockItemId = lot.StockItemId,
                    LotCode = lot.LotCode,
                    ReceivedQuantity = lot.ReceivedQuantity,
                    RemainingQuantity = remaining,
                    UnitCost = lot.UnitCost,
                    ReceivedAtUtc = lot.ReceivedAt.UtcDateTime,
                    ExpiresAtUtc = lot.ExpiresAt?.UtcDateTime
                });
            }
        }
        var remotePriceLists = snapshot.PriceLists ?? [];
        var remoteListPrices = snapshot.ListPrices ?? [];
        var remotePromotions = snapshot.Promotions ?? [];
        var remoteBundles = snapshot.PromotionBundles ?? [];
        if (remotePriceLists.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name))
            || remotePriceLists.Select(item => item.Id).Distinct().Count() != remotePriceLists.Count
            || remoteListPrices.Select(item => (item.PriceListId, item.ProductId)).Distinct().Count()
                != remoteListPrices.Count
            || remoteListPrices.Any(item => item.Price < 0
                || decimal.Round(item.Price, 2) != item.Price
                || !remotePriceLists.Any(list => list.Id == item.PriceListId)
                || !productIds.Contains(item.ProductId))
            || remotePromotions.Select(item => item.Id).Distinct().Count() != remotePromotions.Count
            || remotePromotions.Any(item => item.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(item.Name)
                || !productIds.Contains(item.ProductId)
                || !Enum.IsDefined(item.Kind)
                || item.Value < 0
                || item.Kind == PromotionKind.PercentOff && item.Value is <= 0 or > 100
                || item.StartsAt >= item.EndsAt)
            || remoteBundles.Select(item => item.Id).Distinct().Count() != remoteBundles.Count
            || remoteBundles.Any(bundle => bundle.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(bundle.Name)
                || bundle.Price < 0
                || bundle.Components.Count == 0
                || bundle.Components.Select(component => component.ProductId).Distinct().Count()
                    != bundle.Components.Count
                || bundle.Components.Any(component => component.Quantity <= 0
                    || !productIds.Contains(component.ProductId))
                || bundle.StartsAt is not null && bundle.EndsAt is not null
                    && bundle.StartsAt >= bundle.EndsAt))
        {
            throw new InvalidOperationException("Las listas de precios o promociones del catálogo remoto son inválidas.");
        }

        var localPriceLists = await db.PriceLists.ToDictionaryAsync(item => item.Id);
        foreach (var local in localPriceLists.Values)
        {
            local.IsActive = false;
        }
        foreach (var remote in remotePriceLists)
        {
            if (localPriceLists.TryGetValue(remote.Id, out var local))
            {
                local.Name = remote.Name;
                local.IsActive = remote.IsActive;
            }
            else
            {
                db.PriceLists.Add(new PriceList
                {
                    Id = remote.Id,
                    Name = remote.Name,
                    IsActive = remote.IsActive
                });
            }
        }

        var localListPrices = await db.ProductListPrices.ToListAsync();
        var remoteListPriceKeys = remoteListPrices
            .Select(item => (item.PriceListId, item.ProductId)).ToHashSet();
        db.ProductListPrices.RemoveRange(localListPrices.Where(item =>
            !remoteListPriceKeys.Contains((item.PriceListId, item.ProductId))));
        foreach (var remote in remoteListPrices)
        {
            var local = localListPrices.SingleOrDefault(item =>
                item.PriceListId == remote.PriceListId && item.ProductId == remote.ProductId);
            if (local is null)
            {
                db.ProductListPrices.Add(new ProductListPrice
                {
                    PriceListId = remote.PriceListId,
                    ProductId = remote.ProductId,
                    Price = remote.Price
                });
            }
            else
            {
                local.Price = remote.Price;
            }
        }

        var localPromotions = await db.Promotions.ToDictionaryAsync(item => item.Id);
        foreach (var local in localPromotions.Values)
        {
            local.IsActive = false;
        }
        foreach (var remote in remotePromotions)
        {
            if (localPromotions.TryGetValue(remote.Id, out var local))
            {
                local.ProductId = remote.ProductId;
                local.Name = remote.Name;
                local.Kind = remote.Kind;
                local.Value = remote.Value;
                local.StartsAtUtc = remote.StartsAt.UtcDateTime;
                local.EndsAtUtc = remote.EndsAt.UtcDateTime;
                local.IsActive = remote.IsActive;
            }
            else
            {
                db.Promotions.Add(new Promotion
                {
                    Id = remote.Id,
                    ProductId = remote.ProductId,
                    Name = remote.Name,
                    Kind = remote.Kind,
                    Value = remote.Value,
                    StartsAtUtc = remote.StartsAt.UtcDateTime,
                    EndsAtUtc = remote.EndsAt.UtcDateTime,
                    IsActive = remote.IsActive
                });
            }
        }
        var locallyPendingBundleIds = await db.PromotionBundleOutbox
            .Where(item => !item.IsDelivered)
            .Select(item => item.PromotionBundleId)
            .Distinct()
            .ToListAsync();
        var localBundles = await db.PromotionBundles.Include(bundle => bundle.Components)
            .ToDictionaryAsync(bundle => bundle.Id);
        foreach (var remote in remoteBundles.Where(item => !locallyPendingBundleIds.Contains(item.Id)))
        {
            if (localBundles.TryGetValue(remote.Id, out var local))
            {
                local.Name = remote.Name;
                local.Price = remote.Price;
                local.StartsAtUtc = remote.StartsAt?.UtcDateTime;
                local.EndsAtUtc = remote.EndsAt?.UtcDateTime;
                local.IsActive = remote.IsActive;
                local.IsDeleted = remote.IsDeleted;
                local.CreatedAtUtc = remote.CreatedAt.UtcDateTime;
                local.UpdatedAtUtc = DateTime.UtcNow;
                local.DeletedAtUtc = remote.DeletedAt?.UtcDateTime;
                var existingComponents = local.Components
                    .Select(component => (component.ProductId, component.Quantity))
                    .OrderBy(component => component.ProductId)
                    .ToArray();
                var remoteComponents = remote.Components
                    .Select(component => (component.ProductId, component.Quantity))
                    .OrderBy(component => component.ProductId)
                    .ToArray();
                if (!existingComponents.SequenceEqual(remoteComponents))
                {
                    if (await db.SaleLines.AnyAsync(line => line.PromotionBundleId == remote.Id))
                    {
                        throw new InvalidOperationException(
                            $"No se puede reemplazar el historial del combo '{remote.Name}'.");
                    }
                    var remoteComponentsByProduct = remoteComponents.ToDictionary(
                        component => component.ProductId, component => component.Quantity);
                    foreach (var stale in local.Components
                                 .Where(component => !remoteComponentsByProduct.ContainsKey(component.ProductId))
                                 .ToArray())
                    {
                        db.PromotionBundleComponents.Remove(stale);
                        local.Components.Remove(stale);
                    }
                    foreach (var component in remoteComponents)
                    {
                        var current = local.Components.SingleOrDefault(
                            item => item.ProductId == component.ProductId);
                        if (current is null)
                        {
                            local.Components.Add(new PromotionBundleComponent
                            {
                                PromotionBundleId = remote.Id,
                                ProductId = component.ProductId,
                                Quantity = component.Quantity
                            });
                        }
                        else
                        {
                            current.Quantity = component.Quantity;
                        }
                    }
                }
            }
            else
            {
                var localBundle = new PromotionBundle
                {
                    Id = remote.Id,
                    Name = remote.Name,
                    Price = remote.Price,
                    StartsAtUtc = remote.StartsAt?.UtcDateTime,
                    EndsAtUtc = remote.EndsAt?.UtcDateTime,
                    IsActive = remote.IsActive,
                    IsDeleted = remote.IsDeleted,
                    CreatedAtUtc = remote.CreatedAt.UtcDateTime,
                    UpdatedAtUtc = remote.CreatedAt.UtcDateTime,
                    DeletedAtUtc = remote.DeletedAt?.UtcDateTime,
                    Components = remote.Components.Select(component => new PromotionBundleComponent
                    {
                        PromotionBundleId = remote.Id,
                        ProductId = component.ProductId,
                        Quantity = component.Quantity
                    }).ToList()
                };
                db.PromotionBundles.Add(localBundle);
            }
        }
        if (snapshot.Recipes.Any(item => item.Quantity <= 0
            || !productIds.Contains(item.ProductId)
            || !snapshot.StockItems.Any(stock => stock.Id == item.StockItemId)))
        {
            throw new InvalidOperationException("Las recetas del catálogo remoto contienen referencias inválidas.");
        }
        var existingRecipes = await db.RecipeComponents.ToListAsync();
        var remoteRecipeKeys = snapshot.Recipes.Select(item => (item.ProductId, item.StockItemId)).ToHashSet();
        db.RecipeComponents.RemoveRange(existingRecipes.Where(item =>
            !remoteRecipeKeys.Contains((item.ProductId, item.StockItemId))));
        foreach (var recipe in snapshot.Recipes)
        {
            var existing = existingRecipes.SingleOrDefault(item =>
                item.ProductId == recipe.ProductId && item.StockItemId == recipe.StockItemId);
            if (existing is not null)
            {
                existing.Quantity = recipe.Quantity;
            }
            else
            {
                db.RecipeComponents.Add(new RecipeComponent
                {
                    ProductId = recipe.ProductId,
                    StockItemId = recipe.StockItemId,
                    Quantity = recipe.Quantity
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private static async Task SyncCustomersAsync(HttpClient client, TiendaDbContext db)
    {
        using var response = await client.GetAsync("/api/customers");
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"No se pudo sincronizar clientes (HTTP {(int)response.StatusCode}).");
        }
        var customers = await response.Content.ReadFromJsonAsync<List<CustomerResponse>>();
        if (customers is null)
        {
            throw new InvalidOperationException("La API devolvió un listado de clientes inválido.");
        }
        var local = await db.Customers.ToDictionaryAsync(item => item.Id);
        foreach (var customer in customers)
        {
            if (local.TryGetValue(customer.Id, out var current))
            {
                current.Name = customer.Name;
                current.Phone = customer.Phone;
                current.Address = customer.Address;
                current.TaxId = customer.TaxId;
                current.Notes = customer.Notes;
                current.WalletBalance = customer.WalletBalance;
                current.CreditBalance = customer.CreditBalance;
                current.CreditLimit = customer.CreditLimit;
                current.IsActive = true;
            }
            else
            {
                db.Customers.Add(new CustomerAccount
                {
                    Id = customer.Id,
                    Name = customer.Name,
                    Phone = customer.Phone,
                    Address = customer.Address,
                    TaxId = customer.TaxId,
                    Notes = customer.Notes,
                    WalletBalance = customer.WalletBalance,
                    CreditBalance = customer.CreditBalance,
                    CreditLimit = customer.CreditLimit,
                    IsActive = true
                });
            }
        }
        var activeIds = customers.Select(item => item.Id).ToHashSet();
        foreach (var removed in local.Values.Where(item => !activeIds.Contains(item.Id)))
        {
            removed.IsActive = false;
        }
        await db.SaveChangesAsync();
    }

    private static async Task SyncPaymentTypesAsync(HttpClient client, TiendaDbContext db)
    {
        using var response = await client.GetAsync("/api/payment-types");
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"No se pudieron sincronizar medios de pago (HTTP {(int)response.StatusCode}).");
        }
        var items = await response.Content.ReadFromJsonAsync<List<CustomPaymentTypeResponse>>();
        if (items is null)
        {
            throw new InvalidOperationException("La API devolvió medios de pago inválidos.");
        }
        var local = await db.CustomPaymentTypes.ToDictionaryAsync(item => item.Id);
        foreach (var item in items)
        {
            if (local.TryGetValue(item.Id, out var current))
            {
                current.Name = item.Name;
                current.IsActive = item.IsActive;
            }
            else
            {
                db.CustomPaymentTypes.Add(new CustomPaymentType
                {
                    Id = item.Id,
                    Name = item.Name,
                    IsActive = item.IsActive
                });
            }
        }
        var currentIds = items.Select(item => item.Id).ToHashSet();
        foreach (var removed in local.Values.Where(item => !currentIds.Contains(item.Id)))
        {
            removed.IsActive = false;
        }
        await db.SaveChangesAsync();
    }

    private async Task<string?> GetCloudAccessTokenAsync(HttpClient client)
    {
        var configuredToken = Environment.GetEnvironmentVariable("TIENDA_CLOUD_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return configuredToken;
        }
        if (!string.IsNullOrWhiteSpace(_cachedCloudToken)
            && _cloudTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _cachedCloudToken;
        }

        var username = Environment.GetEnvironmentVariable("TIENDA_CLOUD_USERNAME");
        var password = Environment.GetEnvironmentVariable("TIENDA_CLOUD_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(username, password));
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"No se pudo autenticar la caja con la API (HTTP {(int)response.StatusCode}).");
        }
        var session = await response.Content.ReadFromJsonAsync<LoginResponse>();
        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException("La API devolvió una sesión inválida.");
        }
        _cachedCloudToken = session.AccessToken;
        _cloudTokenExpiresAt = session.ExpiresAt;
        return _cachedCloudToken;
    }

    private sealed record FulfillmentOption(FulfillmentMode Mode, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record PaymentOption(PaymentMethod Method, Guid? PaymentTypeId, string Name);
    private sealed record PriceListChoice(Guid? Id, string Name);
}
