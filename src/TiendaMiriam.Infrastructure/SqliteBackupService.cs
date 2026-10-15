using Microsoft.Data.Sqlite;

namespace TiendaMiriam.Infrastructure;

public static class SqliteBackupService
{
    public static Task CreateBackupAsync(
        string sourceConnectionString,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var source = new SqliteConnectionStringBuilder(sourceConnectionString);
        if (source.Mode == SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(source.DataSource)
            || source.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("El origen debe ser un archivo SQLite persistente.", nameof(sourceConnectionString));
        }

        var sourcePath = Path.GetFullPath(source.DataSource);
        var targetPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("El respaldo no puede sobrescribir la base de datos activa.", nameof(destinationPath));
        }

        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("La ruta de respaldo no contiene una carpeta válida.", nameof(destinationPath));
        if (!Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException("La carpeta seleccionada para el respaldo no existe.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        return Task.Run(() => CopyAndValidate(source, temporaryPath, targetPath, cancellationToken), CancellationToken.None);
    }

    private static void CopyAndValidate(
        SqliteConnectionStringBuilder sourceBuilder,
        string temporaryPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            sourceBuilder.Pooling = false;
            sourceBuilder.Mode = SqliteOpenMode.ReadOnly;
            using (var source = new SqliteConnection(sourceBuilder.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                cancellationToken.ThrowIfCancellationRequested();

                using var command = destination.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                var result = command.ExecuteScalar()?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("La verificación de integridad del respaldo SQLite falló.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
