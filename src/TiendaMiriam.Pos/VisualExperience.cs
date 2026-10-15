using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal sealed record IdleScreenSettings(
    int IdleMinutes = 20,
    string BusinessName = "Tienda Miriam",
    string WelcomeMessage = "Bienvenido a Tienda Miriam",
    bool Use24HourClock = true,
    bool RequirePasswordOnWake = false,
    bool AnimationsEnabled = true);

internal static class VisualAnimationPreferences
{
    public static bool Enabled { get; set; } = true;
}

internal static class WindowFadeAnimator
{
    private const int FadeInDurationMs = 240;
    private const int FadeOutDurationMs = 200;
    private const int AwBlend = 0x00080000;
    private const int AwActivate = 0x00020000;
    private const int AwHide = 0x00010000;

    public static void Attach(Form form)
    {
        form.Shown += (_, _) =>
        {
            if (VisualAnimationPreferences.Enabled && form.TopLevel && form.WindowState != FormWindowState.Minimized)
            {
                AnimateWindow(form.Handle, FadeInDurationMs, AwBlend | AwActivate);
            }
        };
        form.FormClosing += (_, _) =>
        {
            if (VisualAnimationPreferences.Enabled && form.TopLevel && form.IsHandleCreated
                && form.WindowState != FormWindowState.Minimized)
            {
                AnimateWindow(form.Handle, FadeOutDurationMs, AwBlend | AwHide);
            }
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AnimateWindow(IntPtr windowHandle, int durationMs, int flags);
}

internal sealed class TransitionVeil : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 20 };
    private int _alpha;

