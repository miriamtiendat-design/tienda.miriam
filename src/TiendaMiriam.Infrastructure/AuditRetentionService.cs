using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TiendaMiriam.Infrastructure;

public static class AuditRetentionService
{
    public const int DefaultDays = 730;
    private const string RetentionSettingKey = "audit-retention-days";

    public static async Task<int> GetRetentionDaysAsync(
        TiendaDbContext db, CancellationToken cancellationToken = default)
    {
        var data = await db.BusinessAssets.AsNoTracking()
            .Where(asset => asset.Key == RetentionSettingKey)
            .Select(asset => asset.Data)
            .SingleOrDefaultAsync(cancellationToken);
        if (data is null || !int.TryParse(Encoding.UTF8.GetString(data), NumberStyles.None,
                CultureInfo.InvariantCulture, out var days))
        {
            return DefaultDays;
        }
        return Math.Clamp(days, 30, 3650);
    }

    public static async Task SaveRetentionDaysAsync(
        TiendaDbContext db, int days, CancellationToken cancellationToken = default)
    {
        if (days is < 30 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "La retención debe ser de 30 a 3650 días.");
        }
        var setting = await db.BusinessAssets.SingleOrDefaultAsync(
            asset => asset.Key == RetentionSettingKey, cancellationToken);
        var data = Encoding.UTF8.GetBytes(days.ToString(CultureInfo.InvariantCulture));
        if (setting is null)
        {
            db.BusinessAssets.Add(new TiendaMiriam.Domain.BusinessAsset
            {
                Key = RetentionSettingKey,
                ContentType = "text/plain",
                Data = data,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            setting.ContentType = "text/plain";
            setting.Data = data;
            setting.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<int> PruneExpiredAsync(
        TiendaDbContext db, CancellationToken cancellationToken = default)
    {
        var days = await GetRetentionDaysAsync(db, cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var expiredIds = db.AuditEntries.Where(entry => entry.OccurredAtUtc < cutoff
            && !db.AuditOutbox.Any(outbox => outbox.AuditEntryId == entry.Id && !outbox.IsDelivered))
            .Select(entry => entry.Id);
        await db.AuditOutbox.Where(outbox => expiredIds.Contains(outbox.AuditEntryId))
            .ExecuteDeleteAsync(cancellationToken);
        return await db.AuditEntries.Where(entry => expiredIds.Contains(entry.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
