using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var databasePath = Environment.GetEnvironmentVariable("TIENDA_LOCAL_DB_PATH");
        string? packagedDatabaseTemplate = null;
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TiendaMiriam",
                "pos.db");
            packagedDatabaseTemplate = Path.Combine(AppContext.BaseDirectory, "pos.db");
        }
        databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var instanceLockPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TiendaMiriam",
            "pos.instance.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(instanceLockPath)!);
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(
                instanceLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            MessageBox.Show(
                "Ya hay una caja de ventas abierta en este equipo. Usa esa instancia para evitar duplicar la operación.",
                "Tienda Miriam · Caja única",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        using (instanceLock)
        {
        if (packagedDatabaseTemplate is not null
            && !File.Exists(databasePath)
            && File.Exists(packagedDatabaseTemplate))
        {
            File.Copy(packagedDatabaseTemplate, databasePath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = true
        }.ToString();
        var options = new DbContextOptionsBuilder<TiendaDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var db = new TiendaDbContext(options))
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL;";
                await command.ExecuteScalarAsync();

                await db.Database.EnsureCreatedAsync();
                await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
                await DemoCatalogSeeder.EnsureDemoClassificationCorrectedAsync(db);
                if (string.Equals(
                    Environment.GetEnvironmentVariable("TIENDA_SEED_DEMO"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                {
                    await DemoDataSeeder.EnsureSeededAsync(db);
                    await DemoCatalogSeeder.EnsureSeededAsync(db);
                    await DemoCatalogSeeder.EnsureLotsSeededAsync(db);
                }
                await DemoBrandingAssets.EnsureLocalAssetsAsync(db);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        if (args.Contains("--initialize-database", StringComparer.Ordinal))
        {
            return;
        }

        try
        {
            VisualAnimationPreferences.Enabled =
                (await IdleScreenSettingsStore.LoadAsync(options)).AnimationsEnabled;
        }
        catch (InvalidDataException exception)
        {
            VisualAnimationPreferences.Enabled = true;
            MessageBox.Show(
                $"No se pudo leer la preferencia de animaciones; se usará la configuración predeterminada. {exception.Message}",
                "Configuración visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (DbUpdateException exception)
        {
            VisualAnimationPreferences.Enabled = true;
            MessageBox.Show(
                $"No se pudo leer la preferencia de animaciones; se usará la configuración predeterminada. {exception.GetBaseException().Message}",
                "Configuración visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (SqliteException exception)
        {
            VisualAnimationPreferences.Enabled = true;
            MessageBox.Show(
                $"Error de SQLite al leer la preferencia de animaciones; se usará la configuración predeterminada. {exception.Message}",
                "Configuración visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        var localUser = await LocalLoginDialog.AuthenticateAsync(options);
        if (localUser is null)
        {
            return;
        }
        if (localUser.Role == AppRole.Kitchen)
        {
            MessageBox.Show(
                "El rol Cocina no tiene una pantalla de sesión local en esta versión. Solicita al administrador un usuario Cajero o Administrador para usar esta caja.",
                "Rol sin acceso a la caja",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var startInSales = string.Equals(
            Environment.GetEnvironmentVariable("TIENDA_START_MODULE"), "sales", StringComparison.OrdinalIgnoreCase);
        Application.Run(new NavigationShellForm(options, localUser, startInSales));
        }
    }
}