    public TransitionVeil()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint, true);
        Enabled = false;
        Visible = false;
        _timer.Tick += (_, _) =>
        {
            _alpha = Math.Max(0, _alpha - 16);
            Invalidate();
            if (_alpha == 0)
            {
                _timer.Stop();
                Visible = false;
            }
        };
    }

    public void Play()
    {
        if (!VisualAnimationPreferences.Enabled || IsDisposed || !IsHandleCreated)
        {
            return;
        }
        _timer.Stop();
        _alpha = 112;
        Visible = true;
        BringToFront();
        Invalidate();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Color.FromArgb(_alpha, ModernTheme.Canvas));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class IdleScreenSettingsStore
{
    private const string AssetKey = "idle-screen-settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IdleScreenSettings> LoadAsync(
        DbContextOptions<TiendaDbContext> options,
        CancellationToken cancellationToken = default)
    {
        await using var db = new TiendaDbContext(options);
        var asset = await db.BusinessAssets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == AssetKey, cancellationToken);
        if (asset is null)
        {
            return new IdleScreenSettings();
        }

        var settings = JsonSerializer.Deserialize<IdleScreenSettings>(asset.Data, JsonOptions)
            ?? throw new InvalidDataException("La configuración visual guardada no se puede leer.");
        Validate(settings);
        return settings;
    }

    public static async Task SaveAsync(
        DbContextOptions<TiendaDbContext> options,
        IdleScreenSettings settings,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var data = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        await using var db = new TiendaDbContext(options);
        var asset = await db.BusinessAssets.SingleOrDefaultAsync(
            item => item.Key == AssetKey, cancellationToken);
        if (asset is null)
        {
            db.BusinessAssets.Add(new BusinessAsset
            {
                Key = AssetKey,
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
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(IdleScreenSettings settings)
    {
        if (settings.IdleMinutes is < 1 or > 240
            || string.IsNullOrWhiteSpace(settings.BusinessName)
            || settings.BusinessName.Trim().Length > 80
            || string.IsNullOrWhiteSpace(settings.WelcomeMessage)
            || settings.WelcomeMessage.Trim().Length > 160)
        {
            throw new InvalidDataException("La configuración visual contiene valores inválidos.");
        }
    }
}

internal sealed class IdleScreenSettingsDialog : Form
{
    private readonly NumericUpDown _idleMinutes = new()
        { Minimum = 1, Maximum = 240, Value = 20, Dock = DockStyle.Fill };
    private readonly TextBox _businessName = new()
        { Dock = DockStyle.Fill, MaxLength = 80, Text = "Tienda Miriam" };
    private readonly TextBox _welcomeMessage = new()
        { Dock = DockStyle.Fill, MaxLength = 160, Text = "Bienvenido a Tienda Miriam" };
    private readonly CheckBox _use24HourClock = new()
        { Text = "Mostrar la hora en formato 24 horas", Checked = true, AutoSize = true };
    private readonly CheckBox _requirePassword = new()
        { Text = "Pedir contraseña al despertar", Checked = false, AutoSize = true };
    private readonly CheckBox _animationsEnabled = new()
        { Text = "Activar animaciones visuales", Checked = true, AutoSize = true };

    public IdleScreenSettingsDialog(IdleScreenSettings settings)
    {
        WindowFadeAnimator.Attach(this);
        Text = "Pantalla de espera y animaciones";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(660, 390);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;

        _idleMinutes.Value = settings.IdleMinutes;
        _businessName.Text = settings.BusinessName;
        _welcomeMessage.Text = settings.WelcomeMessage;
        _use24HourClock.Checked = settings.Use24HourClock;
        _requirePassword.Checked = settings.RequirePasswordOnWake;
        _animationsEnabled.Checked = settings.AnimationsEnabled;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 2,
            RowCount = 9,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        for (var row = 1; row < 6; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        AddField(layout, "Inactividad (minutos)", _idleMinutes, 1);
        AddField(layout, "Nombre del negocio", _businessName, 2);
        AddField(layout, "Mensaje de bienvenida", _welcomeMessage, 3);
        layout.Controls.Add(_use24HourClock, 1, 4);
        layout.Controls.Add(_requirePassword, 1, 5);
        layout.Controls.Add(_animationsEnabled, 1, 6);
        var note = new Label
        {
            Text = "El logotipo se toma del logotipo actual del negocio. El modo de espera no cierra ni modifica la venta activa.",
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(note, 0, 7);
        layout.SetColumnSpan(note, 2);

        var save = new Button { Text = "Guardar", AutoSize = true };
        ModernTheme.StyleButton(save, true);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_businessName.Text)
                || string.IsNullOrWhiteSpace(_welcomeMessage.Text))
            {
                MessageBox.Show(this, "Completa el nombre del negocio y el mensaje de bienvenida.",
                    "Configuración visual", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
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
        layout.Controls.Add(actions, 0, 8);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public IdleScreenSettings Settings => new(
        (int)_idleMinutes.Value,
        _businessName.Text.Trim(),
        _welcomeMessage.Text.Trim(),
        _use24HourClock.Checked,
        _requirePassword.Checked,
        _animationsEnabled.Checked);

    private static void AddField(TableLayoutPanel layout, string label, Control input, int row)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.Muted
        }, 0, row);
        if (input is TextBox textBox)
        {
            ModernTheme.StyleInput(textBox);
        }
        layout.Controls.Add(input, 1, row);
    }
}

internal sealed class IdleScreenControl : UserControl
{
    private IdleScreenSettings _settings;
    private readonly PictureBox _logo = new()
    {
        Size = new Size(320, 150),
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.Transparent,
        Anchor = AnchorStyles.None
    };
    private readonly Label _businessName = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 34, FontStyle.Bold)
    };
    private readonly Label _welcome = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = ModernTheme.OrangeDark,
        Font = new Font("Segoe UI", 18, FontStyle.Regular)
    };
    private readonly Label _clock = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        Font = new Font("Segoe UI Light", 44, FontStyle.Regular)
    };
    private readonly Label _continueMessage = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = ModernTheme.Muted,
        Font = new Font("Segoe UI", 12, FontStyle.Regular)
    };
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 80 };
    private DateTime _animationStarted;
    private Size _logoBaseSize;

    public IdleScreenControl(IdleScreenSettings settings)
    {
        _settings = settings;
        Dock = DockStyle.Fill;
        TabStop = true;
        Visible = false;
        BackColor = ModernTheme.Canvas;

        var content = new TableLayoutPanel
        {
            Size = new Size(760, 590),
            ColumnCount = 1,
            RowCount = 5,
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent,
            Padding = new Padding(24)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _businessName.Text = settings.BusinessName;
        _welcome.Text = settings.WelcomeMessage;
        _continueMessage.Text = "Toque la pantalla o presione cualquier tecla para continuar";
        content.Controls.Add(_logo, 0, 0);
        content.Controls.Add(_businessName, 0, 1);
        content.Controls.Add(_welcome, 0, 2);
        content.Controls.Add(_clock, 0, 3);
        content.Controls.Add(_continueMessage, 0, 4);
        Controls.Add(content);
        Resize += (_, _) => CenterContent(content);
        VisibleChanged += (_, _) => ConfigureAnimation();
        CenterContent(content);
        UpdateClock(settings.Use24HourClock, DateTime.Now);
        _animationTimer.Tick += (_, _) => AnimateLogo();
        ConfigureAnimation();
    }

    public void ApplySettings(IdleScreenSettings settings)
    {
        _settings = settings;
        _businessName.Text = settings.BusinessName;
        _welcome.Text = settings.WelcomeMessage;
        _continueMessage.Text = "Toque la pantalla o presione cualquier tecla para continuar";
        UpdateClock(settings.Use24HourClock, DateTime.Now);
        ConfigureAnimation();
        Invalidate();
    }

    public async Task LoadLogoAsync(
        DbContextOptions<TiendaDbContext> options,
        CancellationToken cancellationToken = default)
    {
        await using var db = new TiendaDbContext(options);
        var asset = await db.BusinessAssets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == DemoDataSeeder.LogoAssetKey, cancellationToken);
        if (asset is null)
        {
            return;
        }
        using var stream = new MemoryStream(asset.Data, writable: false);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        var image = new Bitmap(source);
        var previous = _logo.Image;
        _logo.Image = image;
        previous?.Dispose();
    }

    public void UpdateClock(bool use24Hour, DateTime now) =>
        _clock.Text = now.ToString(use24Hour ? "HH:mm:ss" : "h:mm:ss tt");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _logo.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void CenterContent(Control content)
    {
        content.Left = Math.Max(0, (ClientSize.Width - content.Width) / 2);
        content.Top = Math.Max(0, (ClientSize.Height - content.Height) / 2);
    }

    private void AnimateLogo()
    {
        var phase = (DateTime.UtcNow - _animationStarted).TotalSeconds * (Math.PI / 4);
        var scale = 1d + 0.025d * Math.Sin(phase);
        _logo.Size = new Size(
            (int)(_logoBaseSize.Width * scale),
            (int)(_logoBaseSize.Height * scale));
    }

    private void ConfigureAnimation()
    {
        if (_settings.AnimationsEnabled && Visible)
        {
            _animationStarted = DateTime.UtcNow;
            _logoBaseSize = _logo.Size;
            _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
            _logo.Size = new Size(320, 150);
        }
    }
}

