using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TiendaMiriam.Infrastructure;

public static class PromotionBundleOutboxSync
{
    public static async Task SyncPendingAsync(
        HttpClient client,
        TiendaDbContext db,
        CancellationToken cancellationToken = default)
    {
        var pending = await db.PromotionBundleOutbox
            .Where(item => !item.IsDelivered)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);
        foreach (var item in pending)
        {
            JsonElement payload;
            try
            {
                using var document = JsonDocument.Parse(item.Payload);
                payload = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                item.Attempts++;
                item.LastError = $"Outbox inválido: {exception.Message}";
                await db.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"No se pudo leer el cambio pendiente del combo {item.PromotionBundleId}.", exception);
            }

            using var response = await client.PostAsJsonAsync(
                "/api/sync/promotion-bundles", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                item.Attempts++;
                item.LastError =
                    $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
                await db.SaveChangesAsync(cancellationToken);
                throw new HttpRequestException(item.LastError);
            }

            item.IsDelivered = true;
            item.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
