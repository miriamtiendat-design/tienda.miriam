using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed class LocalLoginDialog : Form
{
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly TextBox _username = new() { Dock = DockStyle.Fill, MaxLength = 64 };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, MaxLength = 256, UseSystemPasswordChar = true };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = ModernTheme.Muted,
        Text = "Acceso local: funciona sin conexión a internet."
    };

    private LocalLoginDialog(DbContextOptions<TiendaDbContext> options)
    {
        WindowFadeAnimator.Attach(this);
        _options = options;
        Text = "Acceso · Tienda Miriam";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(460, 270);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 2,
            RowCount = 5,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var heading = new Label
        {
            Text = "INICIAR SESIÓN",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = ModernTheme.OrangeDark,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        AddField(layout, "Usuario", _username, 1);
        AddField(layout, "Contraseña", _password, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.SetColumnSpan(_status, 2);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var login = new Button { Text = "ENTRAR", Width = 120, Height = 40 };
        ModernTheme.StyleButton(login, true);
        login.Click += async (_, _) => await LoginAsync();
        actions.Controls.Add(login);
        layout.Controls.Add(actions, 0, 4);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = login;
        CancelButton = new Button { DialogResult = DialogResult.Cancel };
    }

    public LocalUserIdentity? User { get; private set; }

    public static async Task<LocalUserIdentity?> AuthenticateAsync(
        DbContextOptions<TiendaDbContext> options,
        IWin32Window? owner = null)
    {
        await using var db = new TiendaDbContext(options);
        if (!await db.Users.AnyAsync())
        {
            using var setup = new InitialAdministratorDialog();
            if (setup.ShowDialog(owner) != DialogResult.OK)
            {
                return null;
            }

            await using var createDb = new TiendaDbContext(options);
            try
            {
                await new LocalAuthenticationService(createDb).EnsureInitialAdministratorAsync(
                    setup.Username, setup.Password);
            }
            catch (InvalidOperationException exception)
            {
                MessageBox.Show(owner, exception.Message, "Configuración inicial",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        using var login = new LocalLoginDialog(options);
        return login.ShowDialog(owner) == DialogResult.OK ? login.User : null;
    }

    private async Task LoginAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            var user = await new LocalAuthenticationService(db).AuthenticateAsync(_username.Text, _password.Text);
            if (user is null)
            {
                _password.Clear();
                _status.Text = "Credenciales inválidas, usuario inactivo o cuenta temporalmente bloqueada.";
                _status.ForeColor = ModernTheme.Danger;
                return;
            }

            User = user;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo actualizar el estado de acceso: {exception.GetBaseException().Message}";
            _status.ForeColor = ModernTheme.Danger;
        }
    }

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        input.BackColor = ModernTheme.Surface;
        input.ForeColor = ModernTheme.Ink;
        layout.Controls.Add(input, 1, row);
    }
}

internal sealed class InitialAdministratorDialog : Form
{
    private readonly TextBox _username = new() { Dock = DockStyle.Fill, Text = "admin", MaxLength = 64 };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256 };
    private readonly TextBox _confirm = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256 };

    public InitialAdministratorDialog()
    {
        Text = "Configuración inicial · Administrador local";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(500, 300);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Label
        {
            Text = "CREAR ADMINISTRADOR",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = ModernTheme.OrangeDark,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        AddField(layout, "Usuario", _username, 1);
        AddField(layout, "Contraseña (10+)", _password, 2);
        AddField(layout, "Repetir contraseña", _confirm, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var create = new Button { Text = "CREAR Y CONTINUAR", Width = 175, Height = 42 };
        ModernTheme.StyleButton(create, true);
        create.Click += (_, _) =>
        {
            if (_password.Text != _confirm.Text)
            {
                MessageBox.Show(this, "Las contraseñas no coinciden.", "Configuración inicial",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_password.Text) || _password.Text.Length < 10)
            {
                MessageBox.Show(this, "Usa una contraseña de al menos 10 caracteres.", "Configuración inicial",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        actions.Controls.Add(create);
        layout.Controls.Add(actions, 0, 4);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = create;
    }

    public string Username => _username.Text;
    public string Password => _password.Text;

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        input.BackColor = ModernTheme.Surface;
        input.ForeColor = ModernTheme.Ink;
        layout.Controls.Add(input, 1, row);
    }
}

internal sealed class LocalUserManagementDialog : Form
{
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = ModernTheme.Surface,
        ForeColor = ModernTheme.Ink
    };

    public LocalUserManagementDialog(DbContextOptions<TiendaDbContext> options)
    {
        WindowFadeAnimator.Attach(this);
        _options = options;
        Text = "Usuarios locales y roles";
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(700, 450);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(LocalUserIdentity.Username), HeaderText = "Usuario" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(LocalUserIdentity.Role), HeaderText = "Rol" });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(LocalUserIdentity.IsActive), HeaderText = "Activo" });
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 2, BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        AddAction(actions, "Nuevo usuario", CreateUserAsync);
        AddAction(actions, "Cambiar contraseña", ResetPasswordAsync);
        AddAction(actions, "Activar / Desactivar", ToggleUserAsync);
        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        Controls.Add(root);
        Shown += async (_, _) => await RefreshAsync();
    }

    private void AddAction(FlowLayoutPanel panel, string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, Height = 42 };
        ModernTheme.StyleButton(button, title == "Nuevo usuario");
        button.Click += async (_, _) => await action();
        panel.Controls.Add(button);
    }

    private LocalUserIdentity? Selected => _grid.CurrentRow?.DataBoundItem as LocalUserIdentity;

    private async Task RefreshAsync()
    {
        await using var db = new TiendaDbContext(_options);
        _grid.DataSource = await new LocalAuthenticationService(db).GetUsersAsync();
    }

    private async Task CreateUserAsync()
    {
        using var dialog = new CreateLocalUserDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new LocalAuthenticationService(db).CreateUserAsync(dialog.Username, dialog.Password, dialog.Role);
            await RefreshAsync();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Usuarios locales", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ResetPasswordAsync()
    {
        if (Selected is not { } selected) return;
        using var dialog = new PasswordResetDialog(selected.Username);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new LocalAuthenticationService(db).ResetPasswordAsync(selected.Id, dialog.Password);
            MessageBox.Show(this, "Contraseña local actualizada.", "Usuarios locales", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Usuarios locales", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ToggleUserAsync()
    {
        if (Selected is not { } selected) return;
        var active = !selected.IsActive;
        if (MessageBox.Show(this, $"{(active ? "Activar" : "Desactivar")} a {selected.Username}?",
                "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new LocalAuthenticationService(db).SetUserActiveAsync(selected.Id, active);
            await RefreshAsync();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Usuarios locales", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

internal sealed class CreateLocalUserDialog : Form
{
    private readonly TextBox _username = new() { Dock = DockStyle.Fill, MaxLength = 64 };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256 };
    private readonly ComboBox _role = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    public CreateLocalUserDialog()
    {
        Text = "Crear usuario local";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 240);
        BackColor = ModernTheme.Canvas;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddField(layout, "Usuario", _username, 0);
        AddField(layout, "Contraseña (10+)", _password, 1);
        _role.DataSource = new[] { AppRole.Cashier, AppRole.Administrator };
        AddField(layout, "Rol", _role, 2);
        var save = new Button { Text = "CREAR", Width = 110, Height = 40, DialogResult = DialogResult.OK };
        ModernTheme.StyleButton(save, true);
        layout.Controls.Add(save, 1, 3);
        Controls.Add(layout);
        AcceptButton = save;
    }

    public string Username => _username.Text;
    public string Password => _password.Text;
    public AppRole Role => (AppRole)_role.SelectedItem!;

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        layout.Controls.Add(input, 1, row);
    }
}

internal sealed class PasswordResetDialog : Form
{
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256 };

    public PasswordResetDialog(string username)
    {
        Text = $"Cambiar contraseña · {username}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 130);
        BackColor = ModernTheme.Canvas;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Nueva contraseña", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(_password, 1, 0);
        var save = new Button { Text = "ACTUALIZAR", Width = 130, Height = 40, DialogResult = DialogResult.OK };
        ModernTheme.StyleButton(save, true);
        layout.Controls.Add(save, 1, 1);
        Controls.Add(layout);
        AcceptButton = save;
    }

    public string Password => _password.Text;
}

internal sealed class CashSessionDialog : Form
{
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly string _terminalId;
    private readonly LocalUserIdentity _user;
    private readonly Label _summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 12) };
    private readonly NumericUpDown _counted = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Maximum = 1_000_000m, ThousandsSeparator = true };
    private readonly TextBox _note = new() { Dock = DockStyle.Fill, MaxLength = 300, PlaceholderText = "Observación si existe diferencia" };
    private readonly DataGridView _history = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = ModernTheme.Surface,
        ForeColor = ModernTheme.Ink
    };
    private CashSessionSummary? _active;

    public CashSessionDialog(DbContextOptions<TiendaDbContext> options, string terminalId, LocalUserIdentity user)
    {
        WindowFadeAnimator.Attach(this);
        _options = options;
        _terminalId = terminalId;
        _user = user;
        Text = "Arqueo y cierre de caja";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(860, 600);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "OpenedAt", HeaderText = "Apertura" });
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ClosedAt", HeaderText = "Cierre" });
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "OpenedBy", HeaderText = "Abrió" });
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ClosedBy", HeaderText = "Cerró" });
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Expected", HeaderText = "Esperado Bs" });
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Counted", HeaderText = "Contado Bs" });
        _history.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Variance", HeaderText = "Diferencia Bs" });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(_summary, 0, 0);
        layout.SetColumnSpan(_summary, 2);
        AddField(layout, "Efectivo contado", _counted, 1);
        AddField(layout, "Nota de cierre", _note, 2);
        var historyLabel = new Label
        {
            Text = "ÚLTIMOS ARQUEOS DE ESTA CAJA",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = ModernTheme.OrangeDark
        };
        layout.Controls.Add(historyLabel, 0, 3);
        layout.SetColumnSpan(historyLabel, 2);
        layout.Controls.Add(_history, 0, 4);
        layout.SetColumnSpan(_history, 2);
        var close = new Button { Text = "CERRAR TURNO", Width = 150, Height = 42 };
        ModernTheme.StyleButton(close, true);
        close.Click += async (_, _) => await CloseSessionAsync();
        var open = new Button { Text = "ABRIR CAJA", Width = 130, Height = 42 };
        ModernTheme.StyleButton(open, false);
        open.Click += async (_, _) => await OpenSessionAsync();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(close);
        actions.Controls.Add(open);
        layout.Controls.Add(actions, 0, 5);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        Shown += async (_, _) => await RefreshSummaryAsync();
    }

    private async Task RefreshSummaryAsync()
    {
        await using var db = new TiendaDbContext(_options);
        var service = new CashRegisterService(db);
        _active = await service.GetOpenSummaryAsync(_terminalId);
        var history = await service.GetRecentSessionsAsync(_terminalId);
        _history.DataSource = history.Select(item => new
        {
            OpenedAt = item.OpenedAt.ToLocalTime().ToString("dd/MM HH:mm"),
            ClosedAt = item.ClosedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "ABIERTA",
            item.OpenedBy,
            ClosedBy = item.ClosedBy ?? "—",
            Expected = item.ExpectedCash.ToString("0.00"),
            Counted = item.CountedCash?.ToString("0.00") ?? "—",
            Variance = item.Variance?.ToString("+0.00;-0.00;0.00") ?? "—"
        }).ToList();
        if (_active is null)
        {
            _summary.Text = "No hay una sesión de caja abierta. Abre una sesión antes de registrar ventas.";
            _counted.Enabled = false;
            _note.Enabled = false;
            return;
        }

        _summary.Text = $"CAJA {_active.TerminalId} · Abierta por {_active.OpenedBy} · {_active.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm}\n"
            + $"Fondo inicial: {_active.OpeningCash:0.00} Bs\n"
            + $"Efectivo recibido: {_active.CashReceived:0.00} Bs · Vuelto: {_active.ChangePaid:0.00} Bs · QR: {_active.QrReceived:0.00} Bs\n"
            + $"Monedero aplicado: {_active.WalletApplied:0.00} Bs · Fiado emitido: {_active.CreditIssued:0.00} Bs · Otros: {_active.OtherPayments:0.00} Bs\n"
            + $"Ingresos externos en efectivo: monedero {_active.WalletTopUpsCash:0.00} Bs · abonos fiado {_active.CreditPaymentsCash:0.00} Bs\n"
            + $"Gastos: efectivo -{_active.CashExpenses:0.00} Bs · QR {_active.QrExpenses:0.00} Bs · ingresos QR: monedero {_active.WalletTopUpsQr:0.00} Bs / fiado {_active.CreditPaymentsQr:0.00} Bs\n"
            + $"EFECTIVO ESPERADO: {_active.ExpectedCash:0.00} Bs";
        _counted.Value = Math.Min(_active.ExpectedCash, _counted.Maximum);
        _counted.Enabled = true;
        _note.Enabled = true;
    }

    private async Task CloseSessionAsync()
    {
        if (_active is null) return;
        var variance = decimal.Round(_counted.Value - _active.ExpectedCash, 2);
        if (variance != 0 && string.IsNullOrWhiteSpace(_note.Text))
        {
            MessageBox.Show(this, "Registra una observación obligatoria cuando el contado difiere de lo esperado.",
                "Arqueo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            $"¿Cerrar esta caja?\nEsperado: {_active.ExpectedCash:0.00} Bs\nContado: {_counted.Value:0.00} Bs\nDiferencia: {variance:+0.00;-0.00;0.00} Bs",
            "Confirmar cierre", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;
        try
        {
            await using var db = new TiendaDbContext(_options);
            var closed = await new CashRegisterService(db).CloseAsync(
                _active.Id, _terminalId, _user.Username, _counted.Value, _note.Text);
            var cloudMessage = "Sincronización cloud pendiente; se reintentará automáticamente cuando vuelva la conexión.";
            try
            {
                using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync();
                await using var syncDb = new TiendaDbContext(_options);
                await CloudCashRegisterSync.SyncPendingAsync(client, syncDb);
                var stillPending = await syncDb.CashRegisterSessions.AnyAsync(
                    session => session.Id == closed.Id && session.SyncPending);
                cloudMessage = stillPending
                    ? "Cierre guardado localmente; queda pendiente de sincronizar con la nube."
                    : "Cierre sincronizado con la nube.";
            }
            catch (HttpRequestException exception)
            {
                cloudMessage = $"Cierre guardado localmente; nube no disponible: {exception.Message}";
            }
            catch (TaskCanceledException exception)
            {
                cloudMessage = $"Cierre guardado localmente; la nube agotó el tiempo de espera: {exception.Message}";
            }
            catch (InvalidOperationException exception)
            {
                cloudMessage = $"Cierre guardado localmente; sincronización pendiente: {exception.Message}";
            }
            catch (System.Text.Json.JsonException exception)
            {
                cloudMessage = $"Cierre guardado localmente; respuesta cloud inválida, queda pendiente: {exception.Message}";
            }
            catch (DbUpdateException exception)
            {
                cloudMessage = $"Cierre guardado localmente; no se pudo actualizar la cola: {exception.GetBaseException().Message}";
            }
            catch (SqliteException exception)
            {
                cloudMessage = $"Cierre guardado localmente; error SQLite en la cola cloud: {exception.Message}";
            }
            MessageBox.Show(this,
                $"Cierre guardado y auditado.\nEsperado: {closed.ExpectedCash:0.00} Bs\nContado: {closed.CountedCash:0.00} Bs\nDiferencia: {closed.Variance:+0.00;-0.00;0.00} Bs\n\n{cloudMessage}",
                "Turno cerrado", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshSummaryAsync();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Arqueo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OpenSessionAsync()
    {
        if (_active is not null) return;
        using var openingDialog = new OpeningCashDialog();
        if (openingDialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            await using var db = new TiendaDbContext(_options);
            await new CashRegisterService(db).OpenAsync(_terminalId, _user.Username, openingDialog.Amount);
            await RefreshSummaryAsync();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Apertura de caja", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        layout.Controls.Add(input, 1, row);
    }
}

internal sealed class OpeningCashDialog : Form
{
    private readonly NumericUpDown _amount = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Maximum = 1_000_000m, ThousandsSeparator = true };

    public OpeningCashDialog()
    {
        Text = "Apertura de caja";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 140);
        BackColor = ModernTheme.Canvas;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Fondo inicial (Bs)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(_amount, 1, 0);
        var open = new Button { Text = "ABRIR CAJA", Width = 130, Height = 40, DialogResult = DialogResult.OK };
        ModernTheme.StyleButton(open, true);
        layout.Controls.Add(open, 1, 1);
        Controls.Add(layout);
        AcceptButton = open;
    }

    public decimal Amount => _amount.Value;
}
