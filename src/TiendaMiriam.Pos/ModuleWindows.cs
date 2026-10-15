using System.ComponentModel;
using System.Drawing.Printing;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed class LauncherForm : Form
{
    private static readonly Color Surface = ModernTheme.Surface;
    private static readonly Color Accent = ModernTheme.Orange;
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly NavigationManager _navigation;
    private readonly Func<Task>? _reloadVisualSettings;
    private readonly ToolTip _logoToolTip = new();
    private readonly Button _changeLogoButton;
    private readonly PictureBox _brandLogo = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = ModernTheme.SurfaceMuted,
        Margin = new Padding(4)
    };

    public LauncherForm(
        DbContextOptions<TiendaDbContext> options,
        NavigationManager navigation,
        Func<Task>? reloadVisualSettings = null)
    {
        _options = options;
        _navigation = navigation;
        _reloadVisualSettings = reloadVisualSettings;
        Text = "Tienda Miriam | Inicio";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(920, 680);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(32),
            ColumnCount = 2,
            RowCount = 4,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        for (var row = 1; row < 4; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        }

        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = BackColor
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        brand.Controls.Add(_brandLogo, 0, 0);
        brand.Controls.Add(new Label
        {
            Text = "TIENDA MIRIAM\nSistema de gestión · Santa Cruz, Bolivia",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.OrangeDark,
            Font = new Font("Segoe UI", 19, FontStyle.Bold)
        }, 1, 0);
        _changeLogoButton = new Button { Text = "Cambiar logotipo…", Dock = DockStyle.Fill, Margin = new Padding(10) };
        _logoToolTip.SetToolTip(_changeLogoButton,
            "Elige una imagen del equipo o un teléfono conectado. Para una imagen de internet, descárgala primero.");
        DataModuleWindow.StyleButton(_changeLogoButton);
        _changeLogoButton.Click += async (_, _) => await SelectLogoAsync();
        brand.Controls.Add(_changeLogoButton, 2, 0);
        layout.Controls.Add(brand, 0, 0);
        layout.SetColumnSpan(brand, 2);
        AddModuleButton(layout, "VENTAS", "Punto de venta y ventas en espera", 0, 1, "sales");
        AddModuleButton(layout, "INVENTARIO", "Existencias, costos y márgenes", 1, 1, "inventory");
        AddModuleButton(layout, "HISTORIAL DE VENTAS", "Ventas registradas durante el día", 0, 2, "cash");
        AddModuleButton(layout, "PROVEEDORES", "Compras e historial de costos", 1, 2, "suppliers");
        AddModuleButton(layout, "REPORTES", "Ventas, inversión, gastos y utilidad", 0, 3, "reports");
        AddModuleButton(layout, "CIERRES DE CAJA · NUBE", "Arqueos consolidados de todas las terminales", 1, 3, "cash-closures");
        var footerActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = BackColor,
            Padding = new Padding(0, 8, 0, 0)
        };
        var backupButton = new Button
        {
            Text = "Respaldar base local…",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        _logoToolTip.SetToolTip(backupButton,
            "Guarda una copia consistente de SQLite; el archivo incluye ventas y datos de clientes.");
        DataModuleWindow.StyleButton(backupButton);
        backupButton.Click += async (_, _) => await CreateDatabaseBackupAsync();
        footerActions.Controls.Add(backupButton);
        if (_reloadVisualSettings is not null)
        {
            var visualSettingsButton = new Button
            {
                Text = "Pantalla de espera y animaciones…",
                AutoSize = true,
                Margin = new Padding(8, 0, 0, 0)
            };
            DataModuleWindow.StyleButton(visualSettingsButton);
            visualSettingsButton.Click += async (_, _) => await ConfigureVisualExperienceAsync();
            footerActions.Controls.Add(visualSettingsButton);
        }
        var footer = new Label
        {
            Text = "Usa la barra superior para cambiar de módulo sin perder tu trabajo.",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 8, 0, 0),
            ForeColor = ModernTheme.Muted,
            Font = new Font("Segoe UI", 10)
        };
        footerActions.Controls.Add(footer);
        layout.Controls.Add(footerActions, 1, 3);
        Controls.Add(layout);
        Shown += async (_, _) => await LoadLogoAsync();
    }

    private async Task ConfigureVisualExperienceAsync()
    {
        try
        {
            var settings = await IdleScreenSettingsStore.LoadAsync(_options);
            using var dialog = new IdleScreenSettingsDialog(settings);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            await IdleScreenSettingsStore.SaveAsync(_options, dialog.Settings);
            await _reloadVisualSettings!();
            MessageBox.Show(this, "Se guardó la configuración de pantalla y animaciones.",
                "Configuración visual", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(this, exception.Message, "Configuración visual",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (DbUpdateException exception)
        {
            MessageBox.Show(this, $"No se pudo guardar la configuración: {exception.GetBaseException().Message}",
                "Configuración visual", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SqliteException exception)
        {
            MessageBox.Show(this, $"Error SQLite al guardar la configuración: {exception.Message}",
                "Configuración visual", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CreateDatabaseBackupAsync()
    {
        var confirmation = MessageBox.Show(this,
            "El respaldo contiene ventas y datos de clientes. Guárdalo en una ubicación protegida. ¿Continuar?",
            "Respaldo de base local", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Guardar respaldo de Tienda Miriam",
            Filter = "Base de datos SQLite (*.db)|*.db",
            DefaultExt = "db",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = $"tienda-miriam-{DateTime.Now:yyyyMMdd-HHmmss}.db"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            await using var db = new TiendaDbContext(_options);
            var connectionString = db.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("No se pudo obtener la conexión de la base local.");
            }

            await SqliteBackupService.CreateBackupAsync(connectionString, dialog.FileName);
            MessageBox.Show(this, "El respaldo SQLite se creó y superó la verificación de integridad.",
                "Respaldo completado", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, $"No se pudo crear el respaldo: {exception.Message}",
                "Respaldo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, $"Error de archivo al crear el respaldo: {exception.Message}",
                "Respaldo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(this, $"No hay permiso para guardar el respaldo: {exception.Message}",
                "Respaldo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SqliteException exception)
        {
            MessageBox.Show(this, $"Error SQLite al crear el respaldo: {exception.Message}",
                "Respaldo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, $"No se pudo iniciar el respaldo: {exception.Message}",
                "Respaldo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task LoadLogoAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            var asset = await db.BusinessAssets.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Key == DemoDataSeeder.LogoAssetKey);
            if (asset is not null)
            {
                SetLogo(asset.Data);
            }
        }
        catch (DbUpdateException exception)
        {
            MessageBox.Show(this, $"No se pudo cargar el logotipo: {exception.GetBaseException().Message}",
                "Logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SqliteException exception)
        {
            MessageBox.Show(this, $"Error SQLite al cargar el logotipo: {exception.Message}",
                "Logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (OutOfMemoryException)
        {
            MessageBox.Show(this, "El logotipo guardado no es una imagen válida o no se pudo decodificar.",
                "Logotipo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task SelectLogoAsync()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Seleccionar logotipo de Tienda Miriam",
                Filter = "Imágenes compatibles|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Todos los archivos|*.*",
                FilterIndex = 1,
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            _changeLogoButton.Enabled = false;
            _changeLogoButton.Text = "Procesando imagen…";
            UseWaitCursor = true;
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length is <= 0 or > 5_242_880)
            {
                throw new InvalidOperationException("El logotipo debe pesar entre 1 byte y 5 MB.");
            }
            var sourceBytes = await File.ReadAllBytesAsync(dialog.FileName);
            var bytes = await Task.Run(() => DemoBrandingAssets.NormalizePng(sourceBytes));
            await using var db = new TiendaDbContext(_options);
            var asset = await db.BusinessAssets.SingleOrDefaultAsync(item => item.Key == DemoDataSeeder.LogoAssetKey);
            if (asset is null)
            {
                db.BusinessAssets.Add(new BusinessAsset
                {
                    Key = DemoDataSeeder.LogoAssetKey,
                    ContentType = "image/png",
                    Data = bytes,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                asset.ContentType = "image/png";
                asset.Data = bytes;
                asset.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            SetLogo(bytes);
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, $"No se pudo leer la imagen: {exception.Message}",
                "Error de logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(this, $"No hay permiso para leer la imagen: {exception.Message}",
                "Error de logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, $"El archivo no contiene una imagen compatible: {exception.Message}",
                "Error de logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (OutOfMemoryException)
        {
            MessageBox.Show(this, "No se pudo decodificar la imagen. Prueba con PNG, JPEG, BMP, GIF o TIFF.",
                "Imagen no compatible", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Error de logotipo",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (DbUpdateException exception)
        {
            MessageBox.Show(this, $"No se pudo guardar el logotipo: {exception.GetBaseException().Message}",
                "Error de logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SqliteException exception)
        {
            MessageBox.Show(this, $"Error en la base local: {exception.Message}",
                "Error de logotipo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(this, $"Windows no pudo procesar la imagen: {exception.Message}",
                "Imagen no compatible", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
            _changeLogoButton.Text = "Cambiar logotipo…";
            _changeLogoButton.Enabled = true;
        }
    }

    private void SetLogo(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var source = Image.FromStream(stream);
        var previous = _brandLogo.Image;
        _brandLogo.Image = new Bitmap(source);
        previous?.Dispose();
    }

    private void AddModuleButton(
        TableLayoutPanel layout, string title, string description, int column, int row, string pageKey)
    {
        var button = new Button
        {
            Text = $"{title}\n{description}",
            Dock = DockStyle.Fill,
            Margin = new Padding(10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = ModernTheme.Ink,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(24),
            Cursor = Cursors.Hand
        };
        ModernTheme.StyleButton(button, Surface, ModernTheme.Ink, Accent);
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) => _navigation.Navigate(pageKey);
        layout.Controls.Add(button, column, row);
    }
}

internal sealed class NavigationShellForm : Form
{
    private static readonly Color Background = ModernTheme.Canvas;
    private static readonly Color Accent = ModernTheme.Orange;
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = Background };
    private readonly Button _backButton = new()
    {
        Text = "‹  VOLVER",
        Width = 132,
        Height = 44,
        Margin = new Padding(2, 2, 6, 2)
    };
    private readonly Label _currentModule = new()
    {
        Text = "TIENDA MIRIAM",
        AutoSize = false,
        Width = 172,
        ForeColor = ModernTheme.OrangeDark,
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(10, 8, 14, 0)
    };
    private readonly NavigationManager _navigation;
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly LocalUserIdentity _user;
    private readonly System.Windows.Forms.Timer _cashClosureSyncTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _idleDetectionTimer = new() { Interval = 1_000 };
    private readonly IdleActivityMessageFilter _idleInputFilter;
    private IdleScreenControl? _idleScreen;
    private IdleScreenSettings _idleSettings = new();
    private DateTime _lastInputAt = DateTime.UtcNow;
    private bool _idleActive;
    private bool _wakeInProgress;
    private bool _syncingCashClosures;
    private bool _syncingAudit;
    private DateTime _lastAuditRetentionDate = DateTime.MinValue;
    private DateTime _lastAuditRetentionSyncAttemptUtc = DateTime.MinValue;

    public NavigationShellForm(
        DbContextOptions<TiendaDbContext> options,
        LocalUserIdentity user,
        bool startInSales = false)
    {
        WindowFadeAnimator.Attach(this);
        _options = options;
        _user = user;
        AuditExecutionContext.Set(user.Username,
            Environment.GetEnvironmentVariable("TIENDA_TERMINAL_ID") ?? Environment.MachineName);
        _idleInputFilter = new IdleActivityMessageFilter(HandleInputActivity);
        Text = "Tienda Miriam";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(900, 600);
        BackColor = Background;
        ForeColor = ModernTheme.Ink;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Background,
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var navigationBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ModernTheme.Surface,
            Padding = new Padding(6, 5, 6, 4)
        };
        StyleNavigationButton(_backButton);
        navigationBar.Controls.Add(_backButton);
        navigationBar.Controls.Add(_currentModule);

        var homeButton = AddNavigationButton(navigationBar, "INICIO");
        var salesButton = AddNavigationButton(navigationBar, "VENTAS");
        var inventoryButton = AddNavigationButton(navigationBar, "INVENTARIO");
        var cashButton = AddNavigationButton(navigationBar, "HISTORIAL");
        var supplierButton = AddNavigationButton(navigationBar, "PROVEEDORES");
        var customerButton = AddNavigationButton(navigationBar, "CLIENTES / FIADO");
        var promotionBundlesButton = AddNavigationButton(navigationBar, "PROMOCIONES");
        var reportsButton = AddNavigationButton(navigationBar, "REPORTES");
        var auditButton = AddNavigationButton(navigationBar, "AUDITORÍA");
        var cashSessionButton = AddNavigationButton(navigationBar, "ARQUEO / CIERRE");
        var usersButton = AddNavigationButton(navigationBar, "USUARIOS");
        cashSessionButton.Click += (_, _) =>
        {
            using var dialog = new CashSessionDialog(_options, Environment.GetEnvironmentVariable("TIENDA_TERMINAL_ID")
                ?? Environment.MachineName, _user);
            dialog.ShowDialog(this);
        };
        usersButton.Click += (_, _) =>
        {
            if (_user.Role != AppRole.Administrator) return;
            using var dialog = new LocalUserManagementDialog(_options);
            dialog.ShowDialog(this);
        };
        usersButton.Visible = _user.Role == AppRole.Administrator;

        root.Controls.Add(navigationBar, 0, 0);
        root.Controls.Add(_pageHost, 0, 1);
        Controls.Add(root);

        _navigation = new NavigationManager(_pageHost, _currentModule, _backButton);
        _navigation.Register("home", "Menú principal",
            () => new LauncherForm(options, _navigation, LoadIdleSettingsAsync));
        _navigation.Register("sales", "Ventas", () => new MainForm(options, _user));
        _navigation.Register("inventory", "Inventario", () => new InventoryWindow(options, _user));
        _navigation.Register("cash", "Historial de Ventas", () => new CashWindow(options));
        _navigation.Register("suppliers", "Proveedores", () => new SupplierWindow(options, _user.Username));
        _navigation.Register("customers", "Clientes y cuentas por cobrar",
            () => new CustomerModuleWindow(options, _user));
        _navigation.Register("reports", "Reportes", () => new ReportWindow(options));
        _navigation.Register("promotion-bundles", "Promociones y combos",
            () => new PromotionBundleWindow(options, _user.Username));
        _navigation.Register("cash-closures", "Cierres de caja · nube", () => new CashRegisterHistoryWindow(options));
        _navigation.Register("audit", "Auditoría de Cambios", () => new AuditWindow(options, _user));

        _backButton.Click += (_, _) => _navigation.GoBack();
        homeButton.Click += (_, _) => _navigation.Navigate("home");
        salesButton.Click += (_, _) => _navigation.Navigate("sales");
        inventoryButton.Click += (_, _) => _navigation.Navigate("inventory");
        cashButton.Click += (_, _) => _navigation.Navigate("cash");
        supplierButton.Click += (_, _) => _navigation.Navigate("suppliers");
        customerButton.Click += (_, _) => _navigation.Navigate("customers");
        promotionBundlesButton.Click += (_, _) => _navigation.Navigate("promotion-bundles");
        reportsButton.Click += (_, _) => _navigation.Navigate("reports");
        auditButton.Click += (_, _) =>
        {
            if (_user.Role == AppRole.Administrator)
            {
                _navigation.Navigate("audit");
            }
        };
        var isAdministrator = _user.Role == AppRole.Administrator;
        var canOperateRegister = _user.Role is AppRole.Administrator or AppRole.Cashier;
        homeButton.Visible = isAdministrator;
        salesButton.Visible = canOperateRegister;
        cashSessionButton.Visible = canOperateRegister;
        inventoryButton.Visible = isAdministrator;
        cashButton.Visible = isAdministrator;
        supplierButton.Visible = isAdministrator;
        customerButton.Visible = canOperateRegister;
        promotionBundlesButton.Visible = isAdministrator;
        reportsButton.Visible = isAdministrator;
        auditButton.Visible = isAdministrator;

        _cashClosureSyncTimer.Tick += async (_, _) =>
        {
            await SyncPendingCashClosuresAsync();
            await SyncPendingPromotionBundlesAsync();
            await SyncPendingAuditAsync();
        };
        _idleDetectionTimer.Tick += async (_, _) => await CheckIdleStateAsync();
        Application.AddMessageFilter(_idleInputFilter);
        FormClosed += (_, _) =>
        {
            _cashClosureSyncTimer.Stop();
            _cashClosureSyncTimer.Dispose();
            _idleDetectionTimer.Stop();
            _idleDetectionTimer.Dispose();
            Application.RemoveMessageFilter(_idleInputFilter);
        };
        var initialModule = isAdministrator && !startInSales ? "home" : "sales";
        Shown += async (_, _) =>
        {
            _navigation.NavigateInitial(initialModule);
            await LoadIdleSettingsAsync();
            await SyncPendingCashClosuresAsync();
            await SyncPendingPromotionBundlesAsync();
            await SyncPendingAuditAsync();
            _cashClosureSyncTimer.Start();
            _idleDetectionTimer.Start();
        };
    }

    private bool HandleInputActivity()
    {
        _lastInputAt = DateTime.UtcNow;
        if (!_idleActive)
        {
            return false;
        }
        if (_wakeInProgress)
        {
            return false;
        }
        _wakeInProgress = true;
        _ = WakeFromIdleAsync();
        return true;
    }

    private async Task LoadIdleSettingsAsync()
    {
        try
        {
            _idleSettings = await IdleScreenSettingsStore.LoadAsync(_options);
            VisualAnimationPreferences.Enabled = _idleSettings.AnimationsEnabled;
            if (_idleScreen is null)
            {
                _idleScreen = new IdleScreenControl(_idleSettings);
                Controls.Add(_idleScreen);
            }
            else
            {
                _idleScreen.ApplySettings(_idleSettings);
            }
            await _idleScreen.LoadLogoAsync(_options);
            _idleScreen.Visible = _idleActive;
            _idleScreen.BringToFront();
            _lastInputAt = DateTime.UtcNow;
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(this, $"No se pudo cargar la configuración de pantalla: {exception.Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (DbUpdateException exception)
        {
            MessageBox.Show(this, $"No se pudo leer la configuración visual: {exception.GetBaseException().Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SqliteException exception)
        {
            MessageBox.Show(this, $"Error SQLite al leer la configuración visual: {exception.Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, $"El logotipo guardado no se puede mostrar: {exception.Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OutOfMemoryException)
        {
            MessageBox.Show(this, "El logotipo guardado no se puede decodificar.",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task CheckIdleStateAsync()
    {
        if (_wakeInProgress || _idleActive || !Visible || !Enabled)
        {
            if (_idleActive)
            {
                _idleScreen?.UpdateClock(_idleSettings.Use24HourClock, DateTime.Now);
            }
            return;
        }
        if (DateTime.UtcNow - _lastInputAt < TimeSpan.FromMinutes(_idleSettings.IdleMinutes))
        {
            return;
        }
        try
        {
            if (_idleScreen is null)
            {
                _idleScreen = new IdleScreenControl(_idleSettings);
                Controls.Add(_idleScreen);
                await _idleScreen.LoadLogoAsync(_options);
            }
            _idleActive = true;
            _idleScreen.UpdateClock(_idleSettings.Use24HourClock, DateTime.Now);
            _idleScreen.Visible = true;
            _idleScreen.BringToFront();
            _idleScreen.Focus();
        }
        catch (DbUpdateException exception)
        {
            _lastInputAt = DateTime.UtcNow;
            MessageBox.Show(this, $"No se pudo mostrar el logotipo: {exception.GetBaseException().Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SqliteException exception)
        {
            _lastInputAt = DateTime.UtcNow;
            MessageBox.Show(this, $"Error SQLite al mostrar el logotipo: {exception.Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (ArgumentException exception)
        {
            _lastInputAt = DateTime.UtcNow;
            MessageBox.Show(this, $"El logotipo guardado no se puede mostrar: {exception.Message}",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OutOfMemoryException)
        {
            _lastInputAt = DateTime.UtcNow;
            MessageBox.Show(this, "El logotipo guardado no se puede decodificar.",
                "Pantalla de espera", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task WakeFromIdleAsync()
    {
        try
        {
            var canContinue = true;
            if (_idleSettings.RequirePasswordOnWake)
            {
                using var dialog = new WakeAuthenticationDialog(_options, _user);
                canContinue = dialog.ShowDialog(this) == DialogResult.OK;
            }
            if (canContinue)
            {
                _idleActive = false;
                if (_idleScreen is not null)
                {
                    _idleScreen.Visible = false;
                }
            }
        }
        finally
        {
            _lastInputAt = DateTime.UtcNow;
            _wakeInProgress = false;
        }
        await Task.CompletedTask;
    }

    private async Task SyncPendingCashClosuresAsync()
    {
        if (_syncingCashClosures)
        {
            return;
        }
        _syncingCashClosures = true;
        try
        {
            await using var db = new TiendaDbContext(_options);
            var pending = await db.CashRegisterSessions.AnyAsync(
                session => session.SyncPending && session.ClosedAtUtc != null);
            if (!pending)
            {
                Text = "Tienda Miriam";
                return;
            }

            using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync(administrator: true);
            await CloudCashRegisterSync.SyncPendingAsync(client, db);
            pending = await db.CashRegisterSessions.AnyAsync(
                session => session.SyncPending && session.ClosedAtUtc != null);
            Text = pending
                ? "Tienda Miriam · cierres de caja pendientes de sincronizar"
                : "Tienda Miriam";
        }
        catch (HttpRequestException)
        {
            Text = "Tienda Miriam · cierres de caja pendientes de sincronizar";
        }
        catch (TaskCanceledException)
        {
            Text = "Tienda Miriam · sincronización de cierres agotó el tiempo de espera";
        }
        catch (InvalidOperationException)
        {
            Text = "Tienda Miriam · revisa la configuración cloud para sincronizar cierres";
        }
        catch (System.Text.Json.JsonException)
        {
            Text = "Tienda Miriam · la API devolvió una respuesta inválida al sincronizar cierres";
        }
        catch (DbUpdateException)
        {
            Text = "Tienda Miriam · no se pudo actualizar la cola local de cierres";
        }
        catch (SqliteException)
        {
            Text = "Tienda Miriam · error local al sincronizar cierres";
        }
        finally
        {
            _syncingCashClosures = false;
        }
    }

    private async Task SyncPendingAuditAsync()
    {
        if (_syncingAudit)
        {
            return;
        }
        _syncingAudit = true;
        try
        {
            await using var db = new TiendaDbContext(_options);
            if (_lastAuditRetentionDate.Date != DateTime.UtcNow.Date)
            {
                await AuditRetentionService.PruneExpiredAsync(db);
                _lastAuditRetentionDate = DateTime.UtcNow.Date;
            }
            if (_user.Role == AppRole.Administrator
                && DateTime.UtcNow - _lastAuditRetentionSyncAttemptUtc >= TimeSpan.FromMinutes(5))
            {
                _lastAuditRetentionSyncAttemptUtc = DateTime.UtcNow;
                var retentionDays = await AuditRetentionService.GetRetentionDaysAsync(db);
                using var retentionClient = await CloudApiClientFactory.CreateAuthorizedClientAsync(administrator: true);
                using var retentionResponse = await retentionClient.PutAsJsonAsync(
                    "/api/admin/audit/retention", new UpdateAuditRetentionRequest(retentionDays));
                retentionResponse.EnsureSuccessStatusCode();
            }
            if (!await db.AuditOutbox.AnyAsync(item => !item.IsDelivered))
            {
                return;
            }
            using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync();
            await AuditOutboxSync.SyncPendingAsync(client, db);
        }
        catch (HttpRequestException)
        {
            Text = "Tienda Miriam · auditoría o retención pendiente de sincronizar";
        }
        catch (TaskCanceledException)
        {
            Text = "Tienda Miriam · sincronización de auditoría agotó el tiempo de espera";
        }
        catch (InvalidOperationException exception)
        {
            Text = $"Tienda Miriam · configuración de sincronización de auditoría inválida: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            Text = $"Tienda Miriam · error de auditoría pendiente: {exception.Message}";
        }
        catch (DbUpdateException exception)
        {
            Text = $"Tienda Miriam · error guardando sincronización de auditoría: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Text = $"Tienda Miriam · error SQLite sincronizando auditoría: {exception.Message}";
        }
        finally
        {
            _syncingAudit = false;
        }
    }

    private async Task SyncPendingPromotionBundlesAsync()
    {
        if (_user.Role != AppRole.Administrator)
        {
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(_options);
            if (!await db.PromotionBundleOutbox.AnyAsync(item => !item.IsDelivered))
            {
                return;
            }
            using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync(administrator: true);
            await PromotionBundleOutboxSync.SyncPendingAsync(client, db);
        }
        catch (HttpRequestException)
        {
            Text = "Tienda Miriam · combos pendientes de sincronizar";
        }
        catch (TaskCanceledException)
        {
            Text = "Tienda Miriam · sincronización de combos agotó el tiempo de espera";
        }
        catch (InvalidOperationException)
        {
            Text = "Tienda Miriam · revisa la configuración cloud para sincronizar combos";
        }
        catch (System.Text.Json.JsonException)
        {
            Text = "Tienda Miriam · outbox de combos inválido";
        }
        catch (DbUpdateException)
        {
            Text = "Tienda Miriam · no se pudo actualizar la cola local de combos";
        }
        catch (SqliteException)
        {
            Text = "Tienda Miriam · error local al sincronizar combos";
        }
    }

    private static Button AddNavigationButton(FlowLayoutPanel bar, string text)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 42, Margin = new Padding(2, 3, 2, 3) };
        StyleNavigationButton(button);
        bar.Controls.Add(button);
        return button;
    }

    private static void StyleNavigationButton(Button button)
    {
        ModernTheme.StyleButton(button, false);
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
    }
}

internal sealed class NavigationManager
{
    private readonly Panel _host;
    private readonly Label _currentModule;
    private readonly Button _backButton;
    private readonly TransitionVeil _transitionVeil = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, (string Title, Func<Form> Create, Form? Page)> _pages =
        new(StringComparer.Ordinal);
    private readonly Stack<string> _history = new();
    private string? _currentKey;

    public NavigationManager(Panel host, Label currentModule, Button backButton)
    {
        _host = host;
        _currentModule = currentModule;
        _backButton = backButton;
        _host.Controls.Add(_transitionVeil);
    }

    public void Register(string key, string title, Func<Form> create)
    {
        if (string.IsNullOrWhiteSpace(key) || _pages.ContainsKey(key))
        {
            throw new InvalidOperationException($"La ruta de navegación '{key}' es inválida o ya existe.");
        }
        _pages.Add(key, (title, create, null));
    }

    public void NavigateInitial(string key)
    {
        if (_currentKey is not null)
        {
            throw new InvalidOperationException("La navegación inicial ya fue configurada.");
        }
        ShowPage(key);
    }

    public void Navigate(string key)
    {
        if (key == _currentKey)
        {
            return;
        }
        if (_currentKey is not null)
        {
            _history.Push(_currentKey);
        }
        ShowPage(key);
    }

    public void GoBack()
    {
        if (_history.TryPop(out var previous))
        {
            ShowPage(previous);
        }
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var route))
        {
            throw new InvalidOperationException($"No existe la ruta de navegación '{key}'.");
        }

        if (_currentKey is not null && _pages[_currentKey].Page is { } current)
        {
            current.Hide();
        }

        var page = route.Page;
        if (page is null)
        {
            page = route.Create();
            page.TopLevel = false;
            page.FormBorderStyle = FormBorderStyle.None;
            page.WindowState = FormWindowState.Normal;
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _host.Controls.Add(page);
            page.BringToFront();
            _pages[key] = (route.Title, route.Create, page);
        }

        _currentKey = key;
        _currentModule.Text = route.Title.ToUpperInvariant();
        _backButton.Enabled = _history.Count > 0;
        page.Show();
        page.BringToFront();
        _transitionVeil.Play();
    }
}

internal abstract class DataModuleWindow : Form
{
    private readonly PrintDocument _printDocument = new();
    private int _printRowIndex;
    protected readonly DbContextOptions<TiendaDbContext> Options;
    protected readonly FlowLayoutPanel Header = new() { Dock = DockStyle.Fill, WrapContents = true };
    protected readonly DataGridView Grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = ModernTheme.Surface,
        ForeColor = ModernTheme.Ink,
        GridColor = ModernTheme.Border,
        BorderStyle = BorderStyle.None,
        RowHeadersVisible = false
    };
    protected readonly Label Status = new()
    {
        AutoSize = false,
        Width = 330,
        Height = 44,
        AutoEllipsis = true,
        ForeColor = ModernTheme.OrangeDark,
        Padding = new Padding(8)
    };

    protected DataModuleWindow(DbContextOptions<TiendaDbContext> options, string title)
    {
        Options = options;
        Text = $"Tienda Miriam | {title}";
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Normal;
        MinimumSize = new Size(820, 580);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var refresh = new Button { Text = "Actualizar", AutoSize = true, Height = 36 };
        StyleButton(refresh);
        refresh.Click += async (_, _) => await RefreshDataSafelyAsync();
        var print = new Button { Text = "Imprimir", AutoSize = true, Height = 36 };
        StyleButton(print);
        print.Click += (_, _) => PrintReport();
        Header.Controls.Add(refresh);
        Header.Controls.Add(print);
        Header.Controls.Add(Status);
        root.Controls.Add(Header, 0, 0);
        root.Controls.Add(Grid, 0, 1);
        Controls.Add(root);
        Grid.EnableHeadersVisualStyles = false;
        Grid.ColumnHeadersDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        Grid.ColumnHeadersDefaultCellStyle.ForeColor = ModernTheme.Purple;
        Grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = ModernTheme.SurfaceMuted;
        Grid.DefaultCellStyle.BackColor = ModernTheme.Surface;
        Grid.DefaultCellStyle.ForeColor = ModernTheme.Ink;
        Grid.DefaultCellStyle.SelectionBackColor = ModernTheme.Blue;
        Grid.DefaultCellStyle.SelectionForeColor = Color.White;
        Grid.AlternatingRowsDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        _printDocument.DocumentName = Text;
        _printDocument.PrintPage += PrintDocumentPage;
        Shown += async (_, _) => await RefreshDataSafelyAsync();
        FormClosed += (_, _) => _printDocument.Dispose();
    }

    protected void AddColumns(params string[] names)
    {
        Grid.Columns.Clear();
        foreach (var name in names)
        {
            Grid.Columns.Add(name, name);
        }
    }

    protected void ReplaceGridHost(Control host)
    {
        if (Grid.Parent is not Control parent)
        {
            throw new InvalidOperationException("No se encontró el contenedor de la tabla del módulo.");
        }
        var position = parent is TableLayoutPanel table
            ? table.GetPositionFromControl(Grid)
            : new TableLayoutPanelCellPosition(-1, -1);
        var index = parent.Controls.GetChildIndex(Grid);
        parent.Controls.Remove(Grid);
        host.Dock = DockStyle.Fill;
        if (parent is TableLayoutPanel tableParent && position.Column >= 0 && position.Row >= 0)
        {
            tableParent.Controls.Add(host, position.Column, position.Row);
        }
        else
        {
            parent.Controls.Add(host);
        }
        parent.Controls.SetChildIndex(host, index);
    }

    protected abstract Task LoadDataAsync();

    protected Task RefreshModuleAsync() => RefreshDataSafelyAsync();

    private async Task RefreshDataSafelyAsync()
    {
        try
        {
            Status.Text = string.Empty;
            await LoadDataAsync();
            var updated = $"Actualizado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}";
            Status.Text = string.IsNullOrWhiteSpace(Status.Text) ? updated : $"{Status.Text} · {updated}";
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"Error de base de datos: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
    }

    private void PrintReport()
    {
        using var dialog = new PrintDialog
        {
            Document = _printDocument,
            UseEXDialog = true,
            AllowSomePages = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _printRowIndex = 0;
            _printDocument.Print();
            Status.Text = "Reporte enviado a la impresora seleccionada.";
        }
        catch (InvalidPrinterException exception)
        {
            Status.Text = $"Impresora no disponible: {exception.Message}";
        }
        catch (Win32Exception exception)
        {
            Status.Text = $"No se pudo imprimir el reporte: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = $"No se pudo iniciar la impresión: {exception.Message}";
        }
    }

    private void PrintDocumentPage(object? sender, PrintPageEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics
            ?? throw new InvalidOperationException("El controlador de impresión no proporcionó una superficie gráfica.");
        var bounds = eventArgs.MarginBounds;
        using var titleFont = new Font("Segoe UI", 14, FontStyle.Bold);
        using var headerFont = new Font("Segoe UI", 8, FontStyle.Bold);
        using var cellFont = new Font("Segoe UI", 8);
        using var borderPen = new Pen(Color.DimGray);
        using var headerBrush = new SolidBrush(ModernTheme.Orange);
        using var headerTextBrush = new SolidBrush(Color.White);
        using var textBrush = new SolidBrush(Color.Black);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center
        };

        var y = (float)bounds.Top;
        graphics.DrawString(Text, titleFont, textBrush, bounds.Left, y);
        y += titleFont.GetHeight(graphics) + 8;
        graphics.DrawString(DateTime.Now.ToString("dd/MM/yyyy HH:mm"), cellFont, textBrush, bounds.Left, y);
        y += cellFont.GetHeight(graphics) + 10;

        var visibleColumns = Grid.Columns.Cast<DataGridViewColumn>()
            .Where(column => column.Visible)
            .ToArray();
        var totalWeight = visibleColumns.Sum(column => Math.Max(1, column.FillWeight));
        var widths = visibleColumns
            .Select(column => bounds.Width * Math.Max(1, column.FillWeight) / totalWeight)
            .ToArray();
        const float rowHeight = 24;
        var x = (float)bounds.Left;
        for (var columnIndex = 0; columnIndex < visibleColumns.Length; columnIndex++)
        {
            var cell = new RectangleF(x, y, widths[columnIndex], rowHeight);
            graphics.FillRectangle(headerBrush, cell);
            graphics.DrawRectangle(borderPen, cell.X, cell.Y, cell.Width, cell.Height);
            graphics.DrawString(visibleColumns[columnIndex].HeaderText, headerFont, headerTextBrush, cell, format);
            x += widths[columnIndex];
        }
        y += rowHeight;

        while (_printRowIndex < Grid.Rows.Count && y + rowHeight <= bounds.Bottom)
        {
            if (Grid.Rows[_printRowIndex].IsNewRow)
            {
                _printRowIndex++;
                continue;
            }
            x = bounds.Left;
            for (var columnIndex = 0; columnIndex < visibleColumns.Length; columnIndex++)
            {
                var column = visibleColumns[columnIndex];
                var cell = new RectangleF(x, y, widths[columnIndex], rowHeight);
                graphics.DrawRectangle(borderPen, cell.X, cell.Y, cell.Width, cell.Height);
                graphics.DrawString(
                    Grid.Rows[_printRowIndex].Cells[column.Index].FormattedValue?.ToString() ?? string.Empty,
                    cellFont,
                    textBrush,
                    cell,
                    format);
                x += widths[columnIndex];
            }
            y += rowHeight;
            _printRowIndex++;
        }

        eventArgs.HasMorePages = _printRowIndex < Grid.Rows.Count;
        if (!eventArgs.HasMorePages)
        {
            _printRowIndex = 0;
        }
    }

    internal static void StyleButton(Button button)
    {
        ModernTheme.StyleButton(button, true);
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    }
}

internal sealed class ProductionBadgeTextBoxCell : DataGridViewTextBoxCell
{
    protected override void Paint(
        Graphics graphics,
        Rectangle clipBounds,
        Rectangle cellBounds,
        int rowIndex,
        DataGridViewElementStates cellState,
        object? value,
        object? formattedValue,
        string? errorText,
        DataGridViewCellStyle cellStyle,
        DataGridViewAdvancedBorderStyle advancedBorderStyle,
        DataGridViewPaintParts paintParts)
    {
        if (Tag is not true)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
                errorText, cellStyle, advancedBorderStyle, paintParts);
            return;
        }

        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue,
            errorText, cellStyle, advancedBorderStyle,
            paintParts & ~DataGridViewPaintParts.ContentForeground);
        var font = cellStyle.Font ?? DataGridView?.Font ?? SystemFonts.DefaultFont;
        const string badgeText = "Producido por lote";
        using var badgeFont = new Font("Segoe UI", 8, FontStyle.Bold);
        var badgeWidth = (int)Math.Ceiling(graphics.MeasureString(badgeText, badgeFont).Width) + 14;
        var badgeBounds = new Rectangle(
            Math.Max(cellBounds.Left + 6, cellBounds.Right - badgeWidth - 8),
            cellBounds.Top + 5,
            Math.Min(badgeWidth, Math.Max(1, cellBounds.Width - 12)),
            Math.Max(1, cellBounds.Height - 10));
        using var badgeBrush = new SolidBrush(ModernTheme.OrangeDark);
        using var badgeTextBrush = new SolidBrush(Color.White);
        using var badgeFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.FillRectangle(badgeBrush, badgeBounds);
        graphics.DrawString(badgeText, badgeFont, badgeTextBrush, badgeBounds, badgeFormat);

        var textBounds = Rectangle.FromLTRB(
            cellBounds.Left + 8,
            cellBounds.Top + 2,
            Math.Max(cellBounds.Left + 8, badgeBounds.Left - 8),
            cellBounds.Bottom - 2);
        var foreground = cellState.HasFlag(DataGridViewElementStates.Selected)
            ? cellStyle.SelectionForeColor
            : cellStyle.ForeColor;
        using var textBrush = new SolidBrush(foreground);
        using var textFormat = new StringFormat
        {
            Alignment = cellStyle.Alignment is DataGridViewContentAlignment.MiddleRight
                or DataGridViewContentAlignment.TopRight or DataGridViewContentAlignment.BottomRight
                    ? StringAlignment.Far
                    : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(Convert.ToString(formattedValue) ?? string.Empty,
            font, textBrush, textBounds, textFormat);
    }
}

internal sealed class InventoryWindow : DataModuleWindow
{
    private sealed record InventoryRowTag(
        Guid Id, bool IsProduct, Guid? LinkedStockId = null, bool IsRecipe = false);

    private enum InventorySection
    {
        RawIngredients,
        PreparedRecipes,
        FinishedGoods,
        RetailGoods,
        Services
    }

    private readonly TabControl _sections = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<InventorySection, TabPage> _sectionPages = [];
    private readonly Dictionary<TabPage, TransitionVeil> _sectionVeils = [];
    private readonly LocalUserIdentity _user;
    private bool _loadingGrid;
    private readonly PictureBox _productPreview = new()
    {
        Width = 104,
        Height = 42,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = ModernTheme.SurfaceMuted,
        Margin = new Padding(6, 0, 6, 0)
    };
    private readonly Button _changeImage = new()
        { Text = "Añadir / cambiar imagen", AutoSize = true, Height = 36, Enabled = false };
    private readonly Button _removeImage = new()
        { Text = "Quitar imagen", AutoSize = true, Height = 36, Enabled = false };
    private readonly Button _editStock = new() { Text = "Editar insumo", AutoSize = true, Height = 36, Enabled = false };
    private readonly Button _editProduct = new() { Text = "Editar producto", AutoSize = true, Height = 36, Enabled = false };
    private readonly Button _editRecipe = new() { Text = "Editar receta", AutoSize = true, Height = 36, Enabled = false };
    private readonly Button _changeDepartment = new()
        { Text = "Cambiar categoría", AutoSize = true, Height = 36, Enabled = false };

    public InventoryWindow(DbContextOptions<TiendaDbContext> options, LocalUserIdentity user)
        : base(options, "Inventario y márgenes")
    {
        _user = user;
        _sectionPages.Add(InventorySection.RawIngredients,
            new TabPage("Insumos Crudos") { BackColor = ModernTheme.Canvas });
        _sectionPages.Add(InventorySection.PreparedRecipes,
            new TabPage("Preparados del Día") { BackColor = ModernTheme.Canvas });
        _sectionPages.Add(InventorySection.FinishedGoods,
            new TabPage("Congelados / Por Lote") { BackColor = ModernTheme.Canvas });
        _sectionPages.Add(InventorySection.RetailGoods,
            new TabPage("Artículos de Tienda") { BackColor = ModernTheme.Canvas });
        _sectionPages.Add(InventorySection.Services,
            new TabPage("Servicios") { BackColor = ModernTheme.Canvas });
        foreach (var page in _sectionPages.Values)
        {
            var veil = new TransitionVeil { Dock = DockStyle.Fill };
            _sectionVeils.Add(page, veil);
            page.Controls.Add(veil);
            _sections.TabPages.Add(page);
        }
        ReplaceGridHost(_sections);
        ShowSelectedSection();

        StyleButton(_changeImage);
        StyleButton(_removeImage);
        _changeImage.Click += async (_, _) => await SelectSelectedImageAsync();
        _removeImage.Click += async (_, _) => await RemoveSelectedImageAsync();
        var newStock = new Button { Text = "Nuevo insumo", AutoSize = true, Height = 36 };
        StyleButton(newStock);
        newStock.Click += async (_, _) => await CreateStockItemAsync();
        var newProduct = new Button { Text = "Nuevo producto", AutoSize = true, Height = 36 };
        StyleButton(newProduct);
        newProduct.Click += async (_, _) => await CreateProductAsync();
        var production = new Button { Text = "Registrar producción", AutoSize = true, Height = 36 };
        StyleButton(production);
        production.Click += async (_, _) => await RegisterProductionAsync();
        StyleButton(_editRecipe);
        StyleButton(_editStock);
        StyleButton(_editProduct);
        StyleButton(_changeDepartment);
        _editRecipe.Click += async (_, _) => await EditSelectedRecipeAsync();
        _editStock.Click += async (_, _) => await EditSelectedStockItemAsync();
        _editProduct.Click += async (_, _) => await EditSelectedProductAsync();
        _changeDepartment.Click += async (_, _) => await ChangeSelectedProductDepartmentAsync();
        Grid.CellContentClick += async (_, eventArgs) =>
        {
            if (_sections.SelectedTab == _sectionPages[InventorySection.PreparedRecipes]
                && eventArgs.RowIndex >= 0
                && Grid.Columns[eventArgs.ColumnIndex].Name == "RecipeAction")
            {
                await ShowSelectedRecipeAsync(eventArgs.RowIndex);
            }
        };
        Header.Controls.Add(newStock);
        Header.Controls.Add(newProduct);
        Header.Controls.Add(production);
        Header.Controls.Add(_editStock);
        Header.Controls.Add(_editProduct);
        Header.Controls.Add(_editRecipe);
        Header.Controls.Add(_changeDepartment);
        Header.Controls.Add(_changeImage);
        Header.Controls.Add(_removeImage);
        Header.Controls.Add(_productPreview);
        Grid.SelectionChanged += async (_, _) =>
        {
            if (_loadingGrid)
            {
                return;
            }
            var tag = Grid.CurrentRow?.Tag as InventoryRowTag;
            _editStock.Enabled = tag is { IsProduct: false } || tag is { LinkedStockId: not null };
            _editProduct.Enabled = tag is { IsProduct: true };
            _editStock.Text = tag is { IsProduct: true } ? "Editar existencia" : "Editar insumo";
            _editRecipe.Enabled = tag is { IsRecipe: true };
            _changeDepartment.Enabled = tag is { IsProduct: true };
            _changeImage.Enabled = tag is not null;
            _removeImage.Enabled = false;
            await LoadSelectedPhotoAsync();
        };
        _sections.SelectedIndexChanged += async (_, _) =>
        {
            ShowSelectedSection();
            if (_sections.SelectedTab is { } page)
            {
                _sectionVeils[page].Play();
            }
            Grid.ClearSelection();
            _editStock.Enabled = false;
            _editProduct.Enabled = false;
            _editRecipe.Enabled = false;
            _changeDepartment.Enabled = false;
            _changeImage.Enabled = false;
            _removeImage.Enabled = false;
            SetPreview(null);
            await RefreshModuleAsync();
        };
    }

    private async Task RegisterProductionAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(Options);
            var stocks = await db.StockItems.AsNoTracking().OrderBy(item => item.Name).ToListAsync();
            var stocksById = stocks.ToDictionary(item => item.Id);
            var products = await db.Products.AsNoTracking()
                .Where(item => item.IsActive && !item.IsRecipe && item.StockItemId != null
                    && db.ProductDetails.Any(detail => detail.ProductId == item.Id
                        && detail.Department == ProductDepartment.FrozenBatch))
                .OrderBy(item => item.Name)
                .ToListAsync();
            var finishedProducts = products
                .Where(item => item.StockItemId is Guid stockId && stocksById.ContainsKey(stockId))
                .Select(item =>
                {
                    var finishedStock = stocksById[item.StockItemId!.Value];
                    return new ProductionProductOption(item.Id, finishedStock.Id, item.Name, finishedStock.Unit);
                })
                .ToList();
            if (finishedProducts.Count == 0)
            {
                MessageBox.Show(this,
                    "Primero crea el producto congelado y enlázalo a su propia existencia. No lo configures con receta directa.",
                    "No hay productos para producir", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ingredients = stocks.Select(item =>
                new StockItemOption(item.Id, $"{item.Name} · {item.Unit}", item.Unit)).ToList();
            using var dialog = new ProductionEditorDialog(
                finishedProducts,
                ingredients,
                async productId =>
                {
                    await using var capacityDb = new TiendaDbContext(Options);
                    return await new ProductionService(capacityDb)
                        .GetProductionCapacityAsync(productId, CancellationToken.None);
                });
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is not { } request)
            {
                return;
            }

            var result = await new ProductionService(db)
                .RecordProductionAsync(request, _user.Username, CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text =
                $"Lote registrado: {result.QuantityProduced:0.######} × {result.ProductName}. Costo unitario estimado: {PosFormatting.Money(result.UnitCost)}.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo registrar la producción: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al registrar la producción: {exception.Message}";
        }
    }

    protected override async Task LoadDataAsync()
    {
        if (_sections.SelectedTab is null)
        {
            return;
        }
        var section = GetSelectedSection();
        _loadingGrid = true;
        Grid.Rows.Clear();
        switch (section)
        {
            case InventorySection.RawIngredients:
                AddColumns("Nombre", "Unidad", "Stock Actual", "Stock Mínimo", "Costo");
                break;
            case InventorySection.PreparedRecipes:
                Grid.Columns.Clear();
                Grid.Columns.Add("Name", "Nombre");
                Grid.Columns.Add("Price", "Precio de Venta");
                Grid.Columns.Add(new DataGridViewButtonColumn
                {
                    Name = "RecipeAction",
                    HeaderText = "Receta",
                    Text = "Ver receta",
                    UseColumnTextForButtonValue = true,
                    FlatStyle = FlatStyle.Flat
                });
                break;
            case InventorySection.FinishedGoods:
                Grid.Columns.Clear();
                Grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "Name",
                    HeaderText = "Nombre",
                    CellTemplate = new ProductionBadgeTextBoxCell()
                });
                Grid.Columns.Add("AvailableUnits", "Unidades Disponibles");
                Grid.Columns.Add("LastProduction", "Fecha de Producción del Último Lote");
                break;
            case InventorySection.RetailGoods:
                AddColumns("Nombre", "Stock Actual", "Precio de Venta");
                break;
            case InventorySection.Services:
                AddColumns("Nombre", "Precio de Venta", "Servicio");
                break;
        }
        Grid.RowTemplate.Height = section == InventorySection.FinishedGoods ? 34 : 30;

        try
        {
            await using var db = new TiendaDbContext(Options);
            var stockItems = await db.StockItems.AsNoTracking().OrderBy(item => item.Name).ToListAsync();
            var stockById = stockItems.ToDictionary(item => item.Id);
            var products = await db.Products.AsNoTracking().Where(item => item.IsActive)
                .OrderBy(item => item.Name).ToListAsync();
            var recipesByProduct = (await db.RecipeComponents.AsNoTracking().ToListAsync())
                .GroupBy(component => component.ProductId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var photos = await db.ProductPhotos.AsNoTracking().ToDictionaryAsync(photo => photo.ProductId);
            var details = await db.ProductDetails.AsNoTracking().ToDictionaryAsync(item => item.ProductId);
            var productByStockId = products.Where(product => !product.IsRecipe && product.StockItemId is not null)
                .Select(product => product.StockItemId!.Value).ToHashSet();
            var recipeStockIds = recipesByProduct.Values.SelectMany(items => items)
                .Select(component => component.StockItemId).ToHashSet();
            var latestProductionByProduct = await db.StockProductionBatches.AsNoTracking()
                .GroupBy(batch => batch.ProductId)
                .Select(group => new
                {
                    ProductId = group.Key,
                    OccurredAtUtc = group.Max(batch => batch.OccurredAtUtc)
                })
                .ToDictionaryAsync(item => item.ProductId, item => item.OccurredAtUtc);

            if (section == InventorySection.RawIngredients)
            {
                foreach (var stock in stockItems.Where(item => !productByStockId.Contains(item.Id)))
                {
                    var type = recipeStockIds.Contains(stock.Id) ? "Insumo de receta" : "Insumo";
                    var stockRow = Grid.Rows.Add(stock.Name, stock.Unit,
                        stock.OnHand.ToString("0.######"), stock.ReorderAt.ToString("0.######"),
                        PosFormatting.Money(stock.UnitCost));
                    Grid.Rows[stockRow].Tag = new InventoryRowTag(stock.Id, false);
                    Grid.Rows[stockRow].Cells[0].ToolTipText = type;
                }
                foreach (var product in products.Where(item =>
                    details.TryGetValue(item.Id, out var detail)
                    && detail.Department == ProductDepartment.RawIngredient
                    && item.StockItemId is Guid linkedStockId && stockById.ContainsKey(linkedStockId)))
                {
                    var linkedStock = stockById[product.StockItemId!.Value];
                    var stockRow = Grid.Rows.Add(product.Name, linkedStock.Unit,
                        linkedStock.OnHand.ToString("0.######"), linkedStock.ReorderAt.ToString("0.######"),
                        PosFormatting.Money(linkedStock.UnitCost));
                    Grid.Rows[stockRow].Tag = new InventoryRowTag(product.Id, true, linkedStock.Id);
                }
            }
            else if (section == InventorySection.PreparedRecipes)
            {
                foreach (var product in products.Where(item => item.IsRecipe
                    && details.TryGetValue(item.Id, out var detail)
                    && detail.Department == ProductDepartment.PreparedDirect))
                {
                    var rowIndex = Grid.Rows.Add(product.Name, PosFormatting.Money(product.Price), "Ver receta");
                    Grid.Rows[rowIndex].Tag = new InventoryRowTag(product.Id, true, product.StockItemId, true);
                    Grid.Rows[rowIndex].Cells[0].ToolTipText =
                        "Producto preparado · su existencia teórica se calcula por receta.";
                    if (recipesByProduct.TryGetValue(product.Id, out var recipeLines))
                    {
                        Grid.Rows[rowIndex].Cells[0].ToolTipText += Environment.NewLine + string.Join(
                            Environment.NewLine,
                            recipeLines.Select(line =>
                            {
                                var ingredient = stockById.GetValueOrDefault(line.StockItemId);
                                return ingredient is null
                                    ? "Insumo no disponible"
                                    : $"{ingredient.Name}: {line.Quantity:0.######} {ingredient.Unit} por porción";
                            }));
                    }
                }
            }
            else if (section == InventorySection.FinishedGoods)
            {
                foreach (var product in products.Where(item =>
                    !item.IsRecipe && details.TryGetValue(item.Id, out var detail)
                    && detail.Department == ProductDepartment.FrozenBatch
                    && item.StockItemId is Guid linkedStockId && stockById.ContainsKey(linkedStockId)))
                {
                    var finishedStock = stockById[product.StockItemId!.Value];
                    var hasProduction = latestProductionByProduct.TryGetValue(product.Id, out var lastProducedAtUtc);
                    var productionDate = hasProduction
                        ? DateTime.SpecifyKind(lastProducedAtUtc, DateTimeKind.Utc).ToLocalTime()
                            .ToString("dd/MM/yyyy HH:mm")
                        : "Sin producción por lote";
                    var rowIndex = Grid.Rows.Add(
                        product.Name,
                        $"{finishedStock.OnHand:0.######} {finishedStock.Unit}",
                        productionDate);
                    Grid.Rows[rowIndex].Tag = new InventoryRowTag(product.Id, true, product.StockItemId);
                    Grid.Rows[rowIndex].Cells[0].Tag = hasProduction;
                    if (hasProduction)
                    {
                        Grid.Rows[rowIndex].Cells[0].ToolTipText = "Producido por lote";
                    }
                    if (details.TryGetValue(product.Id, out var productDetails))
                    {
                        Grid.Rows[rowIndex].Cells[0].ToolTipText +=
                            $"{Environment.NewLine}Categoría: {productDetails.Department}";
                    }
                    if (photos.ContainsKey(product.Id))
                    {
                        Grid.Rows[rowIndex].Cells[0].ToolTipText +=
                            $"{Environment.NewLine}Fotografía disponible; selecciónala para previsualizar.";
                    }
                }
            }
            else if (section == InventorySection.RetailGoods)
            {
                foreach (var product in products.Where(item =>
                    !item.IsRecipe && details.TryGetValue(item.Id, out var detail)
                    && detail.Department == ProductDepartment.Retail
                    && item.StockItemId is Guid linkedStockId && stockById.ContainsKey(linkedStockId)))
                {
                    var stock = stockById[product.StockItemId!.Value];
                    var rowIndex = Grid.Rows.Add(
                        product.Name,
                        $"{stock.OnHand:0.######} {stock.Unit}",
                        PosFormatting.Money(product.Price));
                    Grid.Rows[rowIndex].Tag = new InventoryRowTag(product.Id, true, product.StockItemId);
                }
            }
            else if (section == InventorySection.Services)
            {
                foreach (var product in products.Where(item =>
                    !item.IsRecipe
                    && details.TryGetValue(item.Id, out var detail)
                    && detail.Department == ProductDepartment.Service))
                {
                    var rowIndex = Grid.Rows.Add(product.Name, PosFormatting.Money(product.Price), "Sin inventario");
                    Grid.Rows[rowIndex].Tag = new InventoryRowTag(product.Id, true, product.StockItemId);
                }
            }
        }
        finally
        {
            _loadingGrid = false;
        }
        var selectedTag = Grid.CurrentRow?.Tag as InventoryRowTag;
        _editStock.Enabled = selectedTag is { IsProduct: false } || selectedTag is { LinkedStockId: not null };
        _editStock.Text = selectedTag is { IsProduct: true } ? "Editar existencia" : "Editar insumo";
        _editProduct.Enabled = selectedTag is { IsProduct: true };
        _editRecipe.Enabled = selectedTag is { IsRecipe: true };
        _changeDepartment.Enabled = selectedTag is { IsProduct: true };
        _changeImage.Enabled = selectedTag is not null;
        if (Grid.Rows.Count == 0)
        {
            Status.Text = section switch
            {
                InventorySection.RawIngredients => "No hay insumos crudos registrados.",
                InventorySection.PreparedRecipes => "No hay preparados del día con receta.",
                InventorySection.FinishedGoods => "No hay productos congelados enlazados a existencias.",
                InventorySection.RetailGoods => "No hay artículos de tienda enlazados a existencias.",
                InventorySection.Services => "No hay servicios registrados.",
                _ => string.Empty
            };
        }
        await LoadSelectedPhotoAsync();
    }

    private InventorySection GetSelectedSection() => _sections.SelectedTab == _sectionPages[InventorySection.PreparedRecipes]
        ? InventorySection.PreparedRecipes
        : _sections.SelectedTab == _sectionPages[InventorySection.FinishedGoods]
            ? InventorySection.FinishedGoods
            : _sections.SelectedTab == _sectionPages[InventorySection.RetailGoods]
                ? InventorySection.RetailGoods
                : _sections.SelectedTab == _sectionPages[InventorySection.Services]
                    ? InventorySection.Services
                    : InventorySection.RawIngredients;

    private void ShowSelectedSection()
    {
        var page = _sections.SelectedTab;
        if (page is null || ReferenceEquals(Grid.Parent, page))
        {
            return;
        }
        Grid.Parent?.Controls.Remove(Grid);
        page.Controls.Add(Grid);
        Grid.Dock = DockStyle.Fill;
    }

    private async Task ShowSelectedRecipeAsync(int rowIndex)
    {
        if (Grid.Rows[rowIndex].Tag is not InventoryRowTag { IsRecipe: true } selected)
        {
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var product = await db.Products.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == selected.Id);
            if (product is null)
            {
                Status.Text = "El producto preparado ya no existe.";
                return;
            }
            var components = await db.RecipeComponents.AsNoTracking()
                .Where(component => component.ProductId == product.Id)
                .Join(db.StockItems.AsNoTracking(),
                    component => component.StockItemId,
                    stock => stock.Id,
                    (component, stock) => $"{stock.Name}: {component.Quantity:0.######} {stock.Unit} por porción")
                .ToListAsync();
            var recipeText = components.Count == 0
                ? "Este producto todavía no tiene insumos en su receta."
                : string.Join(Environment.NewLine, components);
            MessageBox.Show(this, recipeText, $"Receta · {product.Name}",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo consultar la receta: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al consultar la receta: {exception.Message}";
        }
    }

    private async Task SelectSelectedImageAsync()
    {
        if (Grid.CurrentRow?.Tag is not InventoryRowTag selected)
        {
            MessageBox.Show(this, "Selecciona un producto, receta o insumo antes de cargar una imagen.",
                "Imagen de inventario", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new OpenFileDialog
        {
            Title = "Seleccionar imagen",
            Filter = "Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var bytes = await CatalogImageStore.ReadAndNormalizeAsync(dialog.FileName);
            await using var db = new TiendaDbContext(Options);
            if (selected.IsProduct)
            {
                var photo = await db.ProductPhotos.SingleOrDefaultAsync(item => item.ProductId == selected.Id);
                if (photo is null)
                {
                    db.ProductPhotos.Add(new ProductPhoto
                    {
                        ProductId = selected.Id,
                        ContentType = "image/png",
                        Data = bytes,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                }
                else
                {
                    photo.ContentType = "image/png";
                    photo.Data = bytes;
                    photo.UpdatedAtUtc = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
            }
            else
            {
                await CatalogImageStore.SaveAsync(db, "stock-item", selected.Id, bytes);
            }
            SetPreview(bytes);
            _removeImage.Enabled = true;
            Status.Text = "Imagen guardada en SQLite local y optimizada para el sistema.";
        }
        catch (IOException exception)
        {
            Status.Text = $"No se pudo leer la imagen: {exception.Message}";
        }
        catch (ArgumentException exception)
        {
            Status.Text = $"Windows no reconoce el contenido como imagen: {exception.Message}";
        }
        catch (OutOfMemoryException)
        {
            Status.Text = "No se pudo decodificar esta imagen. Prueba con otro formato de imagen.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar la imagen: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar la imagen: {exception.Message}";
        }
        catch (ExternalException exception)
        {
            Status.Text = $"El decodificador de Windows no pudo abrir la imagen: {exception.Message}";
        }
    }

    private async Task RemoveSelectedImageAsync()
    {
        if (Grid.CurrentRow?.Tag is not InventoryRowTag selected)
        {
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            if (selected.IsProduct)
            {
                var photo = await db.ProductPhotos.SingleOrDefaultAsync(item => item.ProductId == selected.Id);
                if (photo is not null)
                {
                    db.ProductPhotos.Remove(photo);
                    await db.SaveChangesAsync();
                }
            }
            else
            {
                await CatalogImageStore.DeleteAsync(db, "stock-item", selected.Id);
            }
            SetPreview(null);
            _removeImage.Enabled = false;
            Status.Text = "Imagen quitada.";
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo quitar la imagen: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al quitar la imagen: {exception.Message}";
        }
    }

    private async Task LoadSelectedPhotoAsync()
    {
        if (Grid.CurrentRow?.Tag is not InventoryRowTag selected)
        {
            SetPreview(null);
            _removeImage.Enabled = false;
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var data = selected.IsProduct
                ? await db.ProductPhotos.AsNoTracking()
                    .Where(item => item.ProductId == selected.Id)
                    .Select(item => item.Data)
                    .SingleOrDefaultAsync()
                : await CatalogImageStore.LoadAsync(db, "stock-item", selected.Id);
            if (Grid.CurrentRow?.Tag is InventoryRowTag current && current.Id == selected.Id)
            {
                SetPreview(data);
                _removeImage.Enabled = data is not null;
            }
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo cargar la imagen: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al cargar la imagen: {exception.Message}";
        }
    }

    private void SetPreview(byte[]? data)
    {
        Image? image = null;
        if (data is not null)
        {
            using var stream = new MemoryStream(data);
            using var source = Image.FromStream(stream);
            image = new Bitmap(source);
        }
        var previous = _productPreview.Image;
        _productPreview.Image = image;
        previous?.Dispose();
    }

    private async Task CreateStockItemAsync()
    {
        using var dialog = new StockItemEditorDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is not { } request)
        {
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            await new CommerceService(db).CreateStockItemAsync(request, "local-admin", CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = $"Insumo «{request.Name}» creado.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar el insumo: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar el insumo: {exception.Message}";
        }
    }

    private async Task EditSelectedStockItemAsync()
    {
        var tag = Grid.CurrentRow?.Tag as InventoryRowTag;
        var stockItemId = tag?.IsProduct == true ? tag.LinkedStockId : tag?.Id;
        if (stockItemId is not Guid id)
        {
            Status.Text = "Selecciona un insumo o un artículo con existencia enlazada.";
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var stock = await db.StockItems.SingleOrDefaultAsync(item => item.Id == id);
            if (stock is null)
            {
                Status.Text = "El insumo seleccionado ya no existe.";
                return;
            }
            using var dialog = new StockItemEditorDialog(stock);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.UpdateRequest is not { } request)
            {
                return;
            }
            await new CommerceService(db).UpdateStockItemAsync(id, request, "local-admin", CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = $"Insumo «{request.Name}» actualizado.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo actualizar el insumo: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al actualizar el insumo: {exception.Message}";
        }
    }

    private async Task CreateProductAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(Options);
            var stockItems = await db.StockItems.AsNoTracking().OrderBy(item => item.Name)
                .Select(item => new StockItemOption(item.Id, $"{item.Name} · {item.Unit}", item.Unit)).ToListAsync();
            using var dialog = new InventoryEditorDialog(stockItems);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ProductRequest is not { } request)
            {
                return;
            }
            await new CommerceService(db).CreateProductAsync(request, "local-admin", CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = request.IsRecipe
                ? $"Producto preparado «{request.Name}» creado con receta por porción."
                : $"Producto de tienda «{request.Name}» creado.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar el producto: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar el producto: {exception.Message}";
        }
    }

    private async Task EditSelectedProductAsync()
    {
        if (Grid.CurrentRow?.Tag is not InventoryRowTag { IsProduct: true } selected)
        {
            Status.Text = "Selecciona un producto para editar.";
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var product = await db.Products.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == selected.Id);
            if (product is null)
            {
                Status.Text = "El producto seleccionado ya no existe.";
                return;
            }
            var stockItems = await db.StockItems.AsNoTracking().OrderBy(item => item.Name)
                .Select(item => new StockItemOption(item.Id, $"{item.Name} · {item.Unit}", item.Unit)).ToListAsync();
            var components = await db.RecipeComponents.AsNoTracking()
                .Where(item => item.ProductId == product.Id)
                .Select(item => new RecipeComponentInput(item.StockItemId, item.Quantity))
                .ToListAsync();
            var details = await db.ProductDetails.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProductId == product.Id);
            using var dialog = new InventoryEditorDialog(stockItems, product, components, true, details);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ProductRequest is not { } request)
            {
                return;
            }
            await new CommerceService(db).UpdateProductAsync(
                product.Id, request, "local-admin", CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = $"Producto «{request.Name}» actualizado.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo actualizar el producto: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al actualizar el producto: {exception.Message}";
        }
    }

    private async Task ChangeSelectedProductDepartmentAsync()
    {
        if (Grid.CurrentRow?.Tag is not InventoryRowTag { IsProduct: true } selected)
        {
            Status.Text = "Selecciona un producto para cambiar su categoría.";
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var product = await db.Products.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == selected.Id);
            var details = await db.ProductDetails.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProductId == selected.Id);
            if (product is null || details is null)
            {
                Status.Text = "El producto ya no existe o no tiene categoría configurada.";
                return;
            }

            using var dialog = new ProductDepartmentDialog(details.Department);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedDepartment is not { } department)
            {
                return;
            }
            await new CommerceService(db).UpdateProductDepartmentAsync(
                product.Id, department, _user.Username, CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = $"Categoría de «{product.Name}» actualizada a {department}.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo cambiar la categoría: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al cambiar la categoría: {exception.Message}";
        }
    }

    private async Task EditSelectedRecipeAsync()
    {
        if (Grid.CurrentRow?.Tag is not InventoryRowTag { IsProduct: true } selected)
        {
            Status.Text = "Selecciona un producto preparado para editar su receta.";
            return;
        }
        var productId = selected.Id;
        try
        {
            await using var db = new TiendaDbContext(Options);
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(item => item.Id == productId);
            if (product is null || !product.IsRecipe)
            {
                Status.Text = "La selección no es un producto preparado.";
                return;
            }
            var stockItems = await db.StockItems.AsNoTracking().OrderBy(item => item.Name)
                .Select(item => new StockItemOption(item.Id, $"{item.Name} · {item.Unit}", item.Unit)).ToListAsync();
            var components = await db.RecipeComponents.AsNoTracking()
                .Where(item => item.ProductId == productId)
                .Select(item => new RecipeComponentInput(item.StockItemId, item.Quantity))
                .ToListAsync();
            using var dialog = new InventoryEditorDialog(stockItems, product, components);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.RecipeRequest is not { } request)
            {
                return;
            }
            await new CommerceService(db).UpdateRecipeAsync(productId, request, "local-admin", CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = $"Receta de «{product.Name}» actualizada.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo actualizar la receta: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al actualizar la receta: {exception.Message}";
        }
    }
}

internal sealed class CashWindow(DbContextOptions<TiendaDbContext> options)
    : DataModuleWindow(options, "Historial de Ventas · hoy")
{
    protected override async Task LoadDataAsync()
    {
        AddColumns("Hora", "Orden", "Artículo", "Cantidad", "Precio/u. Bs", "Costo/u. Bs",
            "Utilidad bruta Bs", "Margen bruto %", "Pago");
        await using var db = new TiendaDbContext(Options);
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/La_Paz");
        var localBusinessDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, businessTimeZone).Date;
        var fromLocal = DateTime.SpecifyKind(localBusinessDate, DateTimeKind.Unspecified);
        var toLocal = fromLocal.AddDays(1);
        var from = TimeZoneInfo.ConvertTimeToUtc(fromLocal, businessTimeZone);
        var to = TimeZoneInfo.ConvertTimeToUtc(toLocal, businessTimeZone);
        var sales = await db.Sales.AsNoTracking()
            .Where(sale => sale.OccurredAtUtc >= from && sale.OccurredAtUtc < to
                && sale.State == SaleState.Completed)
            .Include(sale => sale.Lines)
            .Include(sale => sale.Payments)
            .OrderByDescending(sale => sale.OccurredAtUtc)
            .ToListAsync();
        var lineIds = sales.SelectMany(sale => sale.Lines).Select(line => line.Id).ToArray();
        var financial = await db.SaleLineFinancialSnapshots.AsNoTracking()
            .Where(item => lineIds.Contains(item.SaleLineId))
            .ToDictionaryAsync(item => item.SaleLineId);
        var totals = sales.SelectMany(sale => sale.Payments)
            .GroupBy(payment => payment.Method)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var collected = group.Sum(payment => payment.Method == PaymentMethod.Cash
                    ? payment.Amount - payment.Change
                    : payment.Amount);
                var method = group.Key == PaymentMethod.Transfer ? "QR" : group.Key.ToString();
                return $"{method}: {PosFormatting.Money(collected)}";
            });
        Status.Text = sales.Count == 0
            ? "Sin ventas completadas hoy."
            : $"{sales.Count} ventas · {string.Join(" · ", totals)}";

        foreach (var sale in sales)
        {
            var paymentMethod = sale.Payments.FirstOrDefault()?.Method;
            var payment = paymentMethod == PaymentMethod.Transfer ? "QR" : paymentMethod?.ToString() ?? "—";
            foreach (var line in sale.Lines)
            {
                financial.TryGetValue(line.Id, out var snapshot);
                var orderName = !string.IsNullOrWhiteSpace(sale.NamedOrder)
                    ? sale.NamedOrder
                    : !string.IsNullOrWhiteSpace(sale.DiningTable)
                        ? sale.DiningTable
                        : sale.FulfillmentMode == FulfillmentMode.Takeaway ? "Para llevar" : "Mostrador";
                Grid.Rows.Add(sale.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                    orderName, line.ProductName, line.Quantity.ToString("0.####"),
                    PosFormatting.Money(line.UnitPrice),
                    snapshot?.UnitCost is decimal unitCost ? PosFormatting.Money(unitCost) : "N/D",
                    snapshot?.GrossMargin is decimal grossMargin ? PosFormatting.Money(grossMargin) : "N/D",
                    snapshot?.GrossMarginPercent?.ToString("0.00") ?? "N/D", payment);
            }
        }
    }
}

internal sealed class SupplierWindow : DataModuleWindow
{
    private readonly ComboBox _supplierFilter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 230
    };
    private readonly DateTimePicker _purchaseFrom = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy",
        Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
        Width = 112
    };
    private readonly DateTimePicker _purchaseTo = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy",
        Value = DateTime.Today,
        Width = 112
    };
    private readonly Label _purchaseFromLabel = new()
    {
        Text = "Desde",
        AutoSize = true,
        Height = 36,
        TextAlign = ContentAlignment.MiddleCenter,
        Visible = false
    };
    private readonly Label _purchaseToLabel = new()
    {
        Text = "Hasta",
        AutoSize = true,
        Height = 36,
        TextAlign = ContentAlignment.MiddleCenter,
        Visible = false
    };
    private readonly Button _editSupplier = new() { Text = "Editar proveedor", AutoSize = true, Height = 36 };
    private readonly Button _paySupplierPurchase = new() { Text = "Registrar abono", AutoSize = true, Height = 36 };
    private readonly Button _changeSupplierImage = new()
        { Text = "Añadir / cambiar imagen", AutoSize = true, Height = 36 };
    private readonly Button _removeSupplierImage = new()
        { Text = "Quitar imagen", AutoSize = true, Height = 36, Enabled = false };
    private readonly PictureBox _supplierImagePreview = new()
    {
        Width = 72,
        Height = 42,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = ModernTheme.SurfaceMuted,
        Margin = new Padding(6, 0, 6, 0)
    };
    private readonly ComboBox _viewMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 190
    };
    private readonly string _actor;
    private bool _loadingSuppliers;

    public SupplierWindow(DbContextOptions<TiendaDbContext> options, string actor)
        : base(options, "Proveedores y compras")
    {
        _actor = actor;
        var createSupplier = new Button { Text = "Nuevo proveedor", AutoSize = true, Height = 36 };
        var receivePurchase = new Button { Text = "Registrar compra", AutoSize = true, Height = 36 };
        StyleButton(createSupplier);
        StyleButton(_editSupplier);
        StyleButton(_paySupplierPurchase);
        StyleButton(_changeSupplierImage);
        StyleButton(_removeSupplierImage);
        ModernTheme.StyleButton(receivePurchase, true);
        _viewMode.Items.AddRange([
            "Proveedores", "Insumos y costos", "Historial de compras", "Deudas a proveedores", "Historial de pagos"
        ]);
        _viewMode.SelectedIndex = 0;
        Header.Controls.Add(_supplierFilter);
        Header.Controls.Add(_viewMode);
        _purchaseFrom.Visible = false;
        _purchaseTo.Visible = false;
        Header.Controls.Add(_purchaseFromLabel);
        Header.Controls.Add(_purchaseFrom);
        Header.Controls.Add(_purchaseToLabel);
        Header.Controls.Add(_purchaseTo);
        Header.Controls.Add(createSupplier);
        Header.Controls.Add(_editSupplier);
        Header.Controls.Add(_changeSupplierImage);
        Header.Controls.Add(_removeSupplierImage);
        Header.Controls.Add(_supplierImagePreview);
        Header.Controls.Add(receivePurchase);
        Header.Controls.Add(_paySupplierPurchase);
        createSupplier.Click += async (_, _) => await CreateSupplierAsync();
        _editSupplier.Click += async (_, _) => await EditSupplierAsync();
        _changeSupplierImage.Click += async (_, _) => await SelectSupplierImageAsync();
        _removeSupplierImage.Click += async (_, _) => await RemoveSupplierImageAsync();
        receivePurchase.Click += async (_, _) => await RegisterPurchaseAsync();
        _paySupplierPurchase.Click += async (_, _) => await PaySupplierPurchaseAsync();
        _viewMode.SelectedIndexChanged += async (_, _) =>
        {
            if (!_loadingSuppliers)
            {
                await RefreshModuleAsync();
            }
        };
        _supplierFilter.SelectedIndexChanged += async (_, _) =>
        {
            if (!_loadingSuppliers)
            {
                await RefreshModuleAsync();
            }
        };
        _purchaseFrom.ValueChanged += async (_, _) =>
        {
            if (_purchaseFrom.Value.Date > _purchaseTo.Value.Date)
            {
                _purchaseTo.Value = _purchaseFrom.Value.Date;
            }
            if (!_loadingSuppliers && _viewMode.SelectedIndex == 2)
            {
                await RefreshModuleAsync();
            }
        };
        _purchaseTo.ValueChanged += async (_, _) =>
        {
            if (_purchaseTo.Value.Date < _purchaseFrom.Value.Date)
            {
                _purchaseFrom.Value = _purchaseTo.Value.Date;
            }
            if (!_loadingSuppliers && _viewMode.SelectedIndex == 2)
            {
                await RefreshModuleAsync();
            }
        };
        Grid.SelectionChanged += async (_, _) =>
        {
            _editSupplier.Enabled = _viewMode.SelectedIndex == 0 && Grid.CurrentRow?.Tag is Guid;
            _paySupplierPurchase.Enabled = _viewMode.SelectedIndex == 3 && Grid.CurrentRow?.Tag is Guid;
            var isSupplierSelected = _viewMode.SelectedIndex == 0 && Grid.CurrentRow?.Tag is Guid;
            _changeSupplierImage.Visible = _viewMode.SelectedIndex == 0;
            _removeSupplierImage.Visible = _viewMode.SelectedIndex == 0;
            _supplierImagePreview.Visible = _viewMode.SelectedIndex == 0;
            _changeSupplierImage.Enabled = isSupplierSelected;
            if (isSupplierSelected)
            {
                await LoadSelectedSupplierImageAsync();
            }
            else
            {
                SetSupplierImagePreview(null);
                _removeSupplierImage.Enabled = false;
            }
        };
        _editSupplier.Enabled = false;
        _paySupplierPurchase.Enabled = false;
    }

    protected override async Task LoadDataAsync()
    {
        await using var db = new TiendaDbContext(Options);
        var previousSupplierId = (_supplierFilter.SelectedItem as SupplierOption)?.Id;
        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .Select(item => new SupplierOption(item.Id, item.Name))
            .ToListAsync();
        _loadingSuppliers = true;
        _supplierFilter.DataSource = null;
        _supplierFilter.DataSource = suppliers;
        _supplierFilter.DisplayMember = nameof(SupplierOption.Name);
        _supplierFilter.ValueMember = nameof(SupplierOption.Id);
        var selectedIndex = previousSupplierId is Guid previous
            ? suppliers.FindIndex(item => item.Id == previous)
            : -1;
        _supplierFilter.SelectedIndex = selectedIndex >= 0 ? selectedIndex : -1;
        _loadingSuppliers = false;
        var supplierId = (_supplierFilter.SelectedItem as SupplierOption)?.Id;
        _purchaseFromLabel.Visible = _viewMode.SelectedIndex == 2;
        _purchaseFrom.Visible = _viewMode.SelectedIndex == 2;
        _purchaseToLabel.Visible = _viewMode.SelectedIndex == 2;
        _purchaseTo.Visible = _viewMode.SelectedIndex == 2;
        _editSupplier.Visible = _viewMode.SelectedIndex == 0;
        _changeSupplierImage.Visible = _viewMode.SelectedIndex == 0;
        _removeSupplierImage.Visible = _viewMode.SelectedIndex == 0;
        _supplierImagePreview.Visible = _viewMode.SelectedIndex == 0;
        _paySupplierPurchase.Visible = _viewMode.SelectedIndex == 3;
        _editSupplier.Enabled = false;
        _paySupplierPurchase.Enabled = false;
        _changeSupplierImage.Enabled = false;
        _removeSupplierImage.Enabled = false;
        if (_viewMode.SelectedIndex != 0)
        {
            SetSupplierImagePreview(null);
        }
        if (_viewMode.SelectedIndex == 0)
        {
            AddColumns("Proveedor", "Teléfono", "Contacto", "Dirección", "NIT / referencia", "Artículos asociados");
            var supplierRows = await db.Suppliers.AsNoTracking()
                .Where(item => item.IsActive && (supplierId == null || item.Id == supplierId))
                .OrderBy(item => item.Name)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.Phone,
                    item.ContactName,
                    item.Address,
                    item.TaxId,
                    ItemCount = db.SupplierStockItems.Count(link => link.SupplierId == item.Id)
                }).ToListAsync();
            foreach (var supplier in supplierRows)
            {
                var row = Grid.Rows.Add(supplier.Name, supplier.Phone ?? "—", supplier.ContactName ?? "—",
                    supplier.Address ?? "—", supplier.TaxId ?? "—", supplier.ItemCount);
                Grid.Rows[row].Tag = supplier.Id;
            }
            _editSupplier.Enabled = Grid.CurrentRow?.Tag is Guid;
            Status.Text = supplierRows.Count == 0
                ? "No hay proveedores registrados."
                : $"Proveedores registrados: {supplierRows.Count}";
            return;
        }

        if (_viewMode.SelectedIndex == 2)
        {
            AddColumns("Fecha", "Proveedor", "Referencia", "Pago", "Total (Bs)", "Pagado (Bs)",
                "Saldo (Bs)", "Insumo / artículo", "Cantidad", "Costo unitario (Bs)", "Total línea (Bs)");
            var fromLocal = _purchaseFrom.Value.Date;
            var toExclusiveLocal = _purchaseTo.Value.Date.AddDays(1);
            var fromUtc = new DateTimeOffset(fromLocal, TimeZoneInfo.Local.GetUtcOffset(fromLocal)).UtcDateTime;
            var toExclusiveUtc = new DateTimeOffset(
                toExclusiveLocal, TimeZoneInfo.Local.GetUtcOffset(toExclusiveLocal)).UtcDateTime;
            var purchases = await (from line in db.PurchaseLines.AsNoTracking()
                                   join order in db.PurchaseOrders.AsNoTracking()
                                       on line.PurchaseOrderId equals order.Id
                                   join supplier in db.Suppliers.AsNoTracking()
                                       on order.SupplierId equals supplier.Id
                                   join stock in db.StockItems.AsNoTracking()
                                       on line.StockItemId equals stock.Id
                                   where (supplierId == null || order.SupplierId == supplierId)
                                       && order.ReceivedAtUtc >= fromUtc
                                       && order.ReceivedAtUtc < toExclusiveUtc
                                   orderby order.ReceivedAtUtc descending
                                   select new
                                   {
                                       order.Id,
                                       order.ReceivedAtUtc,
                                       Supplier = supplier.Name,
                                       order.InvoiceReference,
                                       order.PaymentMethod,
                                       order.Total,
                                       order.AmountPaid,
                                       Item = stock.Name,
                                       line.Quantity,
                                       line.UnitCost,
                                       line.LineTotal
                                   }).ToListAsync();
            foreach (var purchase in purchases)
            {
                Grid.Rows.Add(purchase.ReceivedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    purchase.Supplier, purchase.InvoiceReference, purchase.PaymentMethod switch
                    {
                        PaymentMethod.Cash => "Efectivo",
                        PaymentMethod.Transfer => "QR",
                        PaymentMethod.Credit => "Crédito",
                        _ => purchase.PaymentMethod.ToString()
                    }, PosFormatting.Money(purchase.Total), PosFormatting.Money(purchase.AmountPaid),
                    PosFormatting.Money(purchase.Total - purchase.AmountPaid), purchase.Item,
                    purchase.Quantity.ToString("0.######"), PosFormatting.Money(purchase.UnitCost),
                    PosFormatting.Money(purchase.LineTotal));
            }
            var orderCount = purchases.Select(item => item.Id).Distinct().Count();
            Status.Text = $"Período {fromLocal:dd/MM/yyyy}–{_purchaseTo.Value:dd/MM/yyyy} · "
                + $"{orderCount} compras, {purchases.Count} líneas · Total: "
                + PosFormatting.Money(purchases.Sum(item => item.LineTotal));
            return;
        }

        if (_viewMode.SelectedIndex == 3)
        {
            AddColumns("Proveedor", "Fecha de compra", "Referencia", "Vencimiento", "Estado",
                "Total (Bs)", "Pagado (Bs)", "Saldo pendiente (Bs)");
            var debts = await (from order in db.PurchaseOrders.AsNoTracking()
                               join supplier in db.Suppliers.AsNoTracking() on order.SupplierId equals supplier.Id
                               where order.PaymentMethod == PaymentMethod.Credit
                                   && order.Total > order.AmountPaid
                                   && (supplierId == null || order.SupplierId == supplierId)
                               orderby order.ReceivedAtUtc
                               select new
                               {
                                   order.Id,
                                   Supplier = supplier.Name,
                                   order.ReceivedAtUtc,
                                   order.InvoiceReference,
                                   order.Total,
                                   order.AmountPaid,
                                   order.DueAtUtc
                               }).ToListAsync();
            foreach (var debt in debts)
            {
                var dueDate = debt.DueAtUtc is DateTime dueAt
                    ? DateTime.SpecifyKind(dueAt, DateTimeKind.Utc).ToLocalTime().Date
                    : (DateTime?)null;
                var daysOverdue = dueDate is DateTime date
                    ? (DateTime.Today - date).Days
                    : 0;
                var dueLabel = dueDate?.ToString("dd/MM/yyyy") ?? "Sin fecha";
                var state = dueDate is null
                    ? "Sin vencimiento"
                    : daysOverdue > 0
                        ? $"Vencida · {daysOverdue} días"
                        : daysOverdue == 0
                            ? "Vence hoy"
                            : $"En {Math.Abs(daysOverdue)} días";
                var row = Grid.Rows.Add(
                    debt.Supplier,
                    debt.ReceivedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    debt.InvoiceReference,
                    dueLabel,
                    state,
                    PosFormatting.Money(debt.Total),
                    PosFormatting.Money(debt.AmountPaid),
                    PosFormatting.Money(debt.Total - debt.AmountPaid));
                Grid.Rows[row].Tag = debt.Id;
                if (daysOverdue > 0)
                {
                    Grid.Rows[row].Cells[4].Style.ForeColor = Color.Firebrick;
                    Grid.Rows[row].Cells[4].Style.Font = new Font(Grid.Font, FontStyle.Bold);
                }
            }
            Status.Text = $"Compras a crédito pendientes: {debts.Count} · Saldo total: "
                + PosFormatting.Money(debts.Sum(item => item.Total - item.AmountPaid));
            _paySupplierPurchase.Enabled = Grid.CurrentRow?.Tag is Guid;
            return;
        }

        if (_viewMode.SelectedIndex == 4)
        {
            AddColumns("Fecha de pago", "Proveedor", "Referencia", "Método", "Abono (Bs)",
                "Registró", "Terminal / sesión", "Detalle");
            var payments = await (from payment in db.SupplierPayments.AsNoTracking()
                                  join order in db.PurchaseOrders.AsNoTracking()
                                      on payment.PurchaseOrderId equals order.Id
                                  join supplier in db.Suppliers.AsNoTracking()
                                      on order.SupplierId equals supplier.Id
                                  where supplierId == null || order.SupplierId == supplierId
                                  orderby payment.OccurredAtUtc descending
                                  select new
                                  {
                                      payment.OccurredAtUtc,
                                      Supplier = supplier.Name,
                                      order.InvoiceReference,
                                      payment.PaymentMethod,
                                      payment.Amount,
                                      payment.Actor,
                                      payment.CashRegisterSessionId,
                                      payment.Note
                                  }).ToListAsync();
            foreach (var payment in payments)
            {
                Grid.Rows.Add(
                    payment.OccurredAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    payment.Supplier,
                    payment.InvoiceReference,
                    payment.PaymentMethod == PaymentMethod.Transfer ? "QR" : "Efectivo",
                    PosFormatting.Money(payment.Amount),
                    payment.Actor,
                    payment.CashRegisterSessionId.ToString("N")[..8],
                    payment.Note ?? "—");
            }
            Status.Text = $"Abonos a proveedores: {payments.Count} · Total pagado: "
                + PosFormatting.Money(payments.Sum(item => item.Amount));
            return;
        }

        AddColumns("Proveedor", "Teléfono", "Contacto", "Insumo / artículo",
            "Costo acordado (Bs)", "Stock actual");
        var catalog = await (from supplierItem in db.SupplierStockItems.AsNoTracking()
                             join supplier in db.Suppliers.AsNoTracking() on supplierItem.SupplierId equals supplier.Id
                             join stock in db.StockItems.AsNoTracking() on supplierItem.StockItemId equals stock.Id
                             where supplierId == null || supplierItem.SupplierId == supplierId
                             orderby supplier.Name, stock.Name
                             select new
                             {
                                 Supplier = supplier.Name,
                                 supplier.Phone,
                                 supplier.ContactName,
                                 Item = stock.Name,
                                 supplierItem.LastUnitCost,
                                 stock.OnHand,
                                 stock.Unit
                             }).ToListAsync();
        foreach (var item in catalog)
        {
            Grid.Rows.Add(item.Supplier, item.Phone ?? "—", item.ContactName ?? "—",
                item.Item, PosFormatting.Money(item.LastUnitCost), $"{item.OnHand:0.######} {item.Unit}");
        }
        Status.Text = $"Asociaciones de artículos y proveedores: {catalog.Count}";
    }

    private async Task CreateSupplierAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(Options);
            var stockItems = await db.StockItems.AsNoTracking().OrderBy(item => item.Name)
                .Select(item => new StockOption(item.Id, item.Name, item.Unit, item.UnitCost))
                .ToListAsync();
            using var dialog = new SupplierEditorDialog(null, stockItems);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is not { } request)
            {
                return;
            }
            _loadingSuppliers = true;
            _viewMode.SelectedIndex = 0;
            _loadingSuppliers = false;
            var created = await new CommerceService(db).CreateSupplierAsync(request, CancellationToken.None);
            await RefreshModuleAsync();
            SelectSupplier(created.Id);
            await LoadSelectedSupplierImageAsync();
            Status.Text = $"Proveedor «{created.Name}» registrado.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar el proveedor: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar el proveedor: {exception.Message}";
        }
    }

    private async Task EditSupplierAsync()
    {
        var supplierId = Grid.CurrentRow?.Tag is Guid rowSupplierId
            ? rowSupplierId
            : (_supplierFilter.SelectedItem as SupplierOption)?.Id;
        if (supplierId is not Guid id)
        {
            Status.Text = "Selecciona un proveedor para editar.";
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var supplier = await db.Suppliers.AsNoTracking().SingleAsync(item => item.Id == id);
            var stockItems = await db.StockItems.AsNoTracking().OrderBy(item => item.Name)
                .Select(item => new StockOption(item.Id, item.Name, item.Unit, item.UnitCost))
                .ToListAsync();
            var linkedItems = await db.SupplierStockItems.AsNoTracking()
                .Where(item => item.SupplierId == id)
                .ToDictionaryAsync(item => item.StockItemId, item => item.LastUnitCost);
            using var dialog = new SupplierEditorDialog(supplier, stockItems, linkedItems);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is not { } request)
            {
                return;
            }
            var updated = await new CommerceService(db).UpdateSupplierAsync(
                supplier.Id, request, "local-admin", CancellationToken.None);
            await RefreshModuleAsync();
            SelectSupplier(updated.Id);
            await LoadSelectedSupplierImageAsync();
            Status.Text = $"Datos de «{updated.Name}» actualizados.";
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo actualizar el proveedor: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al actualizar el proveedor: {exception.Message}";
        }
    }

    private async Task SelectSupplierImageAsync()
    {
        if (_viewMode.SelectedIndex != 0 || Grid.CurrentRow?.Tag is not Guid supplierId)
        {
            Status.Text = "Selecciona un proveedor en la vista Proveedores para cargar una imagen.";
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Seleccionar imagen del proveedor",
            Filter = "Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var image = await CatalogImageStore.ReadAndNormalizeAsync(dialog.FileName);
            await using var db = new TiendaDbContext(Options);
            await CatalogImageStore.SaveAsync(db, "supplier", supplierId, image);
            if (Grid.CurrentRow?.Tag is Guid selectedId && selectedId == supplierId)
            {
                SetSupplierImagePreview(image);
                _removeSupplierImage.Enabled = true;
            }
            Status.Text = "Imagen del proveedor guardada localmente y optimizada para el sistema.";
        }
        catch (IOException exception)
        {
            Status.Text = $"No se pudo leer la imagen del proveedor: {exception.Message}";
        }
        catch (ArgumentException exception)
        {
            Status.Text = $"Windows no reconoce el contenido como imagen: {exception.Message}";
        }
        catch (OutOfMemoryException)
        {
            Status.Text = "No se pudo decodificar esta imagen. Prueba con otro formato de imagen.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar la imagen del proveedor: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar la imagen del proveedor: {exception.Message}";
        }
        catch (ExternalException exception)
        {
            Status.Text = $"El decodificador de Windows no pudo abrir la imagen: {exception.Message}";
        }
    }

    private async Task RemoveSupplierImageAsync()
    {
        if (_viewMode.SelectedIndex != 0 || Grid.CurrentRow?.Tag is not Guid supplierId)
        {
            return;
        }

        try
        {
            await using var db = new TiendaDbContext(Options);
            await CatalogImageStore.DeleteAsync(db, "supplier", supplierId);
            SetSupplierImagePreview(null);
            _removeSupplierImage.Enabled = false;
            Status.Text = "Imagen del proveedor quitada.";
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo quitar la imagen del proveedor: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al quitar la imagen del proveedor: {exception.Message}";
        }
    }

    private async Task LoadSelectedSupplierImageAsync()
    {
        if (_viewMode.SelectedIndex != 0 || Grid.CurrentRow?.Tag is not Guid supplierId)
        {
            SetSupplierImagePreview(null);
            _removeSupplierImage.Enabled = false;
            return;
        }

        try
        {
            await using var db = new TiendaDbContext(Options);
            var image = await CatalogImageStore.LoadAsync(db, "supplier", supplierId);
            if (Grid.CurrentRow?.Tag is Guid selectedId && selectedId == supplierId)
            {
                SetSupplierImagePreview(image);
                _removeSupplierImage.Enabled = image is not null;
            }
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo cargar la imagen del proveedor: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al cargar la imagen del proveedor: {exception.Message}";
        }
    }

    private void SetSupplierImagePreview(byte[]? data)
    {
        Image? image = null;
        if (data is not null)
        {
            using var stream = new MemoryStream(data);
            using var source = Image.FromStream(stream);
            image = new Bitmap(source);
        }

        var previous = _supplierImagePreview.Image;
        _supplierImagePreview.Image = image;
        previous?.Dispose();
    }

    private async Task RegisterPurchaseAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(Options);
            var suppliers = await db.Suppliers.AsNoTracking()
                .Where(item => item.IsActive)
                .OrderBy(item => item.Name)
                .Select(item => new SupplierOption(item.Id, item.Name))
                .ToListAsync();
            var stockItems = await db.StockItems.AsNoTracking()
                .OrderBy(item => item.Name)
                .Select(item => new StockOption(item.Id, item.Name, item.Unit, item.UnitCost))
                .ToListAsync();
            if (suppliers.Count == 0 || stockItems.Count == 0)
            {
                Status.Text = suppliers.Count == 0
                    ? "Registra un proveedor antes de ingresar una compra."
                    : "Crea al menos un insumo/artículo de inventario antes de ingresar una compra.";
                return;
            }
            var supplierPrices = await db.SupplierStockItems.AsNoTracking()
                .ToDictionaryAsync(
                    item => (item.SupplierId, item.StockItemId),
                    item => item.LastUnitCost);
            using var dialog = new PurchaseReceiveDialog(suppliers, stockItems,
                (_supplierFilter.SelectedItem as SupplierOption)?.Id,
                supplierPrices);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is not { } request)
            {
                return;
            }
            if (request.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Transfer)
            {
                var terminalId = Environment.GetEnvironmentVariable("TIENDA_TERMINAL_ID")
                    ?? Environment.MachineName;
                var sessionId = await db.CashRegisterSessions.AsNoTracking()
                    .Where(session => session.TerminalId == terminalId && session.ClosedAtUtc == null)
                    .Select(session => (Guid?)session.Id)
                    .SingleOrDefaultAsync();
                if (sessionId is null)
                {
                    Status.Text = "Abre la caja de esta terminal antes de registrar una compra pagada en efectivo o QR.";
                    return;
                }
                request = request with { CashRegisterSessionId = sessionId };
            }
            var result = await new CommerceService(db).ReceivePurchaseAsync(request, "local-admin", CancellationToken.None);
            _loadingSuppliers = true;
            _viewMode.SelectedIndex = 2;
            _loadingSuppliers = false;
            await RefreshModuleAsync();
            SelectSupplier(request.SupplierId);
            Status.Text = request.PaymentMethod == PaymentMethod.Credit
                ? $"Compra a crédito registrada por {PosFormatting.Money(result.Total)}. Stock actualizado; no afecta la caja."
                : $"Compra pagada por {PosFormatting.Money(result.Total)} registrada en caja ({(request.PaymentMethod == PaymentMethod.Cash ? "efectivo" : "QR")}).";
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo registrar la compra: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al registrar la compra: {exception.Message}";
        }
    }

    private async Task PaySupplierPurchaseAsync()
    {
        if (Grid.CurrentRow?.Tag is not Guid purchaseId)
        {
            Status.Text = "Selecciona primero una compra pendiente.";
            return;
        }

        try
        {
            var terminalId = Environment.GetEnvironmentVariable("TIENDA_TERMINAL_ID")
                ?? Environment.MachineName;
            await using var db = new TiendaDbContext(Options);
            var purchase = await (from order in db.PurchaseOrders.AsNoTracking()
                                  join supplier in db.Suppliers.AsNoTracking() on order.SupplierId equals supplier.Id
                                  where order.Id == purchaseId
                                  select new
                                  {
                                      order.Id,
                                      order.InvoiceReference,
                                      order.Total,
                                      order.AmountPaid,
                                      order.PaymentMethod,
                                      SupplierName = supplier.Name
                                  }).SingleOrDefaultAsync();
            if (purchase is null || purchase.PaymentMethod != PaymentMethod.Credit)
            {
                Status.Text = "La compra seleccionada ya no está disponible como deuda a crédito.";
                await RefreshModuleAsync();
                return;
            }
            var outstanding = decimal.Round(purchase.Total - purchase.AmountPaid, 2, MidpointRounding.AwayFromZero);
            if (outstanding <= 0)
            {
                Status.Text = "Esta compra ya fue pagada por completo.";
                await RefreshModuleAsync();
                return;
            }

            var sessionId = await db.CashRegisterSessions.AsNoTracking()
                .Where(session => session.TerminalId == terminalId && session.ClosedAtUtc == null)
                .Select(session => (Guid?)session.Id)
                .SingleOrDefaultAsync();
            if (sessionId is null)
            {
                Status.Text = "Abre la caja de esta terminal antes de registrar un pago al proveedor.";
                return;
            }

            using var dialog = new SupplierPaymentDialog(
                purchase.SupplierName, purchase.InvoiceReference, purchase.Total, purchase.AmountPaid);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Payment is not { } payment)
            {
                return;
            }
            var result = await new CommerceService(db).RecordSupplierPurchasePaymentAsync(
                new SupplierPurchasePaymentRequest(
                    purchase.Id, payment.Amount, payment.Method, sessionId.Value, payment.Note),
                _actor,
                CancellationToken.None);
            await RefreshModuleAsync();
            Status.Text = result.OutstandingBalance == 0
                ? $"Deuda de {purchase.SupplierName} saldada. Pago registrado: {PosFormatting.Money(result.PaymentAmount)}."
                : $"Abono registrado para {purchase.SupplierName}. Saldo restante: {PosFormatting.Money(result.OutstandingBalance)}.";
        }
        catch (KeyNotFoundException exception)
        {
            Status.Text = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar el pago al proveedor: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar el pago: {exception.Message}";
        }
    }

    private void SelectSupplier(Guid supplierId)
    {
        for (var index = 0; index < _supplierFilter.Items.Count; index++)
        {
            if (_supplierFilter.Items[index] is SupplierOption option && option.Id == supplierId)
            {
                _supplierFilter.SelectedIndex = index;
                return;
            }
        }
    }

    internal sealed record SupplierOption(Guid Id, string Name);
    internal sealed record StockOption(Guid Id, string Name, string Unit, decimal UnitCost)
    {
        public override string ToString() => $"{Name} · {Unit}";
    }
}

internal sealed class SupplierPaymentDialog : Form
{
    private readonly NumericUpDown _amount = new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = 2,
        Minimum = 0.01m,
        Maximum = 999999999m,
        Increment = 1m,
        ThousandsSeparator = true
    };
    private readonly ComboBox _method = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill
    };
    private readonly TextBox _note = new()
    {
        MaxLength = 160,
        Dock = DockStyle.Fill,
        PlaceholderText = "Referencia o detalle (opcional)"
    };

    public PaymentEntry? Payment { get; private set; }

    public SupplierPaymentDialog(string supplier, string reference, decimal total, decimal paid)
    {
        var outstanding = decimal.Round(total - paid, 2, MidpointRounding.AwayFromZero);
        Text = "Abonar deuda a proveedor";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 300);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        _amount.Maximum = Math.Max(0.01m, outstanding);
        _amount.Value = _amount.Maximum;
        _method.DataSource = new[]
        {
            new SupplierPaymentMethodChoice("Efectivo", PaymentMethod.Cash),
            new SupplierPaymentMethodChoice("QR", PaymentMethod.Transfer)
        };
        _method.DisplayMember = nameof(SupplierPaymentMethodChoice.Name);
        _method.ValueMember = nameof(SupplierPaymentMethodChoice.Method);
        ModernTheme.StyleInput(_amount);
        ModernTheme.StyleInput(_method);
        ModernTheme.StyleInput(_note);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 5,
            BackColor = ModernTheme.Canvas
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var summary = new Label
        {
            Text = $"{supplier} · {reference}\nTotal: {PosFormatting.Money(total)} Bs · Pagado: {PosFormatting.Money(paid)} Bs · Saldo: {PosFormatting.Money(outstanding)} Bs",
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.OrangeDark,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(summary, 0, 0);
        layout.SetColumnSpan(summary, 2);
        AddField(layout, "Abono (Bs)", _amount, 1);
        AddField(layout, "Método", _method, 2);
        AddField(layout, "Detalle", _note, 3);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = "Registrar pago", AutoSize = true, Height = 40 };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, Height = 40, DialogResult = DialogResult.Cancel };
        ModernTheme.StyleButton(save, true);
        DataModuleWindow.StyleButton(cancel);
        save.Click += (_, _) => SavePayment(outstanding);
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 4);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void SavePayment(decimal outstanding)
    {
        if (_amount.Value <= 0 || _amount.Value > outstanding
            || _method.SelectedItem is not SupplierPaymentMethodChoice method)
        {
            MessageBox.Show(this, $"El abono debe estar entre 0,01 y {PosFormatting.Money(outstanding)} Bs.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Payment = new PaymentEntry(
            _amount.Value,
            method.Method,
            string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim());
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private sealed record SupplierPaymentMethodChoice(string Name, PaymentMethod Method);
    public sealed record PaymentEntry(decimal Amount, PaymentMethod Method, string? Note);
}

internal sealed class SupplierEditorDialog : Form
{
    private readonly TextBox _name = new() { MaxLength = 160 };
    private readonly TextBox _phone = new() { MaxLength = 32 };
    private readonly TextBox _contact = new() { MaxLength = 120 };
    private readonly TextBox _address = new() { MaxLength = 240 };
    private readonly TextBox _taxId = new() { MaxLength = 40 };
    private readonly DataGridView _items = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        BackgroundColor = ModernTheme.Surface,
        BorderStyle = BorderStyle.None
    };

    public CreateSupplierRequest? Request { get; private set; }

    public SupplierEditorDialog(
        Supplier? supplier,
        IReadOnlyList<SupplierWindow.StockOption> stockItems,
        IReadOnlyDictionary<Guid, decimal>? linkedItems = null)
    {
        Text = supplier is null ? "Nuevo proveedor" : "Editar proveedor";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(720, 620);
        Size = new Size(800, 700);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        _name.Text = supplier?.Name ?? string.Empty;
        _phone.Text = supplier?.Phone ?? string.Empty;
        _contact.Text = supplier?.ContactName ?? string.Empty;
        _address.Text = supplier?.Address ?? string.Empty;
        _taxId.Text = supplier?.TaxId ?? string.Empty;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = ModernTheme.Canvas
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var entries = new (string Label, Control Input)[]
        {
            ("Nombre *", _name), ("Teléfono *", _phone), ("Contacto *", _contact),
            ("Dirección *", _address), ("NIT / referencia", _taxId)
        };
        for (var row = 0; row < entries.Length; row++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            fields.Controls.Add(new Label { Text = entries[row].Label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            entries[row].Input.Dock = DockStyle.Fill;
            fields.Controls.Add(entries[row].Input, 1, row);
        }
        root.Controls.Add(fields, 0, 0);

        _items.EnableHeadersVisualStyles = false;
        _items.ColumnHeadersDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
        _items.ColumnHeadersDefaultCellStyle.ForeColor = ModernTheme.Purple;
        _items.DefaultCellStyle.BackColor = ModernTheme.Surface;
        _items.DefaultCellStyle.ForeColor = ModernTheme.Ink;
        _items.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Selected",
            HeaderText = "Asociar",
            FillWeight = 16
        });
        _items.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Item",
            HeaderText = "Insumo / artículo",
            ReadOnly = true,
            FillWeight = 54
        });
        _items.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "UnitCost",
            HeaderText = "Costo acordado (Bs)",
            FillWeight = 30,
            DefaultCellStyle = { Format = "0.####" }
        });
        foreach (var item in stockItems)
        {
            var selected = linkedItems?.ContainsKey(item.Id) == true;
            var cost = linkedItems?.GetValueOrDefault(item.Id) ?? item.UnitCost;
            var row = _items.Rows.Add(selected, $"{item.Name} · {item.Unit}", cost.ToString("0.####", CultureInfo.CurrentCulture));
            _items.Rows[row].Tag = item.Id;
        }
        var itemSection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        itemSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        itemSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        itemSection.Controls.Add(new Label
        {
            Text = "Artículos que suministra · marca los que compras y registra su costo acordado",
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.Ink,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        }, 0, 0);
        itemSection.Controls.Add(_items, 0, 1);
        root.Controls.Add(itemSection, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "Guardar", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        ModernTheme.StyleButton(save, true);
        DataModuleWindow.StyleButton(cancel);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_phone.Text)
                || string.IsNullOrWhiteSpace(_address.Text) || string.IsNullOrWhiteSpace(_contact.Text))
            {
                MessageBox.Show(this, "Nombre, teléfono, contacto y dirección son obligatorios.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                var missing = string.IsNullOrWhiteSpace(_name.Text) ? _name
                    : string.IsNullOrWhiteSpace(_phone.Text) ? _phone
                    : string.IsNullOrWhiteSpace(_contact.Text) ? _contact
                    : _address;
                missing.Focus();
                return;
            }
            _items.EndEdit();
            var associations = new List<SupplierStockItemInput>();
            foreach (DataGridViewRow row in _items.Rows)
            {
                if (row.Cells["Selected"].Value is not true)
                {
                    continue;
                }
                var costText = Convert.ToString(row.Cells["UnitCost"].Value) ?? string.Empty;
                var parsed = decimal.TryParse(costText, NumberStyles.Number,
                    CultureInfo.CurrentCulture, out var unitCost);
                if (!parsed)
                {
                    parsed = decimal.TryParse(costText, NumberStyles.Number,
                        CultureInfo.InvariantCulture, out unitCost);
                }
                if (row.Tag is not Guid stockItemId || !parsed
                    || unitCost < 0 || decimal.Round(unitCost, 4) != unitCost)
                {
                    MessageBox.Show(this, "Revisa el costo de los artículos seleccionados (máximo 4 decimales).",
                        "Revisar costos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                associations.Add(new SupplierStockItemInput(stockItemId, unitCost));
            }
            Request = new CreateSupplierRequest(
                _name.Text.Trim(), Optional(_taxId.Text), Optional(_phone.Text), Optional(_address.Text),
                Optional(_contact.Text), associations);
            DialogResult = DialogResult.OK;
            Close();
        };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

internal sealed class PurchaseReceiveDialog : Form
{
    private readonly ComboBox _supplier = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _paymentMethod = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _reference = new() { MaxLength = 100, Dock = DockStyle.Fill };
    private readonly DateTimePicker _receivedAt = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy HH:mm",
        Dock = DockStyle.Fill
    };
    private readonly DateTimePicker _dueAt = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy",
        Value = DateTime.Today.AddDays(30),
        Dock = DockStyle.Fill
    };
    private readonly FlowLayoutPanel _lineList = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = ModernTheme.Surface
    };
    private readonly IReadOnlyList<SupplierWindow.StockOption> _stockItems;
    private readonly IReadOnlyDictionary<(Guid SupplierId, Guid StockItemId), decimal> _supplierPrices;

    public ReceivePurchaseRequest? Request { get; private set; }

    public PurchaseReceiveDialog(
        IReadOnlyList<SupplierWindow.SupplierOption> suppliers,
        IReadOnlyList<SupplierWindow.StockOption> stockItems,
        Guid? selectedSupplierId,
        IReadOnlyDictionary<(Guid SupplierId, Guid StockItemId), decimal> supplierPrices)
    {
        _stockItems = stockItems;
        _supplierPrices = supplierPrices;
        Text = "Registrar compra · actualiza inventario";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(760, 540);
        Size = new Size(860, 620);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        _supplier.DataSource = suppliers.ToList();
        _supplier.DisplayMember = nameof(SupplierWindow.SupplierOption.Name);
        _supplier.ValueMember = nameof(SupplierWindow.SupplierOption.Id);
        if (selectedSupplierId is Guid id)
        {
            _supplier.SelectedValue = id;
        }
        _reference.Text = $"COMPRA-{DateTime.Now:yyyyMMdd-HHmm}";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var metadata = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4 };
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metadata.Controls.Add(new Label { Text = "Proveedor", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        metadata.Controls.Add(_supplier, 1, 0);
        metadata.Controls.Add(new Label { Text = "Referencia", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        metadata.Controls.Add(_reference, 3, 0);
        metadata.Controls.Add(new Label { Text = "Fecha recepción", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        metadata.Controls.Add(_receivedAt, 1, 1);
        metadata.SetColumnSpan(_receivedAt, 3);
        metadata.Controls.Add(new Label { Text = "Método de pago", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        _paymentMethod.DataSource = new[]
        {
            new PaymentChoice("Efectivo", PaymentMethod.Cash),
            new PaymentChoice("QR", PaymentMethod.Transfer),
            new PaymentChoice("Crédito", PaymentMethod.Credit)
        };
        _paymentMethod.DisplayMember = nameof(PaymentChoice.Name);
        _paymentMethod.ValueMember = nameof(PaymentChoice.Method);
        metadata.Controls.Add(_paymentMethod, 1, 2);
        metadata.SetColumnSpan(_paymentMethod, 3);
        metadata.Controls.Add(new Label { Text = "Vence el", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        metadata.Controls.Add(_dueAt, 1, 3);
        metadata.SetColumnSpan(_dueAt, 3);
        _paymentMethod.SelectedValueChanged += (_, _) =>
            _dueAt.Enabled = _paymentMethod.SelectedValue is PaymentMethod.Credit;
        _dueAt.Enabled = false;
        layout.Controls.Add(metadata, 0, 0);
        var lineHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        lineHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        lineHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        lineHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        lineHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
        foreach (var label in new[] { "Insumo / artículo", "Cantidad", "Costo unitario (Bs)", "" })
        {
            lineHeader.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        }
        layout.Controls.Add(lineHeader, 0, 1);
        layout.Controls.Add(_lineList, 0, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "Registrar y actualizar stock", AutoSize = true };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var addLine = new Button { Text = "Añadir insumo", AutoSize = true };
        ModernTheme.StyleButton(save, true);
        DataModuleWindow.StyleButton(cancel);
        DataModuleWindow.StyleButton(addLine);
        addLine.Click += (_, _) => AddLine();
        save.Click += (_, _) => SavePurchase();
        _supplier.SelectedValueChanged += (_, _) => RefreshSuggestedCosts();
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        footer.Controls.Add(addLine);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        AddLine();
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void AddLine()
    {
        var row = new TableLayoutPanel
        {
            Width = 790,
            Height = 42,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(2),
            BackColor = ModernTheme.Surface
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
        var stock = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DataSource = _stockItems.ToList() };
        var quantity = CreateAmountControl(6, 1m);
        var cost = CreateAmountControl(4, _stockItems[0].UnitCost);
        stock.SelectedValueChanged += (_, _) =>
        {
            if (stock.SelectedItem is SupplierWindow.StockOption selected)
            {
                cost.Value = GetSuggestedCost(selected);
            }
        };
        var remove = new Button { Text = "Quitar", Dock = DockStyle.Fill };
        DataModuleWindow.StyleButton(remove);
        remove.Click += (_, _) => _lineList.Controls.Remove(row);
        row.Controls.Add(stock, 0, 0);
        row.Controls.Add(quantity, 1, 0);
        row.Controls.Add(cost, 2, 0);
        row.Controls.Add(remove, 3, 0);
        _lineList.Controls.Add(row);
    }

    private void RefreshSuggestedCosts()
    {
        foreach (var row in _lineList.Controls.OfType<TableLayoutPanel>())
        {
            if (row.Controls[0] is ComboBox stockBox
                && stockBox.SelectedItem is SupplierWindow.StockOption stock
                && row.Controls[2] is NumericUpDown cost)
            {
                cost.Value = GetSuggestedCost(stock);
            }
        }
    }

    private decimal GetSuggestedCost(SupplierWindow.StockOption stock)
    {
        if (_supplier.SelectedItem is SupplierWindow.SupplierOption supplier
            && _supplierPrices.TryGetValue((supplier.Id, stock.Id), out var lastCost))
        {
            return Math.Clamp(lastCost, 0, 999999999);
        }
        return Math.Clamp(stock.UnitCost, 0, 999999999);
    }

    private void SavePurchase()
    {
        var lines = new List<PurchaseLineRequest>();
        foreach (TableLayoutPanel row in _lineList.Controls.OfType<TableLayoutPanel>())
        {
            if (row.Controls[0] is not ComboBox stockBox || stockBox.SelectedItem is not SupplierWindow.StockOption stock
                || row.Controls[1] is not NumericUpDown quantity || row.Controls[2] is not NumericUpDown cost)
            {
                continue;
            }
            if (quantity.Value <= 0)
            {
                MessageBox.Show(this, "Cada cantidad debe ser mayor que cero.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                quantity.Focus();
                return;
            }
            lines.Add(new PurchaseLineRequest(stock.Id, quantity.Value, cost.Value));
        }
        if (_supplier.SelectedItem is not SupplierWindow.SupplierOption supplier
            || string.IsNullOrWhiteSpace(_reference.Text) || lines.Count == 0)
        {
            MessageBox.Show(this, "Selecciona proveedor, ingresa una referencia y añade al menos un artículo.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var paymentMethod = (_paymentMethod.SelectedItem as PaymentChoice)?.Method ?? PaymentMethod.Cash;
        if (paymentMethod == PaymentMethod.Credit && _dueAt.Value.Date < _receivedAt.Value.Date)
        {
            MessageBox.Show(this, "La fecha de vencimiento no puede ser anterior a la fecha de recepción.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _dueAt.Focus();
            return;
        }
        DateTimeOffset? dueAt = paymentMethod == PaymentMethod.Credit
            ? new DateTimeOffset(_dueAt.Value.Date, TimeZoneInfo.Local.GetUtcOffset(_dueAt.Value.Date))
            : null;
        Request = new ReceivePurchaseRequest(
            supplier.Id, _reference.Text.Trim(), new DateTimeOffset(_receivedAt.Value), lines,
            paymentMethod, DueAt: dueAt);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static NumericUpDown CreateAmountControl(int decimalPlaces, decimal value) => new()
    {
        Dock = DockStyle.Fill,
        DecimalPlaces = decimalPlaces,
        Minimum = 0,
        Maximum = 999999999,
        Increment = decimalPlaces == 6 ? 0.1m : 0.01m,
        Value = Math.Clamp(value, 0, 999999999)
    };

    private sealed record PaymentChoice(string Name, PaymentMethod Method);
}

internal sealed class CashRegisterHistoryWindow : DataModuleWindow
{
    private readonly DateTimePicker _from = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy HH:mm",
        Value = DateTime.Today.AddDays(-30),
        Dock = DockStyle.Fill
    };
    private readonly DateTimePicker _to = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy HH:mm",
        Value = DateTime.Now,
        Dock = DockStyle.Fill
    };
    private readonly TextBox _cashier = new()
    {
        MaxLength = 64,
        PlaceholderText = "Todos",
        Dock = DockStyle.Fill
    };
    private readonly TextBox _terminal = new()
    {
        MaxLength = 80,
        PlaceholderText = "Todas",
        Dock = DockStyle.Fill
    };

    public CashRegisterHistoryWindow(DbContextOptions<TiendaDbContext> options)
        : base(options, "Cierres de caja · nube")
    {
        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = ModernTheme.Canvas
        };
        filters.Controls.Add(CreateFilter("Desde", _from, 200));
        filters.Controls.Add(CreateFilter("Hasta", _to, 200));
        filters.Controls.Add(CreateFilter("Cajero", _cashier, 150));
        filters.Controls.Add(CreateFilter("Terminal", _terminal, 170));
        ModernTheme.StyleInput(_cashier);
        ModernTheme.StyleInput(_terminal);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ModernTheme.Canvas
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ReplaceGridHost(content);
        content.Controls.Add(filters, 0, 0);
        content.Controls.Add(Grid, 0, 1);
    }

    protected override async Task LoadDataAsync()
    {
        Grid.Columns.Clear();
        Grid.Rows.Clear();
        if (_from.Value >= _to.Value)
        {
            throw new InvalidOperationException("La fecha Desde debe ser anterior a la fecha Hasta.");
        }

        try
        {
            using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync();
            var query = new List<string>
            {
                $"from={Uri.EscapeDataString(new DateTimeOffset(_from.Value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}",
                $"to={Uri.EscapeDataString(new DateTimeOffset(_to.Value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}"
            };
            if (!string.IsNullOrWhiteSpace(_cashier.Text))
            {
                query.Add($"cashier={Uri.EscapeDataString(_cashier.Text.Trim())}");
            }
            if (!string.IsNullOrWhiteSpace(_terminal.Text))
            {
                query.Add($"terminal={Uri.EscapeDataString(_terminal.Text.Trim())}");
            }

            using var response = await client.GetAsync(
                $"/api/admin/cash-register/closures?{string.Join("&", query)}");
            if (!response.IsSuccessStatusCode)
            {
                var details = await response.Content.ReadAsStringAsync();
                var accessMessage = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    ? "La cuenta cloud configurada no tiene rol Administrador."
                    : $"La API cloud devolvió HTTP {(int)response.StatusCode}.";
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(details) ? accessMessage : $"{accessMessage} {details}");
            }

            var result = await response.Content.ReadFromJsonAsync<CashRegisterClosureListResponse>()
                ?? throw new InvalidOperationException("La API cloud devolvió un historial vacío o inválido.");
            AddColumns("Terminal", "Apertura", "Cierre", "Abrió", "Cajero", "Fondo inicial (Bs)",
                "Esperado (Bs)", "Contado (Bs)", "Diferencia (Bs)");
            foreach (var session in result.Sessions)
            {
                Grid.Rows.Add(
                    session.TerminalId,
                    session.OpenedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    session.ClosedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    session.OpenedBy,
                    session.ClosedBy,
                    PosFormatting.Money(session.OpeningCash),
                    PosFormatting.Money(session.ExpectedCash),
                    PosFormatting.Money(session.CountedCash),
                    session.Variance.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture));
            }

            Status.Text = $"Fuente: nube · cierres recibidos: {result.Sessions.Count}"
                + (result.HasMore ? " · Hay más de 1000; acota el intervalo para ver los restantes." : string.Empty);
        }
        catch (HttpRequestException exception)
        {
            Status.Text = $"No se pudo consultar la nube: {exception.Message}";
        }
        catch (TaskCanceledException exception)
        {
            Status.Text = $"Se agotó el tiempo de espera al consultar la nube: {exception.Message}";
        }
        catch (System.Text.Json.JsonException exception)
        {
            Status.Text = $"La nube devolvió un formato de historial inválido: {exception.Message}";
        }
    }

    private static Control CreateFilter(string label, Control input, int width)
    {
        var group = new TableLayoutPanel
        {
            Width = width,
            Height = 50,
            Margin = new Padding(2, 0, 6, 0),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ModernTheme.Canvas
        };
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        group.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.Ink
        }, 0, 0);
        group.Controls.Add(input, 0, 1);
        return group;
    }
}

internal sealed class ReportWindow : DataModuleWindow
{
    private readonly TabControl _reportTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _chartsTab = new("Gráficos") { BackColor = ModernTheme.Canvas };
    private readonly TabPage _detailsTab = new("Detalle") { BackColor = ModernTheme.Canvas };
    private readonly Panel _chartsHost = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        Padding = new Padding(8),
        BackColor = ModernTheme.Canvas
    };
    private readonly TableLayoutPanel _chartsLayout = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2,
        Dock = DockStyle.Top,
        BackColor = ModernTheme.Canvas
    };
    private readonly Label _chartsHiddenMessage = new()
    {
        Text = "Los gráficos están ocultos. Activa “Ver gráficos” para mostrarlos.",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = ModernTheme.Muted,
        Visible = false
    };
    private readonly CheckBox _showCharts = new()
    {
        Text = "Ver gráficos",
        Checked = true,
        AutoSize = true,
        ForeColor = ModernTheme.Ink,
        Margin = new Padding(12, 9, 4, 4)
    };
    private readonly Button _downloadExcel = new()
    {
        Text = "Descargar Excel",
        Width = 145,
        Height = 36,
        Enabled = false
    };
    private readonly List<ReportChartControl> _chartControls = [];
    private readonly FlowLayoutPanel _periodButtons = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = false,
        Padding = new Padding(0, 4, 0, 2)
    };
    private readonly FlowLayoutPanel _dateSelectors = new()
    {
        Dock = DockStyle.Fill,
        WrapContents = false,
        Padding = new Padding(0, 2, 0, 2)
    };
    private readonly TableLayoutPanel _summaryCards = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 4,
        RowCount = 1,
        BackColor = ModernTheme.Canvas,
        Padding = new Padding(0, 4, 0, 8)
    };
    private readonly DateTimePicker _from = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy HH:mm",
        Width = 145,
        Value = DateTime.Today
    };
    private readonly DateTimePicker _to = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd/MM/yyyy HH:mm",
        Width = 145,
        Value = DateTime.Now
    };
    private readonly Button _customPeriod = new()
    {
        Text = "Personalizado",
        Width = 150,
        Height = 42
    };
    private readonly Button _generateCustom = new()
    {
        Text = "Generar",
        Width = 100,
        Height = 36,
        Visible = false
    };
    private readonly Button _downloadPdf = new()
    {
        Text = "Descargar PDF",
        Width = 140,
        Height = 36,
        Enabled = false
    };
    private ReportSummaryResponse? _currentReport;
    private ReportVisualData? _currentVisualData;
    private bool _loadingChartsPreference;

    protected override Task LoadDataAsync() => LoadReportAsync();

    public ReportWindow(DbContextOptions<TiendaDbContext> options) : base(options, "Reportes financieros")
    {
        for (var column = 0; column < 4; column++)
        {
            _summaryCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        _summaryCards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var root = Controls.OfType<TableLayoutPanel>().Single();
        var header = (FlowLayoutPanel)root.GetControlFromPosition(0, 0)!;
        header.Controls.Add(_showCharts);
        _from.Enabled = false;
        _to.Enabled = false;
        AddPeriodButton("Hoy", () =>
        {
            var now = DateTime.Now;
            return (now.Date, now);
        });
        AddPeriodButton("Esta Semana", () =>
        {
            var now = DateTime.Now;
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            return (monday, now);
        });
        AddPeriodButton("Este Mes", () =>
        {
            var now = DateTime.Now;
            return (new DateTime(now.Year, now.Month, 1), now);
        });
        AddPeriodButton("Este Año", () =>
        {
            var now = DateTime.Now;
            return (new DateTime(now.Year, 1, 1), now);
        });
        ModernTheme.StyleButton(_customPeriod, false);
        _customPeriod.Click += (_, _) => SetCustomMode();
        _periodButtons.Controls.Add(_customPeriod);

        _dateSelectors.Controls.Add(new Label
        {
            Text = "Desde",
            AutoSize = true,
            ForeColor = ModernTheme.Ink,
            Padding = new Padding(6, 10, 0, 0)
        });
        _dateSelectors.Controls.Add(_from);
        _dateSelectors.Controls.Add(new Label
        {
            Text = "Hasta",
            AutoSize = true,
            ForeColor = ModernTheme.Ink,
            Padding = new Padding(6, 10, 0, 0)
        });
        _dateSelectors.Controls.Add(_to);
        ModernTheme.StyleButton(_generateCustom, true);
        _generateCustom.Click += async (_, _) => await LoadReportSafelyAsync();
        _dateSelectors.Controls.Add(_generateCustom);
        StyleButton(_downloadPdf);
        _downloadPdf.Click += async (_, _) => await DownloadPdfAsync();
        _dateSelectors.Controls.Add(_downloadPdf);
        StyleButton(_downloadExcel);
        _downloadExcel.Click += async (_, _) => await DownloadExcelAsync();
        _dateSelectors.Controls.Add(_downloadExcel);
        _showCharts.CheckedChanged += async (_, _) =>
        {
            UpdateChartVisibility();
            if (!_loadingChartsPreference && IsHandleCreated)
            {
                await SaveChartVisibilityAsync();
            }
        };

        root.RowCount = 5;
        root.RowStyles.Clear();
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.SetRow(Grid, 4);
        root.Controls.Add(_periodButtons, 0, 1);
        root.Controls.Add(_dateSelectors, 0, 2);
        root.SetRow(_summaryCards, 3);
        root.Controls.Add(_summaryCards, 0, 3);
        _detailsTab.Controls.Add(Grid);
        _chartsHost.Controls.Add(_chartsHiddenMessage);
        _chartsHost.Controls.Add(_chartsLayout);
        _chartsTab.Controls.Add(_chartsHost);
        _reportTabs.TabPages.Add(_chartsTab);
        _reportTabs.TabPages.Add(_detailsTab);
        root.Controls.Add(_reportTabs, 0, 4);
    }

    private void AddPeriodButton(string title, Func<(DateTime Start, DateTime End)> range)
    {
        var button = new Button
        {
            Text = title,
            Width = 132,
            Height = 42
        };
        ModernTheme.StyleButton(button, title == "Hoy");
        button.Click += async (_, _) =>
        {
            var (start, end) = range();
            _from.Value = start;
            _to.Value = end;
            _from.Enabled = false;
            _to.Enabled = false;
            _generateCustom.Visible = false;
            ModernTheme.StyleButton(_customPeriod, false);
            await LoadReportSafelyAsync();
        };
        _periodButtons.Controls.Add(button);
    }

    private void SetCustomMode()
    {
        _from.Enabled = true;
        _to.Enabled = true;
        _generateCustom.Visible = true;
        ModernTheme.StyleButton(_customPeriod, true);
    }

    private async Task LoadReportSafelyAsync()
    {
        try
        {
            if (_from.Value >= _to.Value)
            {
                Status.Text = "La fecha Desde debe ser anterior a la fecha Hasta.";
                return;
            }
            await LoadReportAsync();
            Status.Text = $"Reporte actualizado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"Error de base de datos: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite: {exception.Message}";
        }
    }

    private async Task LoadReportAsync()
    {
        var from = new DateTimeOffset(_from.Value).ToUniversalTime();
        var to = new DateTimeOffset(_to.Value).ToUniversalTime();
        await using var db = new TiendaDbContext(Options);
        var report = await new CommerceService(db).GetReportAsync(from, to, CancellationToken.None);
        var visualData = await ReportVisualDataService.LoadAsync(db, from, to, CancellationToken.None);
        var chartsVisible = await ReportChartsPreferenceStore.LoadAsync(db, CancellationToken.None);
        _loadingChartsPreference = true;
        _showCharts.Checked = chartsVisible;
        _loadingChartsPreference = false;
        _currentReport = report;
        _currentVisualData = visualData;
        _downloadPdf.Enabled = true;
        _downloadExcel.Enabled = true;
        BuildCharts(visualData);
        _summaryCards.Controls.Clear();
        AddSummaryCard("INGRESOS TOTALES", PosFormatting.Money(report.Revenue), ModernTheme.Blue);
        AddSummaryCard("COSTO DE VENTAS", PosFormatting.Money(report.CostOfGoods), ModernTheme.OrangeDark);
        AddSummaryCard("GASTOS OPERATIVOS", PosFormatting.Money(report.Expenses), ModernTheme.Red);
        AddSummaryCard(
            "UTILIDAD NETA",
            report.NetProfit is decimal netProfit ? PosFormatting.Money(netProfit) : "N/D · faltan costos",
            report.NetProfit is null ? ModernTheme.Muted : ModernTheme.Success);
        AddColumns("Indicador", "Importe Bs");
        Grid.Rows.Add("Ventas completadas", report.CompletedSales);
        Grid.Rows.Add("Ingresos por ventas", PosFormatting.Money(report.Revenue));
        Grid.Rows.Add("Costo de ventas (insumos consumidos)", PosFormatting.Money(report.CostOfGoods));
        Grid.Rows.Add("Ganancia bruta", report.GrossProfit is decimal gross ? PosFormatting.Money(gross) : "N/D: faltan costos");
        Grid.Rows.Add("Gastos operativos", PosFormatting.Money(report.Expenses));
        Grid.Rows.Add("Utilidad neta", report.NetProfit is decimal net ? PosFormatting.Money(net) : "N/D: faltan costos");
        Grid.Rows.Add("Inversión en compras recibidas", PosFormatting.Money(report.PurchaseInvestment));
        Grid.Rows.Add("Ventas con costos incompletos", report.SalesWithoutCostSnapshot);
        Grid.Rows.Add("Inventario restante (no se resta de utilidad)", "Se valora desde Inventario");
        Grid.Rows.Add("—", "—");
        Grid.Rows.Add("COMBOS PROMOCIONALES", "VENTAS / IMPACTO");
        var bundleReports = await new PromotionBundleService(db).GetSalesReportAsync(
            from, to, CancellationToken.None);
        if (bundleReports.Count == 0)
        {
            Grid.Rows.Add("Combos promocionales", "Sin ventas en el período");
        }
        else
        {
            foreach (var bundle in bundleReports)
            {
                Grid.Rows.Add(
                    $"{bundle.Name} · {bundle.TimesSold} vendidos",
                    $"Ingresos {PosFormatting.Money(bundle.Revenue)} Bs · descuento {PosFormatting.Money(bundle.DiscountGiven)} Bs");
            }
        }
        if (report.SalesWithoutCostSnapshot > 0)
        {
            Status.Text = $"Utilidad neta no disponible: {report.SalesWithoutCostSnapshot} venta(s) con costo incompleto.";
        }
        if (visualData.From.Date > from.ToLocalTime().Date)
        {
            var rangeNotice = "Los gráficos muestran como máximo los últimos 90 días del período.";
            Status.Text = string.IsNullOrWhiteSpace(Status.Text)
                ? rangeNotice
                : $"{Status.Text} {rangeNotice}";
        }
    }

    private async Task SaveChartVisibilityAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(Options);
            await ReportChartsPreferenceStore.SaveAsync(db, _showCharts.Checked, CancellationToken.None);
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo guardar la preferencia de gráficos: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al guardar la preferencia: {exception.Message}";
        }
    }

    private void BuildCharts(ReportVisualData data)
    {
        _chartsLayout.SuspendLayout();
        foreach (Control old in _chartsLayout.Controls)
        {
            old.Dispose();
        }
        _chartsLayout.Controls.Clear();
        _chartControls.Clear();
        _chartsLayout.ColumnStyles.Clear();
        _chartsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _chartsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddChart(new ReportChartControl("Ventas por día", ReportChartKind.Line, data.DailySales,
            accent: Color.FromArgb(139, 167, 255)));
        var comparison = new ReportChartControl("Ingresos vs gastos", ReportChartKind.Columns);
        comparison.SetComparison(data.Revenue, data.Expenses);
        AddChart(comparison);
        AddChart(new ReportChartControl("10 productos más vendidos", ReportChartKind.HorizontalBars,
            products: data.BestSellers, accent: Color.FromArgb(76, 210, 160)));
        AddChart(new ReportChartControl("10 productos menos vendidos", ReportChartKind.HorizontalBars,
            products: data.SlowestSellers, accent: Color.FromArgb(246, 178, 88)));
        AddChart(new ReportChartControl("Ventas por hora · 08:00–23:00", ReportChartKind.Line,
            data.HourlySales, accent: Color.FromArgb(183, 151, 255)));
        AddChart(new ReportChartControl("Métodos de pago", ReportChartKind.Doughnut,
            payments: data.PaymentMethods));
        AddChart(new ReportChartControl("Utilidad neta por día", ReportChartKind.Line,
            data.DailyNetProfit, accent: Color.FromArgb(76, 210, 160)));
        _chartsLayout.ResumeLayout();
        UpdateChartVisibility();

        void AddChart(ReportChartControl chart)
        {
            chart.Dock = DockStyle.Fill;
            chart.Margin = new Padding(7);
            _chartControls.Add(chart);
            _chartsLayout.Controls.Add(chart, (_chartControls.Count - 1) % 2, (_chartControls.Count - 1) / 2);
        }
    }

    private void UpdateChartVisibility()
    {
        _chartsLayout.Visible = _showCharts.Checked;
        _chartsHiddenMessage.Visible = !_showCharts.Checked;
        if (!_showCharts.Checked && _reportTabs.SelectedTab == _chartsTab)
        {
            _reportTabs.SelectedTab = _detailsTab;
        }
    }

    private async Task DownloadExcelAsync()
    {
        if (_currentReport is not { } report || _currentVisualData is not { } data)
        {
            Status.Text = "Genera un reporte antes de descargarlo.";
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "Descargar reporte con gráficos",
            Filter = "Libro de Excel (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = $"tienda-miriam-reporte-{report.From.ToLocalTime():yyyyMMdd-HHmm}.xlsx"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, ReportExcelExporter.Create(report, data));
            Status.Text = $"Excel guardado: {dialog.FileName}";
        }
        catch (IOException exception)
        {
            Status.Text = $"No se pudo guardar el Excel: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            Status.Text = $"No hay permiso para guardar el Excel: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            Status.Text = $"No se pudo generar el libro Excel: {exception.Message}";
        }
    }

    private async Task DownloadPdfAsync()
    {
        if (_currentReport is not { } report)
        {
            Status.Text = "Genera un reporte antes de descargarlo.";
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Descargar reporte financiero",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = $"tienda-miriam-reporte-{report.From.ToLocalTime():yyyyMMdd-HHmm}.pdf"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            byte[]? logo;
            await using (var db = new TiendaDbContext(Options))
            {
                logo = await db.BusinessAssets.AsNoTracking()
                    .Where(asset => asset.Key == DemoDataSeeder.LogoAssetKey)
                    .Select(asset => asset.Data)
                    .SingleOrDefaultAsync();
            }

            var chartImages = _chartControls.Select(chart => chart.CapturePng()).ToArray();
            var chartsLimitedTo90Days = _currentVisualData is { } visualData
                && visualData.From.Date > report.From.ToLocalTime().Date;
            var pdf = FinancialReportPdf.Create(report, logo, chartImages, chartsLimitedTo90Days);
            await File.WriteAllBytesAsync(dialog.FileName, pdf);
            Status.Text = $"PDF guardado: {dialog.FileName}";
        }
        catch (IOException exception)
        {
            Status.Text = $"No se pudo guardar el PDF: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            Status.Text = $"No hay permiso para guardar el PDF en esa ubicación: {exception.Message}";
        }
        catch (ArgumentException exception)
        {
            Status.Text = $"El logotipo guardado no es compatible con el PDF: {exception.Message}";
        }
        catch (OutOfMemoryException)
        {
            Status.Text = "No se pudo procesar el logotipo para el PDF; revisa que la imagen sea válida.";
        }
        catch (DbUpdateException exception)
        {
            Status.Text = $"No se pudo leer el logotipo para el PDF: {exception.GetBaseException().Message}";
        }
        catch (SqliteException exception)
        {
            Status.Text = $"Error SQLite al preparar el PDF: {exception.Message}";
        }
    }

    private void AddSummaryCard(string title, string value, Color accent)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(12, 9, 12, 8),
            BackColor = ModernTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = ModernTheme.Muted,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var valueLabel = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = accent,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        card.Controls.Add(valueLabel);
        card.Controls.Add(titleLabel);
        _summaryCards.Controls.Add(card, _summaryCards.Controls.Count, 0);
    }
}
