using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class AuditOutboxSync
{
    public static async Task<int> SyncPendingAsync(
        HttpClient client, TiendaDbContext db, CancellationToken cancellationToken = default)
    {
        var pending = await db.AuditOutbox.Where(item => !item.IsDelivered)
            .OrderBy(item => item.CreatedAtUtc).Take(50).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var entries = new List<AuditEntry>(pending.Count);
        foreach (var item in pending)
        {
            try
            {
                var entry = System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(item.Payload)
                    ?? throw new InvalidDataException("La auditoría serializada está vacía.");
                entries.Add(entry);
            }
            catch (System.Text.Json.JsonException exception)
            {
                item.Attempts++;
                item.LastError = $"Outbox de auditoría inválida: {exception.Message}";
                await db.SaveChangesAsync(cancellationToken);
                throw new InvalidDataException($"No se pudo leer la auditoría pendiente {item.AuditEntryId}.", exception);
            }
        }

        using var response = await client.PostAsJsonAsync("/api/sync/audit", entries, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (var item in pending)
            {
                item.Attempts++;
                item.LastError = $"HTTP {(int)response.StatusCode}: {message}";
            }
            await db.SaveChangesAsync(cancellationToken);
            throw new HttpRequestException($"No se pudo sincronizar la auditoría: HTTP {(int)response.StatusCode}.");
        }

        foreach (var item in pending)
        {
            item.IsDelivered = true;
            item.LastError = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }
}
