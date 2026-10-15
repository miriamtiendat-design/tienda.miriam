using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TiendaMiriam.Tests;

public sealed class DemoCatalogSimulationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SeededCatalogCoversEveryDepartmentAndInventorySalesReconcile()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        Assert.True(await DemoDataSeeder.EnsureSeededAsync(db));
        Assert.True(await DemoCatalogSeeder.EnsureSeededAsync(db));
        Assert.True(await DemoCatalogSeeder.EnsureLotsSeededAsync(db));
        Assert.False(await DemoCatalogSeeder.EnsureSeededAsync(db));
        Assert.False(await DemoCatalogSeeder.EnsureLotsSeededAsync(db));

        var products = await (
            from product in db.Products.AsNoTracking()
            join details in db.ProductDetails.AsNoTracking() on product.Id equals details.ProductId
            select new { Product = product, details.Department })
            .ToDictionaryAsync(item => item.Product.Sku, item => item);
        var departments = products.Values.Select(item => item.Department).Distinct().ToHashSet();
        Assert.Equal(5, departments.Count);
        Assert.Contains(ProductDepartment.RawIngredient, departments);
        Assert.Contains(ProductDepartment.PreparedDirect, departments);
        Assert.Contains(ProductDepartment.FrozenBatch, departments);
        Assert.Contains(ProductDepartment.Retail, departments);
        Assert.Contains(ProductDepartment.Service, departments);

        var rice = products["DEMO-X-ARROZ"].Product;
        var empanada = products["DEMO-X-EMPANADA"].Product;
        var recharge = products["DEMO-X-RECARGA10"].Product;
        var photocopy = products["DEMO-X-FOTOCOPIA"].Product;
        Assert.Equal(ProductDepartment.RawIngredient, products["DEMO-X-ARROZ"].Department);
        Assert.Equal(ProductDepartment.RawIngredient, products["DEMO-X-ACEITE"].Department);
        Assert.Equal(ProductDepartment.RawIngredient, products["DEMO-X-CARNEKG"].Department);
        Assert.Equal(ProductDepartment.Retail, products["DEMO-X-DETERGENTE"].Department);
        Assert.Equal(ProductDepartment.Retail, products["DEMO-X-BOLSA"].Department);
        Assert.Equal(ProductDepartment.PreparedDirect, products["DEMO-X-POLLO"].Department);
        Assert.Equal(ProductDepartment.FrozenBatch, products["COM-001"].Department);
        Assert.Equal(ProductDepartment.Service, products["DEMO-X-RECARGA10"].Department);
        Assert.NotNull(rice.StockItemId);
        Assert.False(empanada.IsRecipe);
        Assert.NotNull(empanada.StockItemId);
        Assert.Null(recharge.StockItemId);
        Assert.Null(photocopy.StockItemId);

        var inventoryBefore = await db.StockItems.AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.OnHand);
        var flourId = await db.StockItems.Where(item => item.Name == "Harina")
            .Select(item => item.Id).SingleAsync();
        var cheeseId = await db.StockItems.Where(item => item.Name == "Queso para recetas")
            .Select(item => item.Id).SingleAsync();
        var eggId = await db.StockItems.Where(item => item.Name == "Huevo")
            .Select(item => item.Id).SingleAsync();
        var frozenStockId = empanada.StockItemId!.Value;
        var frozenLotId = await db.StockLots
            .Where(lot => lot.StockItemId == frozenStockId)
            .Select(lot => lot.Id)
            .SingleAsync();

        var cash = new CashRegisterService(db);
        var session = await cash.OpenAsync("DEMO-CATALOG-01", "demo-tester", 500m);
        var saleService = new SaleService(db);
        var sales = new[]
        {
            (Product: rice, Quantity: 2m, Method: PaymentMethod.Cash),
            (Product: empanada, Quantity: 2m, Method: PaymentMethod.Transfer),
            (Product: photocopy, Quantity: 2m, Method: PaymentMethod.Cash),
            (Product: recharge, Quantity: 1m, Method: PaymentMethod.Transfer),
            (Product: empanada, Quantity: 3m, Method: PaymentMethod.Cash)
        };
        foreach (var item in sales)
        {
            var total = item.Product.Price * item.Quantity;
            await saleService.CreateAsync(
                new CreateSaleRequest(
                    Guid.NewGuid(),
                    session.TerminalId,
                    [new SaleLineRequest(item.Product.Id, item.Quantity, item.Product.Price)],
                    item.Method,
                    total,
                    DateTimeOffset.UtcNow,
                    CashRegisterSessionId: session.Id),
                enqueueForSync: true,
                CancellationToken.None);
        }

        var inventoryAfter = await db.StockItems.AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.OnHand);
        Assert.Equal(inventoryBefore[rice.StockItemId!.Value] - 2m, inventoryAfter[rice.StockItemId.Value]);
        Assert.Equal(inventoryBefore[flourId], inventoryAfter[flourId]);
        Assert.Equal(inventoryBefore[cheeseId], inventoryAfter[cheeseId]);
        Assert.Equal(inventoryBefore[eggId], inventoryAfter[eggId]);
        Assert.Equal(inventoryBefore[frozenStockId] - 5m, inventoryAfter[frozenStockId]);
        Assert.Equal(40m, await db.StockLots.Where(lot => lot.Id == frozenLotId)
            .Select(lot => lot.RemainingQuantity).SingleAsync());

        var summary = await cash.GetOpenSummaryAsync(session.TerminalId);
        Assert.NotNull(summary);
        Assert.Equal(547m, summary.ExpectedCash);
        Assert.Equal(26m, summary.QrReceived);
        var closed = await cash.CloseAsync(
            session.Id, session.TerminalId, "demo-tester", summary.ExpectedCash, "Prueba de catálogo demo.");
        Assert.Equal(0m, closed.Variance);
        Assert.Equal(5, await db.SyncOutbox.CountAsync(item => !item.IsDelivered));

        output.WriteLine("SIMULACIÓN DE CATÁLOGO E INVENTARIO (SQLite aislada)");
        output.WriteLine($"Productos con departamento: {products.Count}; departamentos representados: {departments.Count}/5");
        output.WriteLine($"Ventas de prueba aceptadas: {sales.Length}; stock agotado de más: no");
        output.WriteLine($"Existencias: arroz -2 bolsas; insumos de empanada sin cambios; congeladas -5; servicios sin inventario.");
        output.WriteLine($"Cierre: efectivo esperado {closed.ExpectedCash:0.00} Bs; QR {closed.QrReceived:0.00} Bs; diferencia {closed.Variance:0.00} Bs");
        output.WriteLine("Resultado: catálogo idempotente, inventario descontado exactamente y todas las ventas pendientes en outbox.");
    }
}