internal sealed class WakeAuthenticationDialog : Form
{
    private readonly TextBox _password = new()
        { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256 };
    private readonly LocalUserIdentity _user;
    private readonly DbContextOptions<TiendaDbContext> _options;
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = ModernTheme.Muted,
        Text = "Confirma tu contraseña para continuar."
    };

    public WakeAuthenticationDialog(
        DbContextOptions<TiendaDbContext> options,
        LocalUserIdentity user)
    {
        WindowFadeAnimator.Attach(this);
        _options = options;
        _user = user;
        Text = "Desbloquear Tienda Miriam";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(460, 220);
        BackColor = ModernTheme.Canvas;
        ForeColor = ModernTheme.Ink;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 2,
            RowCount = 4,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var heading = new Label
        {
            Text = $"Bienvenido, {_user.Username}",
            Dock = DockStyle.Fill,
            ForeColor = ModernTheme.OrangeDark,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        layout.Controls.Add(new Label
        {
            Text = "Contraseña",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        ModernTheme.StyleInput(_password);
        layout.Controls.Add(_password, 1, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.SetColumnSpan(_status, 2);
        var unlock = new Button { Text = "Continuar", AutoSize = true };
        ModernTheme.StyleButton(unlock, true);
        unlock.Click += async (_, _) => await VerifyPasswordAsync();
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        actions.Controls.Add(unlock);
        layout.Controls.Add(actions, 0, 3);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = unlock;
    }

    private async Task VerifyPasswordAsync()
    {
        try
        {
            await using var db = new TiendaDbContext(_options);
            var authenticated = await new LocalAuthenticationService(db)
                .AuthenticateAsync(_user.Username, _password.Text);
            if (authenticated?.Id != _user.Id)
            {
                _password.Clear();
                _status.Text = "Contraseña incorrecta o cuenta temporalmente bloqueada.";
                _status.ForeColor = ModernTheme.Danger;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (DbUpdateException exception)
        {
            _status.Text = $"No se pudo verificar el acceso: {exception.GetBaseException().Message}";
            _status.ForeColor = ModernTheme.Danger;
        }
        catch (SqliteException exception)
        {
            _status.Text = $"Error de base de datos: {exception.Message}";
            _status.ForeColor = ModernTheme.Danger;
        }
    }
}

internal sealed class IdleActivityMessageFilter(Func<bool> onInput) : IMessageFilter
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is not (WmKeyDown or WmSysKeyDown
            or WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown
            or WmMouseWheel or WmXButtonDown))
        {
            return false;
        }
        return onInput();
    }
}
