using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class CustomerChangeOutboxSync
{
    public static async Task SyncPendingAsync(
        HttpClient client,
        TiendaDbContext db,
        CustomerSyncChangeKind kind,
        CancellationToken cancellationToken = default)
    {
        if (kind == CustomerSyncChangeKind.CreditPayment
            && await db.SyncOutbox.AnyAsync(item => !item.IsDelivered, cancellationToken))
        {
            return;
        }

        var pending = await db.CustomerChangeOutbox
            .Where(item => !item.IsDelivered && item.Kind == kind)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var item in pending)
        {
            CustomerSyncChange change;
            try
            {
                change = JsonSerializer.Deserialize<CustomerSyncChange>(item.Payload)
                    ?? throw new InvalidOperationException($"El cambio de cliente {item.Id} está vacío.");
            }
            catch (JsonException exception)
            {
                item.Attempts++;
                item.LastError = $"Outbox de cliente inválido: {exception.Message}";
                await db.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException(item.LastError, exception);
            }

            using var response = await client.PostAsJsonAsync(
                "/api/sync/customer-changes", change, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                item.Attempts++;
                item.LastError = $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
                await db.SaveChangesAsync(cancellationToken);
                throw new HttpRequestException(item.LastError);
            }

            item.IsDelivered = true;
            item.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
