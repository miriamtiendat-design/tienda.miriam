using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class DiningTableService(TiendaDbContext db)
{
    public async Task CombineAsync(
        string sourceTable,
        string destinationTable,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceTable) || string.IsNullOrWhiteSpace(destinationTable)
            || string.Equals(sourceTable.Trim(), destinationTable.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException("Selecciona dos mesas diferentes y un usuario válido.");
        }

        sourceTable = sourceTable.Trim();
        destinationTable = destinationTable.Trim();
        actor = actor.Trim();

        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var source = await db.HeldSales.SingleOrDefaultAsync(item =>
            item.DiningTable == sourceTable && !item.ClosePending && !item.DeletePending,
            cancellationToken);
        var destination = await db.HeldSales.SingleOrDefaultAsync(item =>
            item.DiningTable == destinationTable && !item.ClosePending && !item.DeletePending,
            cancellationToken);
        if (source is null || destination is null)
        {
            throw new InvalidOperationException("Una de las comandas cambió o se cerró. Actualiza el plano e inténtalo de nuevo.");
        }
        if (source.CustomerId != destination.CustomerId
            || source.PriceListId != destination.PriceListId
            || source.FulfillmentMode != destination.FulfillmentMode)
        {
            throw new InvalidOperationException("No se pueden combinar comandas con distintos clientes, listas de precios o modalidades.");
        }

        var destinationLines = JsonSerializer.Deserialize<List<SaleLineRequest>>(destination.Payload)
            ?? throw new JsonException("La comanda de destino está dañada.");
        var sourceLines = JsonSerializer.Deserialize<List<SaleLineRequest>>(source.Payload)
            ?? throw new JsonException("La comanda de origen está dañada.");
        foreach (var incoming in sourceLines)
        {
            var existingIndex = destinationLines.FindIndex(line => line.ProductId == incoming.ProductId);
            if (existingIndex < 0)
            {
                destinationLines.Add(incoming);
                continue;
            }

            var existing = destinationLines[existingIndex];
            if (existing.UnitPriceAtSale != incoming.UnitPriceAtSale
                || existing.AppliedPromotionId != incoming.AppliedPromotionId
                || existing.PromotionBundleId != incoming.PromotionBundleId
                || !string.Equals(existing.Notes, incoming.Notes, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"No se pueden combinar las líneas de producto {incoming.ProductId}: tienen precios, promociones o notas diferentes.");
            }
            destinationLines[existingIndex] = existing with { Quantity = existing.Quantity + incoming.Quantity };
        }

        var combinedPayload = JsonSerializer.Serialize(destinationLines);
        if (combinedPayload.Length > 32_768)
        {
            throw new InvalidOperationException("La comanda combinada excede el tamaño permitido.");
        }
        destination.Payload = combinedPayload;
        destination.NamedOrder = CombineOrderNames(destination.NamedOrder, source.NamedOrder);
        destination.KitchenNotes = CombineKitchenNotes(destination.KitchenNotes, source.KitchenNotes);
        destination.CheckoutRequested |= source.CheckoutRequested;
        MarkHeldSaleForSync(destination);
        source.DeletePending = true;
        source.DiningTable = null;
        source.UpdatedAtUtc = DateTime.UtcNow;
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = "DINING_TABLES_COMBINED",
            EntityType = "DiningTables",
            EntityId = source.Id,
            Reason = $"Combinación de mesas {sourceTable} y {destinationTable}",
            OccurredAtUtc = DateTime.UtcNow,
            TerminalId = source.TerminalId,
            PreviousValue = sourceTable,
            NewValue = destinationTable
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string? CombineOrderNames(string? destination, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return destination;
        }
        if (string.IsNullOrWhiteSpace(destination))
        {
            return source;
        }
        var combined = $"{destination.Trim()} + {source.Trim()}";
        return combined.Length <= 80
            ? combined
            : throw new InvalidOperationException("Los nombres de las órdenes combinadas superan 80 caracteres.");
    }

    private static string? CombineKitchenNotes(string? destination, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return destination;
        }
        if (string.IsNullOrWhiteSpace(destination))
        {
            return source;
        }
        if (string.Equals(destination.Trim(), source.Trim(), StringComparison.Ordinal))
        {
            return destination;
        }
        var combined = $"{destination.Trim()} · {source.Trim()}";
        return combined.Length <= 160
            ? combined
            : throw new InvalidOperationException("Las notas de cocina combinadas superan 160 caracteres.");
    }

    private static void MarkHeldSaleForSync(HeldSale held)
    {
        if (!held.SyncPending)
        {
            held.ExpectedVersion = held.Version;
            held.Version++;
        }
        held.SyncPending = true;
        held.UpdatedAtUtc = DateTime.UtcNow;
    }
}
