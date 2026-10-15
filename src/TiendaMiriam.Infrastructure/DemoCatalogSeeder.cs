using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class DemoCatalogSeeder
{
    public const string SeedMarker = "demo-catalog-v2";
    private const string Actor = "demo-catalog-seed";

    public static async Task<bool> EnsureSeededAsync(
        TiendaDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (await db.BusinessAssets.AnyAsync(item => item.Key == SeedMarker, cancellationToken))
        {
            await EnsureDemoClassificationCorrectedAsync(db, cancellationToken);
            return false;
        }

        if (!await db.BusinessAssets.AnyAsync(item => item.Key == DemoDataSeeder.SeedMarker, cancellationToken))
        {
            throw new InvalidOperationException("Primero carga el catálogo de demostración base.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var commerce = new CommerceService(db);
        var stock = new Dictionary<string, StockItem>(StringComparer.Ordinal);

        async Task AddStockAsync(
            string key,
            string name,
            string unit,
            decimal openingStock,
            decimal reorderAt,
            decimal unitCost)
        {
            stock.Add(key, await commerce.CreateStockItemAsync(
                new CreateStockItemRequest(name, unit, openingStock, reorderAt, unitCost),
                Actor,
                cancellationToken));
        }

        await AddStockAsync("rice", "Arroz grano largo 1 kg", "bolsa", 45m, 12m, 8m);
        await AddStockAsync("oil", "Aceite vegetal 900 ml", "botella", 30m, 8m, 9.5m);
        await AddStockAsync("wafer", "Galleta wafer", "paquete", 60m, 15m, 2.2m);
        await AddStockAsync("detergent", "Detergente líquido 500 ml", "botella", 24m, 6m, 6.5m);
        await AddStockAsync("bag", "Bolsa de papel kraft", "unidad", 100m, 20m, 0.45m);
        await AddStockAsync("frozen", "Empanada congelada de queso", "unidad", 45m, 10m, 3.25m);
        await AddStockAsync("beefkg", "Carne de res para venta", "kg", 18m, 5m, 45m);
        await AddStockAsync("cheese", "Queso para recetas", "g", 6000m, 1500m, 0.035m);

        var ingredients = await db.StockItems.AsNoTracking()
            .Where(item => item.Name == "Harina" || item.Name == "Huevo"
                || item.Name == "Pollo" || item.Name == "Arroz" || item.Name == "Sal"
                || item.Name == "Cebolla")
            .ToDictionaryAsync(item => item.Name, item => item.Id, cancellationToken);
        foreach (var required in new[] { "Harina", "Huevo", "Pollo", "Arroz", "Sal", "Cebolla" })
        {
            if (!ingredients.ContainsKey(required))
            {
                throw new InvalidOperationException($"Falta el insumo demo requerido: {required}.");
            }
        }

        async Task AddProductAsync(
            string sku,
            string name,
            decimal price,
            ProductDepartment department,
            Guid? stockItemId,
            IReadOnlyList<RecipeComponentInput>? recipe = null,
            string? barcode = null,
            string? printStation = null)
        {
            await commerce.CreateProductAsync(
                new CreateProductRequest(
                    sku,
                    name,
                    price,
                    recipe is { Count: > 0 },
                    stockItemId,
                    department,
                    recipe ?? [],
                    barcode,
                    printStation),
                Actor,
                cancellationToken);
        }

        await AddProductAsync("DEMO-X-ARROZ", "Arroz grano largo 1 kg", 12m,
            ProductDepartment.RawIngredient, stock["rice"].Id, barcode: "7840000000101");
        await AddProductAsync("DEMO-X-ACEITE", "Aceite vegetal 900 ml", 14m,
            ProductDepartment.RawIngredient, stock["oil"].Id, barcode: "7840000000102");
        await AddProductAsync("DEMO-X-WAFER", "Galleta wafer", 4m,
            ProductDepartment.Retail, stock["wafer"].Id, barcode: "7840000000103");
        await AddProductAsync("DEMO-X-CARNEKG", "Carne de res por kg", 60m,
            ProductDepartment.RawIngredient, stock["beefkg"].Id, barcode: "2840000000104");
        await AddProductAsync("DEMO-X-EMPANADA", "Empanada congelada de queso", 7m,
            ProductDepartment.FrozenBatch, stock["frozen"].Id);
        await AddProductAsync("DEMO-X-POLLO", "Pollo con arroz (receta)", 28m,
            ProductDepartment.PreparedDirect, null,
            [
                new RecipeComponentInput(ingredients["Pollo"], 180m),
                new RecipeComponentInput(ingredients["Arroz"], 120m),
                new RecipeComponentInput(ingredients["Sal"], 2m),
                new RecipeComponentInput(ingredients["Cebolla"], 12m)
            ],
            printStation: "Cocina");
        await AddProductAsync("DEMO-X-RECARGA10", "Recarga móvil Bs 10", 12m,
            ProductDepartment.Service, null);
        await AddProductAsync("DEMO-X-DETERGENTE", "Detergente líquido 500 ml", 10m,
            ProductDepartment.Retail, stock["detergent"].Id, barcode: "7840000000105");
        await AddProductAsync("DEMO-X-FOTOCOPIA", "Fotocopia blanco y negro", 1m,
            ProductDepartment.Service, null);
        await AddProductAsync("DEMO-X-BOLSA", "Bolsa de papel kraft", 0.5m,
            ProductDepartment.Retail, stock["bag"].Id);

        db.BusinessAssets.Add(new BusinessAsset
        {
            Key = SeedMarker,
            ContentType = "text/plain",
            Data = System.Text.Encoding.UTF8.GetBytes(
                "Catálogo ficticio de demostración; no son registros comerciales."),
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await EnsureDemoClassificationCorrectedAsync(db, cancellationToken);
        return true;
    }

    public static async Task<bool> EnsureDemoClassificationCorrectedAsync(
        TiendaDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (!await db.BusinessAssets.AnyAsync(
                item => item.Key == SeedMarker || item.Key == DemoDataSeeder.SeedMarker,
                cancellationToken))
        {
            return false;
        }
        await EnsureClassificationCorrectedAsync(db, cancellationToken);
        return true;
    }

    private static async Task EnsureClassificationCorrectedAsync(
        TiendaDbContext db,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var products = await db.Products.ToDictionaryAsync(item => item.Sku, cancellationToken);
        var details = await db.ProductDetails.ToDictionaryAsync(item => item.ProductId, cancellationToken);
        SetDepartment("DEMO-X-ARROZ", ProductDepartment.RawIngredient);
        SetDepartment("DEMO-X-ACEITE", ProductDepartment.RawIngredient);
        SetDepartment("DEMO-X-CARNEKG", ProductDepartment.RawIngredient);
        SetDepartment("DEMO-X-WAFER", ProductDepartment.Retail);
        SetDepartment("DEMO-X-DETERGENTE", ProductDepartment.Retail);
        SetDepartment("DEMO-X-BOLSA", ProductDepartment.Retail);
        SetDepartment("DEMO-X-FOTOCOPIA", ProductDepartment.Service);

        if (products.TryGetValue("COM-001", out var asadito))
        {
            var finishedStock = asadito.StockItemId is Guid existingStockId
                ? await db.StockItems.SingleOrDefaultAsync(
                    item => item.Id == existingStockId, cancellationToken)
                : null;
            if (finishedStock is null)
            {
                finishedStock = await db.StockItems.SingleOrDefaultAsync(
                    item => item.Name == "Asadito congelado", cancellationToken);
            }
            if (finishedStock is null)
            {
                finishedStock = await new CommerceService(db).CreateStockItemAsync(
                    new CreateStockItemRequest("Asadito congelado", "unidad", 0m, 10m, 0m),
                    Actor,
                    cancellationToken);
            }
            asadito.IsRecipe = false;
            asadito.StockItemId = finishedStock.Id;
            asadito.Name = "Asadito congelado";
            var asaditoComponents = await db.RecipeComponents
                .Where(item => item.ProductId == asadito.Id).ToListAsync(cancellationToken);
            db.RecipeComponents.RemoveRange(asaditoComponents);
            if (details.TryGetValue(asadito.Id, out var asaditoDetails))
            {
                asaditoDetails.Department = ProductDepartment.FrozenBatch;
            }
        }

        if (products.TryGetValue("DEMO-X-EMPANADA", out var empanada))
        {
            if (empanada.IsRecipe && products.TryGetValue("DEMO-X-EMP-FROZEN", out var duplicate)
                && duplicate.StockItemId is Guid frozenStockId)
            {
                empanada.IsRecipe = false;
                empanada.StockItemId = frozenStockId;
                empanada.Name = "Empanada congelada de queso";
                var components = await db.RecipeComponents
                    .Where(item => item.ProductId == empanada.Id).ToListAsync(cancellationToken);
                db.RecipeComponents.RemoveRange(components);
                duplicate.IsActive = false;
            }
            if (details.TryGetValue(empanada.Id, out var empanadaDetails))
            {
                empanadaDetails.Department = ProductDepartment.FrozenBatch;
            }
        }
        if (products.TryGetValue("DEMO-X-EMP-FROZEN", out var frozen))
        {
            if (details.TryGetValue(frozen.Id, out var frozenDetails))
            {
                frozenDetails.Department = ProductDepartment.FrozenBatch;
            }
        }
        foreach (var product in products.Values.Where(item =>
            item.IsRecipe && item.Name.Contains("empanada", StringComparison.OrdinalIgnoreCase)))
        {
            var finishedStock = await db.StockItems.SingleOrDefaultAsync(
                item => item.Name == $"{product.Name} congelada", cancellationToken);
            if (finishedStock is null)
            {
                finishedStock = await new CommerceService(db).CreateStockItemAsync(
                    new CreateStockItemRequest($"{product.Name} congelada", "unidad", 0m, 5m, 0m),
                    Actor,
                    cancellationToken);
            }
            product.IsRecipe = false;
            product.StockItemId = finishedStock.Id;
            if (details.TryGetValue(product.Id, out var productDetails))
            {
                productDetails.Department = ProductDepartment.FrozenBatch;
            }
            var components = await db.RecipeComponents
                .Where(item => item.ProductId == product.Id).ToListAsync(cancellationToken);
            db.RecipeComponents.RemoveRange(components);
        }

        if (products.TryGetValue("DEMO-X-RECARGA10", out var recharge)
            && recharge.StockItemId is Guid rechargeStockId)
        {
            recharge.StockItemId = null;
            if (await CanRemoveDemoRechargeStockAsync(db, rechargeStockId, cancellationToken))
            {
                var movements = await db.StockMovements
                    .Where(item => item.StockItemId == rechargeStockId).ToListAsync(cancellationToken);
                var lots = await db.StockLots
                    .Where(item => item.StockItemId == rechargeStockId).ToListAsync(cancellationToken);
                db.StockMovements.RemoveRange(movements);
                db.StockLots.RemoveRange(lots);
                var rechargeStock = await db.StockItems.SingleAsync(
                    item => item.Id == rechargeStockId, cancellationToken);
                db.StockItems.Remove(rechargeStock);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        void SetDepartment(string sku, ProductDepartment department)
        {
            if (products.TryGetValue(sku, out var product)
                && details.TryGetValue(product.Id, out var productDetails))
            {
                productDetails.Department = department;
            }
        }
    }

    private static async Task<bool> CanRemoveDemoRechargeStockAsync(
        TiendaDbContext db,
        Guid stockItemId,
        CancellationToken cancellationToken) =>
        !await db.PurchaseLines.AnyAsync(item => item.StockItemId == stockItemId, cancellationToken)
        && !await db.RecipeComponents.AnyAsync(item => item.StockItemId == stockItemId, cancellationToken)
        && !await db.StockProductionLines.AnyAsync(item => item.StockItemId == stockItemId, cancellationToken)
        && !await db.StockProductionBatches.AnyAsync(
            item => item.FinishedStockItemId == stockItemId, cancellationToken)
        && !await db.StockCountLines.AnyAsync(item => item.StockItemId == stockItemId, cancellationToken)
        && !await db.SupplierStockItems.AnyAsync(item => item.StockItemId == stockItemId, cancellationToken);

    public static async Task<bool> EnsureLotsSeededAsync(
        TiendaDbContext db,
        CancellationToken cancellationToken = default)
    {
        const string markerKey = "demo-stock-lots-v1";
        if (await db.BusinessAssets.AnyAsync(item => item.Key == markerKey, cancellationToken))
        {
            return false;
        }
        if (!await db.BusinessAssets.AnyAsync(item => item.Key == SeedMarker, cancellationToken))
        {
            throw new InvalidOperationException("Primero carga el catálogo de demostración extendido.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var receivedAt = DateTime.UtcNow;
        foreach (var lot in new[]
        {
            (Name: "Arroz grano largo 1 kg", Code: "DEMO-RICE-01", ExpiryDays: 365),
            (Name: "Aceite vegetal 900 ml", Code: "DEMO-OIL-01", ExpiryDays: 270),
            (Name: "Galleta wafer", Code: "DEMO-WAFER-01", ExpiryDays: 120),
            (Name: "Empanada congelada de queso", Code: "DEMO-FROZEN-01", ExpiryDays: 60),
            (Name: "Carne de res para venta", Code: "DEMO-BEEF-01", ExpiryDays: 5),
            (Name: "Queso para recetas", Code: "DEMO-CHEESE-01", ExpiryDays: 7)
        })
        {
            var item = await db.StockItems.SingleAsync(stock => stock.Name == lot.Name, cancellationToken);
            if (item.OnHand <= 0m || await db.StockLots.AnyAsync(
                    existing => existing.StockItemId == item.Id, cancellationToken))
            {
                throw new InvalidOperationException($"No se puede preparar el lote demo de {item.Name}.");
            }
            db.StockLots.Add(new StockLot
            {
                Id = Guid.NewGuid(),
                StockItemId = item.Id,
                LotCode = lot.Code,
                ReceivedQuantity = item.OnHand,
                RemainingQuantity = item.OnHand,
                UnitCost = item.UnitCost,
                ReceivedAtUtc = receivedAt,
                ExpiresAtUtc = receivedAt.AddDays(lot.ExpiryDays)
            });
        }

        db.BusinessAssets.Add(new BusinessAsset
        {
            Key = markerKey,
            ContentType = "text/plain",
            Data = System.Text.Encoding.UTF8.GetBytes("Lotes ficticios de demostración."),
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
