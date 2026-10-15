using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal static class CatalogImageStore
{
    private const long MaximumFileBytes = 50 * 1024 * 1024;

    public static async Task<byte[]> ReadAndNormalizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (file.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidOperationException("Selecciona una imagen válida de hasta 50 MB.");
        }

        var sourceBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return await Task.Run(() => DemoBrandingAssets.NormalizePng(sourceBytes), cancellationToken);
    }

    public static async Task<byte[]?> LoadAsync(
        TiendaDbContext db, string category, Guid entityId, CancellationToken cancellationToken = default)
    {
        var key = CreateKey(category, entityId);
        return await db.BusinessAssets.AsNoTracking()
            .Where(asset => asset.Key == key)
            .Select(asset => asset.Data)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public static async Task SaveAsync(
        TiendaDbContext db,
        string category,
        Guid entityId,
        byte[] normalizedPng,
        CancellationToken cancellationToken = default)
    {
        var key = CreateKey(category, entityId);
        var asset = await db.BusinessAssets.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (asset is null)
        {
            db.BusinessAssets.Add(new TiendaMiriam.Domain.BusinessAsset
            {
                Key = key,
                ContentType = "image/png",
                Data = normalizedPng,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            asset.ContentType = "image/png";
            asset.Data = normalizedPng;
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<bool> DeleteAsync(
        TiendaDbContext db, string category, Guid entityId, CancellationToken cancellationToken = default)
    {
        var key = CreateKey(category, entityId);
        var asset = await db.BusinessAssets.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (asset is null)
        {
            return false;
        }

        db.BusinessAssets.Remove(asset);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string CreateKey(string category, Guid entityId) =>
        $"catalog-image:{category}:{entityId:N}";
}
