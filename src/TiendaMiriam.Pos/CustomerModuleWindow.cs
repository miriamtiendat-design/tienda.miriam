using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed class CustomerModuleWindow : DataModuleWindow
{
    private sealed record CustomerRow(Guid Id, string Name, string? Phone, string? TaxId, decimal CreditBalance);
    private sealed record ReceivableRow(
        Guid Id, string Name, string? Phone, decimal CreditBalance, DateTime? LastCreditAtUtc, int DaysSinceLastCredit);
    private sealed record LedgerRow(
        Guid Id, CustomerLedgerKind Kind, decimal WalletDelta, decimal CreditDelta,
        PaymentMethod PaymentMethod, DateTime OccurredAtUtc, string Actor, string Description);

    private readonly LocalUserIdentity _user;
    private readonly string _terminalId;
    private readonly bool _isAdministrator;
    private readonly Button _customerView = new() { Text = "Clientes", AutoSize = true, Height = 36 };
    private readonly Button _receivableView = new() { Text = "Cuentas por cobrar", AutoSize = true, Height = 36 };
    private readonly Button _createCustomer = new() { Text = "Nuevo cliente", AutoSize = true, Height = 36 };
    private readonly Button _editCustomer = new() { Text = "Editar ficha", AutoSize = true, Height = 36 };
    private readonly Button _history = new() { Text = "Historial", AutoSize = true, Height = 36 };
    private readonly Button _registerPayment = new() { Text = "Registrar abono", AutoSize = true, Height = 36 };
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 15_000 };
    private bool _showReceivables;
    private bool _syncing;

    public CustomerModuleWindow(
        DbContextOptions<TiendaDbContext> options,
        LocalUserIdentity user)
        : base(options, "Clientes y cuentas por cobrar")
    {
        _user = user;
        _terminalId = Environment.GetEnvironmentVariable("TIENDA_TERMINAL_ID") ?? Environment.MachineName;
        _isAdministrator = user.Role == AppRole.Administrator;
        foreach (var button in new[]
                 {
                     _customerView, _receivableView, _createCustomer, _editCustomer, _history, _registerPayment
                 })
        {
            StyleCustomerButton(button);
            Header.Controls.Add(button);
        }
        _createCustomer.Visible = _isAdministrator;
        _editCustomer.Visible = _isAdministrator;
        _customerView.Click += async (_, _) => await SetViewAsync(false);
        _receivableView.Click += async (_, _) => await SetViewAsync(true);
        _createCustomer.Click += async (_, _) => await CreateCustomerAsync();
        _editCustomer.Click += async (_, _) => await EditCustomerAsync();
        _history.Click += async (_, _) => await ShowHistoryAsync();
        _registerPayment.Click += async (_, _) => await RegisterPaymentAsync();
        Grid.SelectionChanged += (_, _) => UpdateActionState();
        UpdateActionState();

        _syncTimer.Tick += async (_, _) => await SyncPendingChangesSafelyAsync();
        Shown += (_, _) => _syncTimer.Start();
        FormClosed += (_, _) => _syncTimer.Dispose();
    }

    protected override async Task LoadDataAsync()
    {
        await using var db = new TiendaDbContext(Options);
        Grid.Rows.Clear();
        if (_showReceivables)
        {
            await LoadReceivablesAsync(db);
        }
        else
        {
            await LoadCustomersAsync(db);
        }
        UpdateActionState();
    }

    private async Task LoadCustomersAsync(TiendaDbContext db)
    {
        AddColumns("ID", "Nombre", "Teléfono", "NIT / CI", "Saldo fiado (Bs)");
        Grid.Columns[0].Visible = false;
        var customers = await db.Customers.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .Select(item => new CustomerRow(item.Id, item.Name, item.Phone, item.TaxId, item.CreditBalance))
            .ToListAsync();
        foreach (var customer in customers)
        {
            Grid.Rows.Add(customer.Id, customer.Name, customer.Phone ?? "—", customer.TaxId ?? "—",
                PosFormatting.Money(customer.CreditBalance));
        }
        Status.Text = $"{customers.Count} clientes activos.";
    }

    private async Task LoadReceivablesAsync(TiendaDbContext db)
    {
        AddColumns("ID", "Cliente", "Teléfono", "Debe (Bs)", "Último fiado", "Días de atraso*");
        Grid.Columns[0].Visible = false;
        var customers = await db.Customers.AsNoTracking()
            .Where(item => item.IsActive && item.CreditBalance > 0)
            .OrderByDescending(item => item.CreditBalance)
            .Select(item => new { item.Id, item.Name, item.Phone, item.CreditBalance })
            .ToListAsync();
        var customerIds = customers.Select(customer => customer.Id).ToArray();
        var lastCredits = await db.CustomerLedger.AsNoTracking()
            .Where(entry => entry.Kind == CustomerLedgerKind.CreditSale
                && customerIds.Contains(entry.CustomerId))
            .GroupBy(entry => entry.CustomerId)
            .Select(group => new { CustomerId = group.Key, OccurredAtUtc = group.Max(entry => entry.OccurredAtUtc) })
            .ToDictionaryAsync(item => item.CustomerId, item => item.OccurredAtUtc);
        var localToday = DateTime.Now.Date;
        foreach (var customer in customers)
        {
            DateTime? lastCredit = lastCredits.TryGetValue(customer.Id, out var occurredAt) ? occurredAt : null;
            var daysSince = lastCredit is DateTime timestamp
                ? Math.Max(0, (localToday - DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime().Date).Days)
                : 0;
            Grid.Rows.Add(customer.Id, customer.Name, customer.Phone ?? "—",
                PosFormatting.Money(customer.CreditBalance),
                lastCredit is DateTime creditAt
                    ? new DateTimeOffset(DateTime.SpecifyKind(creditAt, DateTimeKind.Utc), TimeSpan.Zero)
                        .ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                    : "—",
                lastCredit is null ? "—" : daysSince.ToString());
        }
        Status.Text = $"{customers.Count} clientes con saldo pendiente. *Días desde el último fiado; no hay vencimiento individual configurado.";
    }

    private async Task SetViewAsync(bool showReceivables)
    {
        _showReceivables = showReceivables;
        _customerView.BackColor = showReceivables ? ModernTheme.Surface : ModernTheme.Orange;
        _customerView.ForeColor = showReceivables ? ModernTheme.Ink : Color.White;
        _receivableView.BackColor = showReceivables ? ModernTheme.Orange : ModernTheme.Surface;
        _receivableView.ForeColor = showReceivables ? Color.White : ModernTheme.Ink;
        await RefreshModuleAsync();
    }

    private Guid? SelectedCustomerId()
    {
        if (Grid.CurrentRow?.Cells.Count > 0 && Grid.CurrentRow.Cells[0].Value is Guid id)
        {
            return id;
        }
        return null;
    }

    private void UpdateActionState()
    {
        var selected = SelectedCustomerId() is not null;
        _editCustomer.Enabled = _isAdministrator && selected;
        _history.Enabled = selected;
        _registerPayment.Enabled = selected && _showReceivables;
    }

    private async Task CreateCustomerAsync()
    {
        using var dialog = new CustomerEditorDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            await using var db = new TiendaDbContext(Options);
            var customer = await new CommerceService(db).CreateCustomerAsync(
                dialog.Request, CancellationToken.None, enqueueForSync: true);
            var syncError = await TrySyncCustomerChangesAsync();
            await RefreshModuleAsync();
            SelectCustomerRow(customer.Id);
            Status.Text = syncError is null
                ? $"Cliente {customer.Name} guardado y sincronizado."
                : $"Cliente {customer.Name} guardado localmente; sincronización pendiente: {syncError}";
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (DbUpdateException exception)
        {
            ShowError($"No se pudo guardar el cliente: {exception.GetBaseException().Message}");
        }
    }

    private async Task EditCustomerAsync()
    {
        if (!_isAdministrator || SelectedCustomerId() is not Guid customerId)
        {
            return;
        }
        await using var db = new TiendaDbContext(Options);
        var customer = await db.Customers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == customerId && item.IsActive);
        if (customer is null)
        {
            ShowError("El cliente ya no está disponible. Actualiza la lista.");
            return;
        }
        using var dialog = new CustomerEditorDialog(customer);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var updated = await new CommerceService(db).UpdateCustomerAsync(
                customer.Id, dialog.Request, _user.Username, CancellationToken.None, enqueueForSync: true);
            var syncError = await TrySyncCustomerChangesAsync();
            await RefreshModuleAsync();
            SelectCustomerRow(updated.Id);
            Status.Text = syncError is null
                ? $"Ficha de {updated.Name} actualizada y sincronizada."
                : $"Ficha de {updated.Name} guardada localmente; sincronización pendiente: {syncError}";
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task ShowHistoryAsync()
    {
        if (SelectedCustomerId() is not Guid customerId)
        {
            return;
        }
        await using var db = new TiendaDbContext(Options);
        var customer = await db.Customers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == customerId);
        if (customer is null)
        {
            ShowError("No se encontró el cliente seleccionado.");
            return;
        }
        var totalPurchased = await db.CustomerLedger.AsNoTracking()
            .Where(entry => entry.CustomerId == customerId
                && (entry.Kind == CustomerLedgerKind.CreditSale
                    || entry.Kind == CustomerLedgerKind.WalletPurchase))
            .SumAsync(entry => (decimal?)(entry.CreditDelta - entry.WalletDelta)) ?? 0m;
        var totalPaid = await db.CustomerLedger.AsNoTracking()
            .Where(entry => entry.CustomerId == customerId && entry.Kind == CustomerLedgerKind.CreditPayment)
            .SumAsync(entry => (decimal?)-entry.CreditDelta) ?? 0m;
        var movements = await db.CustomerLedger.AsNoTracking()
            .Where(entry => entry.CustomerId == customerId)
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(500)
            .Select(entry => new LedgerRow(
                entry.Id, entry.Kind, entry.WalletDelta, entry.CreditDelta, entry.PaymentMethod,
                entry.OccurredAtUtc, entry.Actor, entry.Description))
            .ToListAsync();
        using var history = new CustomerHistoryDialog(
            customer.Name, totalPurchased, customer.CreditBalance, totalPaid, movements);
        history.ShowDialog(this);
    }

    private async Task RegisterPaymentAsync()
    {
        if (SelectedCustomerId() is not Guid customerId)
        {
            return;
        }
        await using var db = new TiendaDbContext(Options);
        var customer = await db.Customers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == customerId && item.IsActive);
        if (customer is null || customer.CreditBalance <= 0)
        {
            ShowError("El cliente no tiene saldo pendiente. Actualiza la lista.");
            return;
        }
        var session = await db.CashRegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TerminalId == _terminalId && item.ClosedAtUtc == null);
        if (session is null)
        {
            ShowError("Abre la caja antes de registrar un abono, incluso si el pago es por QR.");
            return;
        }
        using var dialog = new CustomerCreditPaymentDialog(customer.Name, customer.CreditBalance, session.TerminalId);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            await new CommerceService(db).RecordCreditPaymentAsync(
                customer.Id,
                dialog.Amount,
                string.IsNullOrWhiteSpace(dialog.Description)
                    ? dialog.PaymentMethod == PaymentMethod.Cash
                        ? "Abono de fiado en efectivo"
                        : "Abono de fiado por QR"
                    : $"{(dialog.PaymentMethod == PaymentMethod.Cash ? "Abono de fiado en efectivo" : "Abono de fiado por QR")}: {dialog.Description}",
                _user.Username,
                dialog.PaymentMethod,
                CancellationToken.None,
                session.Id,
                enqueueForSync: true);
            var syncError = await TrySyncCustomerChangesAsync();
            await SetViewAsync(true);
            Status.Text = syncError is null
                ? $"Abono de {PosFormatting.Money(dialog.Amount)} registrado y sincronizado por "
                    + (dialog.PaymentMethod == PaymentMethod.Cash ? "efectivo" : "QR")
                    + "; se aplicó a la sesión de caja abierta."
                : $"Abono de {PosFormatting.Money(dialog.Amount)} guardado localmente; "
                    + $"sincronización pendiente: {syncError}";
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (DbUpdateException exception)
        {
            ShowError($"No se pudo registrar el abono: {exception.GetBaseException().Message}");
        }
    }

    private async Task<string?> TrySyncCustomerChangesAsync()
    {
        try
        {
            using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync(
                administrator: _isAdministrator);
            await using var db = new TiendaDbContext(Options);
            await CustomerChangeOutboxSync.SyncPendingAsync(
                client, db, CustomerSyncChangeKind.ProfileUpsert);
            await CustomerChangeOutboxSync.SyncPendingAsync(
                client, db, CustomerSyncChangeKind.CreditPayment);
            return null;
        }
        catch (HttpRequestException exception)
        {
            return exception.Message;
        }
        catch (TaskCanceledException exception)
        {
            return exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
        catch (System.Text.Json.JsonException exception)
        {
            return exception.Message;
        }
    }

    private async Task SyncPendingChangesSafelyAsync()
    {
        if (_syncing || IsDisposed || !IsHandleCreated)
        {
            return;
        }
        _syncing = true;
        try
        {
            using var client = await CloudApiClientFactory.CreateAuthorizedClientAsync(
                administrator: _isAdministrator);
            await using var db = new TiendaDbContext(Options);
            await CustomerChangeOutboxSync.SyncPendingAsync(
                client, db, CustomerSyncChangeKind.ProfileUpsert);
            await CustomerChangeOutboxSync.SyncPendingAsync(
                client, db, CustomerSyncChangeKind.CreditPayment);
        }
        catch (HttpRequestException exception)
        {
            Status.Text = $"Cambios de clientes pendientes de sincronizar: {exception.Message}";
        }
        catch (TaskCanceledException exception)
        {
            Status.Text = $"Sincronización de clientes temporalmente sin respuesta: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = $"Sincronización de clientes pendiente: {exception.Message}";
        }
        catch (System.Text.Json.JsonException exception)
        {
            Status.Text = $"Cambio de cliente inválido en la cola local: {exception.Message}";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SelectCustomerRow(Guid customerId)
    {
        foreach (DataGridViewRow row in Grid.Rows)
        {
            if (row.Cells[0].Value is Guid id && id == customerId)
            {
                row.Selected = true;
                Grid.CurrentCell = row.Cells[1];
                break;
            }
        }
        UpdateActionState();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Clientes y fiado", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void StyleCustomerButton(Button button)
    {
        DataModuleWindow.StyleButton(button);
        button.Margin = new Padding(3, 4, 3, 4);
    }

    private sealed class CustomerEditorDialog : Form
    {
        private readonly TextBox _name = new() { MaxLength = 160 };
        private readonly TextBox _phone = new() { MaxLength = 32 };
        private readonly TextBox _address = new() { MaxLength = 240 };
        private readonly TextBox _taxId = new() { MaxLength = 40 };
        private readonly TextBox _notes = new() { MaxLength = 500, Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly NumericUpDown _creditLimit = new()
        {
            DecimalPlaces = 2,
            Maximum = 100000000m,
            Minimum = 0m,
            ThousandsSeparator = true
        };

        public CreateCustomerRequest Request => new(
            _name.Text, _phone.Text, _creditLimit.Value, _address.Text, _taxId.Text, _notes.Text);

        public CustomerEditorDialog(CustomerAccount? customer = null)
        {
            Text = customer is null ? "Nuevo cliente" : "Editar ficha del cliente";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(520, 430);
            BackColor = ModernTheme.Canvas;
            ForeColor = ModernTheme.Ink;

            _name.Text = customer?.Name ?? string.Empty;
            _phone.Text = customer?.Phone ?? string.Empty;
            _address.Text = customer?.Address ?? string.Empty;
            _taxId.Text = customer?.TaxId ?? string.Empty;
            _notes.Text = customer?.Notes ?? string.Empty;
            _creditLimit.Value = Math.Clamp(customer?.CreditLimit ?? 0m, _creditLimit.Minimum, _creditLimit.Maximum);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 7,
                BackColor = BackColor
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddField(layout, "Nombre *", _name, 0);
            AddField(layout, "Teléfono", _phone, 1);
            AddField(layout, "Dirección", _address, 2);
            AddField(layout, "NIT / CI", _taxId, 3);
            AddField(layout, "Notas", _notes, 4);
            AddField(layout, "Límite de fiado (Bs)", _creditLimit, 5);
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var save = new Button { Text = "Guardar", Width = 110, Height = 38, DialogResult = DialogResult.OK };
            StyleCustomerButton(save);
            var cancel = new Button { Text = "Cancelar", Width = 110, Height = 38, DialogResult = DialogResult.Cancel };
            StyleCustomerButton(cancel);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 6);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text))
                {
                    MessageBox.Show(this, "Escribe el nombre del cliente.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    save.DialogResult = DialogResult.None;
                }
                else
                {
                    save.DialogResult = DialogResult.OK;
                }
            };
        }

        private static void AddField(TableLayoutPanel layout, string label, Control control, int row)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            }, 0, row);
            control.Dock = DockStyle.Fill;
            if (control is TextBox textBox)
            {
                ModernTheme.StyleInput(textBox);
            }
            layout.Controls.Add(control, 1, row);
        }
    }

    private sealed class CustomerCreditPaymentDialog : Form
    {
        private readonly NumericUpDown _amount = new()
        {
            DecimalPlaces = 2,
            Minimum = 0.01m,
            ThousandsSeparator = true
        };
        private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _description = new() { MaxLength = 240, PlaceholderText = "Detalle adicional (opcional)" };

        public decimal Amount => _amount.Value;
        public string Description => _description.Text.Trim();
        public PaymentMethod PaymentMethod => _method.SelectedValue is PaymentMethod method
            ? method
            : PaymentMethod.Cash;

        public CustomerCreditPaymentDialog(string customerName, decimal outstanding, string terminalId)
        {
            Text = "Registrar abono de fiado";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(520, 290);
            BackColor = ModernTheme.Canvas;
            ForeColor = ModernTheme.Ink;
            _amount.Maximum = outstanding;
            _amount.Value = Math.Min(outstanding, _amount.Maximum);
            var choices = new[]
            {
                new PaymentChoice("Efectivo", PaymentMethod.Cash),
                new PaymentChoice("QR", PaymentMethod.Transfer)
            };
            _method.DataSource = choices;
            _method.DisplayMember = nameof(PaymentChoice.Name);
            _method.ValueMember = nameof(PaymentChoice.Method);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label
            {
                Text = $"{customerName}\nDebe: {PosFormatting.Money(outstanding)} Bs · Caja: {terminalId}",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            }, 0, 0);
            layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
            AddField(layout, "Abono (Bs)", _amount, 1);
            AddField(layout, "Método de pago", _method, 2);
            AddField(layout, "Detalle", _description, 3);
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var save = new Button { Text = "Registrar", Width = 110, Height = 38, DialogResult = DialogResult.OK };
            StyleCustomerButton(save);
            var cancel = new Button { Text = "Cancelar", Width = 110, Height = 38, DialogResult = DialogResult.Cancel };
            StyleCustomerButton(cancel);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 4);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static void AddField(TableLayoutPanel layout, string label, Control control, int row)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            }, 0, row);
            control.Dock = DockStyle.Fill;
            if (control is TextBox textBox)
            {
                ModernTheme.StyleInput(textBox);
            }
            else if (control is ComboBox comboBox)
            {
                ModernTheme.StyleInput(comboBox);
            }
            layout.Controls.Add(control, 1, row);
        }
    }

    private sealed class CustomerHistoryDialog : Form
    {
        public CustomerHistoryDialog(
            string customerName,
            decimal totalPurchased,
            decimal outstanding,
            decimal totalPaid,
            IReadOnlyList<LedgerRow> movements)
        {
            Text = $"Historial · {customerName}";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 560);
            Size = new Size(1050, 640);
            BackColor = ModernTheme.Canvas;
            ForeColor = ModernTheme.Ink;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var summary = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = ModernTheme.Surface,
                Padding = new Padding(10)
            };
            AddMetric(summary, "Cliente", customerName);
            AddMetric(summary, "Compras con cuenta", PosFormatting.Money(totalPurchased) + " Bs");
            AddMetric(summary, "Debe", PosFormatting.Money(outstanding) + " Bs");
            AddMetric(summary, "Pagado", PosFormatting.Money(totalPaid) + " Bs");
            layout.Controls.Add(summary, 0, 0);

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                BackgroundColor = ModernTheme.Surface,
                GridColor = ModernTheme.Border,
                BorderStyle = BorderStyle.None,
                DataSource = movements.Select(item => new
                {
                    Fecha = new DateTimeOffset(DateTime.SpecifyKind(item.OccurredAtUtc, DateTimeKind.Utc), TimeSpan.Zero)
                        .ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    Movimiento = item.Kind switch
                    {
                        CustomerLedgerKind.CreditSale => "Fiado",
                        CustomerLedgerKind.CreditPayment => "Abono de fiado",
                        CustomerLedgerKind.WalletTopUp => "Recarga de monedero",
                        CustomerLedgerKind.WalletPurchase => "Compra con monedero",
                        CustomerLedgerKind.Reversal => "Reversión",
                        _ => item.Kind.ToString()
                    },
                    Detalle = item.Description,
                    EfectivoQR = item.PaymentMethod switch
                    {
                        PaymentMethod.Cash => "Efectivo",
                        PaymentMethod.Transfer => "QR",
                        _ => item.PaymentMethod.ToString()
                    },
                    Fiado = PosFormatting.Money(item.CreditDelta),
                    Monedero = PosFormatting.Money(item.WalletDelta),
                    Registró = item.Actor
                }).ToList()
            };
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = ModernTheme.SurfaceMuted;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ModernTheme.Purple;
            layout.Controls.Add(grid, 0, 1);
            Controls.Add(layout);
        }

        private static void AddMetric(FlowLayoutPanel parent, string title, string value)
        {
            var card = new Panel
            {
                Width = 220,
                Height = 64,
                BackColor = ModernTheme.SurfaceMuted,
                Margin = new Padding(5)
            };
            card.Controls.Add(new Label
            {
                Text = title.ToUpperInvariant(),
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = ModernTheme.Muted,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            });
            card.Controls.Add(new Label
            {
                Text = value,
                Dock = DockStyle.Fill,
                ForeColor = ModernTheme.Ink,
                Font = new Font("Segoe UI", 11, FontStyle.Bold)
            });
            parent.Controls.Add(card);
        }
    }

    private sealed record PaymentChoice(string Name, PaymentMethod Method);
}
