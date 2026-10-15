using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class ProductionService(TiendaDbContext db) : IProductionService
{
    public async Task<ProductionCapacityResponse> GetProductionCapacityAsync(
        Guid productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == productId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe o está inactivo.");
        if (product.IsRecipe || product.StockItemId is not Guid finishedStockId
            || !await db.ProductDetails.AsNoTracking()
                .AnyAsync(item => item.ProductId == productId
                    && item.Department == ProductDepartment.FrozenBatch, cancellationToken))
        {
            throw new InvalidOperationException(
                "La producción por lotes requiere un producto congelado enlazado a su propio stock; no uses un producto vendido por receta.");
        }
        var finishedStock = await db.StockItems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == finishedStockId, cancellationToken)
            ?? throw new InvalidOperationException("El producto terminado no tiene una existencia configurada.");
        var latestBatch = await db.StockProductionBatches.AsNoTracking()
            .Where(item => item.ProductId == productId)
            .OrderByDescending(item => item.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestBatch is null)
        {
            return new ProductionCapacityResponse(
                product.Id, product.Name, finishedStock.Unit, finishedStock.OnHand, null, null, []);
        }

        var batchLines = await db.StockProductionLines.AsNoTracking()
            .Where(item => item.ProductionBatchId == latestBatch.Id)
            .OrderBy(item => item.StockItemId)
            .ToListAsync(cancellationToken);
        var ingredientIds = batchLines.Select(item => item.StockItemId).ToArray();
        var ingredients = await db.StockItems.AsNoTracking()
            .Where(item => ingredientIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (batchLines.Count == 0 || batchLines.Any(line =>
                line.QuantityConsumed <= 0 || !ingredients.ContainsKey(line.StockItemId)))
        {
            return new ProductionCapacityResponse(
                product.Id, product.Name, finishedStock.Unit, finishedStock.OnHand,
                null, new DateTimeOffset(latestBatch.OccurredAtUtc, TimeSpan.Zero), []);
        }

        var capacityLines = batchLines.Select(line =>
        {
            var stock = ingredients[line.StockItemId];
            var quantityPerUnit = line.QuantityConsumed / latestBatch.QuantityProduced;
            var possible = decimal.Floor(stock.OnHand / quantityPerUnit);
            return new ProductionCapacityIngredientResponse(
                stock.Name, stock.OnHand, stock.Unit, quantityPerUnit, possible);
        }).ToArray();
        return new ProductionCapacityResponse(
            product.Id, product.Name, finishedStock.Unit, finishedStock.OnHand,
            capacityLines.Min(item => item.PossibleFinishedUnits),
            new DateTimeOffset(latestBatch.OccurredAtUtc, TimeSpan.Zero),
            capacityLines);
    }

    public async Task<ProductionBatchResponse> RecordProductionAsync(
        RecordProductionRequest request, string actor, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var previous = await db.StockProductionBatches.AsNoTracking()
            .Where(item => item.Id == request.BatchId)
            .Select(item => new { item.Id, item.ProductId, item.QuantityProduced, item.TotalCost, item.OccurredAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (previous is not null)
        {
            var previousLines = await db.StockProductionLines.AsNoTracking()
                .Where(item => item.ProductionBatchId == previous.Id)
                .OrderBy(item => item.StockItemId)
                .Select(item => new { item.StockItemId, item.QuantityConsumed })
                .ToListAsync(cancellationToken);
            var requestedLines = request.Ingredients.OrderBy(item => item.StockItemId).ToArray();
            if (previous.ProductId != request.ProductId
                || previous.QuantityProduced != request.QuantityProduced
                || !previousLines.Select(item => (item.StockItemId, item.QuantityConsumed))
                    .SequenceEqual(requestedLines.Select(item => (item.StockItemId, item.QuantityConsumed))))
            {
                throw new InvalidOperationException("El identificador del lote ya se usó con datos distintos.");
            }
            var previousName = await db.Products.AsNoTracking()
                .Where(item => item.Id == previous.ProductId)
                .Select(item => item.Name)
                .SingleAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ProductionBatchResponse(
                previous.Id, previous.ProductId, previousName, previous.QuantityProduced,
                previous.TotalCost / previous.QuantityProduced,
                new DateTimeOffset(previous.OccurredAtUtc, TimeSpan.Zero));
        }

        var product = await db.Products.SingleOrDefaultAsync(
            item => item.Id == request.ProductId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El producto terminado no existe o está inactivo.");
        if (product.IsRecipe || product.StockItemId is not Guid finishedStockId
            || !await db.ProductDetails.AsNoTracking()
                .AnyAsync(item => item.ProductId == request.ProductId
                    && item.Department == ProductDepartment.FrozenBatch, cancellationToken))
        {
            throw new InvalidOperationException(
                "Selecciona un producto congelado enlazado a su propio stock. Los productos con receta al momento de venta no se producen por este módulo.");
        }

        var ingredientIds = request.Ingredients.Select(item => item.StockItemId).ToArray();
        if (ingredientIds.Contains(finishedStockId))
        {
            throw new InvalidOperationException("El stock del producto terminado no puede usarse como insumo del mismo lote.");
        }
        var stockIds = ingredientIds.Append(finishedStockId).Distinct().ToArray();
        var stocks = await db.StockItems.Where(item => stockIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (stocks.Count != stockIds.Length)
        {
            throw new KeyNotFoundException("Falta uno o más insumos o la existencia del producto terminado.");
        }

        var finishedStock = stocks[finishedStockId];
        var occurredAt = DateTime.UtcNow;
        var batch = new StockProductionBatch
        {
            Id = request.BatchId,
            ProductId = product.Id,
            FinishedStockItemId = finishedStockId,
            QuantityProduced = request.QuantityProduced,
            Actor = actor,
            OccurredAtUtc = occurredAt
        };

        foreach (var ingredient in request.Ingredients.OrderBy(item => item.StockItemId))
        {
            var stock = stocks[ingredient.StockItemId];
            if (stock.OnHand < ingredient.QuantityConsumed)
            {
                throw new InvalidOperationException(
                    $"Stock insuficiente de «{stock.Name}»: se requieren {ingredient.QuantityConsumed:0.######} {stock.Unit} y hay {stock.OnHand:0.######} {stock.Unit}.");
            }

            var lots = await db.StockLots
                .Where(lot => lot.StockItemId == stock.Id && lot.RemainingQuantity > 0)
                .OrderBy(lot => lot.ExpiresAtUtc == null)
                .ThenBy(lot => lot.ExpiresAtUtc)
                .ThenBy(lot => lot.ReceivedAtUtc)
                .ToListAsync(cancellationToken);
            var untrackedBalance = Math.Max(0m, stock.OnHand - lots.Sum(lot => lot.RemainingQuantity));
            var eligibleLots = lots
                .Where(lot => lot.ExpiresAtUtc is null || lot.ExpiresAtUtc > occurredAt)
                .ToArray();
            if (eligibleLots.Sum(lot => lot.RemainingQuantity) + untrackedBalance < ingredient.QuantityConsumed)
            {
                throw new InvalidOperationException($"Stock vigente insuficiente de «{stock.Name}» para producir el lote.");
            }

            var remaining = ingredient.QuantityConsumed;
            var ingredientCost = 0m;
            foreach (var lot in eligibleLots)
            {
                if (remaining == 0)
                {
                    break;
                }
                var consumed = Math.Min(lot.RemainingQuantity, remaining);
                lot.RemainingQuantity -= consumed;
                remaining -= consumed;
                ingredientCost += consumed * lot.UnitCost;
                AddMovement(db, batch.Id, stock.Id, -consumed, occurredAt, lot.Id, "Consumo producción");
            }
            if (remaining > 0)
            {
                ingredientCost += remaining * stock.UnitCost;
                AddMovement(db, batch.Id, stock.Id, -remaining, occurredAt, null, "Consumo producción");
            }

            stock.OnHand -= ingredient.QuantityConsumed;
            var lineCost = decimal.Round(ingredientCost, 4, MidpointRounding.AwayFromZero);
            batch.TotalCost += lineCost;
            batch.Lines.Add(new StockProductionLine
            {
                Id = Guid.NewGuid(),
                ProductionBatchId = batch.Id,
                StockItemId = stock.Id,
                QuantityConsumed = ingredient.QuantityConsumed,
                UnitCost = decimal.Round(
                    ingredientCost / ingredient.QuantityConsumed, 4, MidpointRounding.AwayFromZero),
                TotalCost = lineCost
            });
        }

        batch.TotalCost = decimal.Round(batch.TotalCost, 4, MidpointRounding.AwayFromZero);
        var finishedUnitCost = decimal.Round(
            batch.TotalCost / batch.QuantityProduced, 4, MidpointRounding.AwayFromZero);
        var previousFinishedValue = finishedStock.OnHand * finishedStock.UnitCost;
        finishedStock.OnHand += batch.QuantityProduced;
        finishedStock.UnitCost = decimal.Round(
            (previousFinishedValue + batch.TotalCost) / finishedStock.OnHand,
            4,
            MidpointRounding.AwayFromZero);

        var finishedLot = new StockLot
        {
            Id = batch.Id,
            StockItemId = finishedStock.Id,
            LotCode = $"PROD-{occurredAt:yyyyMMddHHmmss}-{batch.Id:N}"[..Math.Min(80, $"PROD-{occurredAt:yyyyMMddHHmmss}-{batch.Id:N}".Length)],
            ReceivedQuantity = batch.QuantityProduced,
            RemainingQuantity = batch.QuantityProduced,
            UnitCost = finishedUnitCost,
            ReceivedAtUtc = occurredAt
        };
        db.StockLots.Add(finishedLot);
        AddMovement(db, batch.Id, finishedStock.Id, batch.QuantityProduced, occurredAt, finishedLot.Id, "Producción recibida");
        db.StockProductionBatches.Add(batch);
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = "PRODUCTION_BATCH_RECORDED",
            EntityType = nameof(StockProductionBatch),
            EntityId = batch.Id,
            Reason = $"{batch.QuantityProduced:0.######} {finishedStock.Unit} de {product.Name}; costo del lote {batch.TotalCost:0.####}",
            OccurredAtUtc = occurredAt
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ProductionBatchResponse(
            batch.Id, product.Id, product.Name, batch.QuantityProduced,
            finishedUnitCost, new DateTimeOffset(occurredAt, TimeSpan.Zero));
    }

    private static void ValidateRequest(RecordProductionRequest request)
    {
        if (request.BatchId == Guid.Empty || request.ProductId == Guid.Empty
            || request.QuantityProduced <= 0 || decimal.Round(request.QuantityProduced, 6) != request.QuantityProduced
            || request.Ingredients is null || request.Ingredients.Count == 0 || request.Ingredients.Count > 200
            || request.Ingredients.Any(item => item.StockItemId == Guid.Empty || item.QuantityConsumed <= 0
                || decimal.Round(item.QuantityConsumed, 6) != item.QuantityConsumed)
            || request.Ingredients.Select(item => item.StockItemId).Distinct().Count() != request.Ingredients.Count)
        {
            throw new InvalidOperationException(
                "El lote requiere producto terminado, cantidad positiva y consumos válidos sin insumos repetidos.");
        }
    }

    private static void AddMovement(
        TiendaDbContext db,
        Guid batchId,
        Guid stockItemId,
        decimal delta,
        DateTime occurredAt,
        Guid? lotId,
        string reason)
    {
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            StockItemId = stockItemId,
            SaleId = Guid.Empty,
            ProductionBatchId = batchId,
            StockLotId = lotId,
            QuantityDelta = delta,
            OccurredAtUtc = occurredAt,
            Reason = reason
        });
    }
}
