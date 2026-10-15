using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TiendaMiriam.Tests;

public sealed class ExtremeCasesSimulationTests(ITestOutputHelper output)
{
    private const string Actor = "extreme-case-simulation";

    [Fact]
    public async Task RunExtremeCasesSequentiallyAndReportObservedBehavior()
    {
        var results = new[]
        {
            await SellRecipeWhenIngredientIsMissingAsync(),
            await CloseCashWithLargeVarianceAsync(),
            await ReceivePurchaseWithoutOpenCashRegisterAsync(),
            await SimulateCloudOutageAndLaterSyncAsync(),
            await CompeteForSameDiningTableAsync(),
            await ProduceWithZeroIngredientAsync(),
            await ProduceBeyondAvailableIngredientStockAsync()
        };

        output.WriteLine("SIMULACIÓN DE CASOS EXTREMOS (SQLite en memoria)");
        for (var index = 0; index < results.Length; index++)
        {
            output.WriteLine($"{index + 1}. {results[index].Name}: {results[index].Details}");
        }
        output.WriteLine($"Escenarios que pasaron sus comprobaciones: {results.Count(item => item.Passed)}/{results.Length}");

        Assert.All(results, result => Assert.True(result.Passed, result.Name + ": " + result.Details));
    }

    private static async Task<CaseResult> SellRecipeWhenIngredientIsMissingAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseAsync(connection);
        var commerce = new CommerceService(db);
        var potato = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Papa", "kg", 0m, 0m, 3m), Actor, CancellationToken.None);
        var product = await commerce.CreateProductAsync(
            new CreateProductRequest(
                "EXT-SALCHIPAPA", "Salchipapa", 20m, true, null, ProductDepartment.PreparedFood,
                [new RecipeComponentInput(potato.Id, 0.15m)]),
            Actor,
            CancellationToken.None);
        var request = new CreateSaleRequest(
            Guid.NewGuid(), "EXT-CAJA-1",
            [new SaleLineRequest(product.Id, 1m, product.Price)],
            PaymentMethod.Cash, product.Price, DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaleService(db).CreateAsync(request, true, CancellationToken.None));
        var unsaved = !await db.Sales.AnyAsync(item => item.Id == request.SaleId)
            && !await db.SyncOutbox.AnyAsync(item => item.SaleId == request.SaleId);
        return new CaseResult(
            "Venta de receta sin papa",
            $"RECHAZADA: {error.Message} No se guardó la venta ni se encoló para sincronización.",
            unsaved && error.Message.Contains("Stock insuficiente", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<CaseResult> CloseCashWithLargeVarianceAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseAsync(connection);
        var cash = new CashRegisterService(db);
        var session = await cash.OpenAsync("EXT-CAJA-DIF", Actor, 1000m);
        var result = await cash.CloseAsync(session.Id, "EXT-CAJA-DIF", Actor, 800m, "Prueba de diferencia grande.");
        var savedSession = await db.CashRegisterSessions.AsNoTracking().SingleAsync(item => item.Id == session.Id);
        var wasAcceptedAndRecorded = result.ExpectedCash == 1000m
            && result.CountedCash == 800m
            && result.Variance == -200m
            && savedSession.ClosedAtUtc is not null
            && savedSession.Variance == -200m;
        return new CaseResult(
            "Cierre con diferencia de 200 Bs",
            $"ACEPTADO y registrado: esperado {result.ExpectedCash:0.00} Bs, contado {result.CountedCash:0.00} Bs, diferencia {result.Variance:0.00} Bs. En la pantalla se exige una observación y confirmación explícita; no bloquea el cierre si se confirman.",
            wasAcceptedAndRecorded);
    }

    private static async Task<CaseResult> ReceivePurchaseWithoutOpenCashRegisterAsync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseAsync(connection);
        var commerce = new CommerceService(db);
        var stock = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Harina", "kg", 2m, 0m, 4m), Actor, CancellationToken.None);
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = "Proveedor de prueba",
            Phone = "70000000",
            Address = "Dirección de prueba",
            ContactName = "Contacto de prueba",
            IsActive = true
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var receivedAt = DateTimeOffset.UtcNow;
        var cashRequest = new ReceivePurchaseRequest(
            supplier.Id,
            "EXT-CASH-NO-REGISTER",
            receivedAt,
            [new PurchaseLineRequest(stock.Id, 1m, 4m)],
            PaymentMethod.Cash);
        var cashError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commerce.ReceivePurchaseAsync(cashRequest, Actor, CancellationToken.None));
        var cashPurchaseWasRejected = !await db.PurchaseOrders.AnyAsync(item =>
            item.InvoiceReference == cashRequest.InvoiceReference);

        var creditRequest = new ReceivePurchaseRequest(
            supplier.Id,
            "EXT-CREDIT-NO-REGISTER",
            receivedAt,
            [new PurchaseLineRequest(stock.Id, 1m, 4m)],
            PaymentMethod.Credit);
        await commerce.ReceivePurchaseAsync(creditRequest, Actor, CancellationToken.None);
        var creditOrder = await db.PurchaseOrders.SingleAsync(item =>
            item.InvoiceReference == creditRequest.InvoiceReference);
        var creditWasAllowedAsDebt = creditOrder.CashRegisterSessionId is null
            && creditOrder.AmountPaid == 0m
            && await db.Expenses.AllAsync(expense => expense.PurchaseOrderId != creditOrder.Id);

        return new CaseResult(
            "Compra de proveedor sin caja abierta",
            $"Compra pagada en efectivo: RECHAZADA ({cashError.Message}); no dejó compra registrada. Compra a crédito: PERMITIDA como deuda sin sesión de caja ni gasto inmediato.",
            cashPurchaseWasRejected && creditWasAllowedAsDebt);
    }

    private static async Task<CaseResult> SimulateCloudOutageAndLaterSyncAsync()
    {
        await using var localConnection = new SqliteConnection("Data Source=:memory:");
        await localConnection.OpenAsync();
        await using var local = await CreateDatabaseAsync(localConnection);
        var localStock = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Agua", Unit = "unidad", OnHand = 3m, UnitCost = 1m
        };
        var localProduct = new Product
        {
            Id = Guid.NewGuid(), Sku = "EXT-AGUA", Name = "Agua", Price = 5m, StockItemId = localStock.Id
        };
        local.AddRange(localStock, localProduct);
        await local.SaveChangesAsync();

        var request = new CreateSaleRequest(
            Guid.NewGuid(), "EXT-CAJA-OFFLINE",
            [new SaleLineRequest(localProduct.Id, 1m, localProduct.Price)],
            PaymentMethod.Cash, localProduct.Price, DateTimeOffset.UtcNow);
        await new SaleService(local).CreateAsync(request, true, CancellationToken.None);
        var outboxItem = await local.SyncOutbox.SingleAsync(item => item.SaleId == request.SaleId);
        var localSaleSaved = await local.Sales.AnyAsync(item => item.Id == request.SaleId)
            && await local.StockItems.Where(item => item.Id == localStock.Id)
                .Select(item => item.OnHand).SingleAsync() == 2m;
        var remainedQueuedDuringOutage = !outboxItem.IsDelivered;

        await using var cloudConnection = new SqliteConnection("Data Source=:memory:");
        await cloudConnection.OpenAsync();
        await using var cloud = await CreateDatabaseAsync(cloudConnection);
        cloud.Add(new StockItem
        {
            Id = localStock.Id, Name = localStock.Name, Unit = localStock.Unit,
            OnHand = 3m, UnitCost = localStock.UnitCost
        });
        cloud.Add(new Product
        {
            Id = localProduct.Id, Sku = localProduct.Sku, Name = localProduct.Name,
            Price = localProduct.Price, StockItemId = localStock.Id
        });
        await cloud.SaveChangesAsync();
        var queuedRequest = System.Text.Json.JsonSerializer.Deserialize<CreateSaleRequest>(outboxItem.Payload)
            ?? throw new InvalidOperationException("La venta offline no pudo leerse de la outbox.");
        await new SaleService(cloud).CreateAsync(queuedRequest, false, CancellationToken.None);
        outboxItem.IsDelivered = true;
        await local.SaveChangesAsync();

        var deliveredAfterRecovery = outboxItem.IsDelivered
            && await cloud.Sales.AnyAsync(item => item.Id == request.SaleId)
            && await cloud.StockItems.Where(item => item.Id == localStock.Id)
                .Select(item => item.OnHand).SingleAsync() == 2m;
        return new CaseResult(
            "Corte de nube durante una venta",
            $"LOCAL: venta guardada y stock bajó a 2; la outbox quedó pendiente durante la caída. TRAS SIMULAR RECUPERACIÓN: reproducción aceptada por una segunda SQLite y elemento marcado entregado. No se hizo una conexión de red a una nube real.",
            localSaleSaved && remainedQueuedDuringOutage && deliveredAfterRecovery);
    }

    private static async Task<CaseResult> CompeteForSameDiningTableAsync()
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = $"mesa-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using (var setup = new TiendaDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(setup);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ClaimResult> ClaimAsync(string terminal, Guid id)
        {
            await gate.Task;
            await using var db = new TiendaDbContext(options);
            try
            {
                await new CommerceService(db).SaveHeldSaleAsync(
                    new HoldSaleRequest(id, terminal, "[]", "Mesa 5"),
                    CancellationToken.None);
                return new ClaimResult(true, "Orden guardada.");
            }
            catch (InvalidOperationException exception)
            {
                return new ClaimResult(false, exception.Message);
            }
        }

        var claims = new[]
        {
            ClaimAsync("CAJERO-A", Guid.NewGuid()),
            ClaimAsync("CAJERO-B", Guid.NewGuid())
        };
        gate.SetResult();
        var results = await Task.WhenAll(claims);
        await using var verify = new TiendaDbContext(options);
        var mesaClaims = await verify.HeldSales.CountAsync(item => item.DiningTable == "Mesa 5");
        var winners = results.Count(result => result.Accepted);
        var losers = results.Length - winners;
        var oneWinner = winners == 1 && losers == 1 && mesaClaims == 1;
        var loserNotice = results.SingleOrDefault(result => !result.Accepted)?.Details ?? "Sin rechazo.";
        return new CaseResult(
            "Dos cajeros reclaman Mesa 5 a la vez",
            $"Ganadores: {winners}; rechazados: {losers}; órdenes guardadas para Mesa 5: {mesaClaims}. Respuesta al perdedor: {loserNotice}",
            oneWinner);
    }

    private static async Task<CaseResult> ProduceWithZeroIngredientAsync()
    {
        var result = await AttemptProductionAsync(5m, 0m);
        return new CaseResult(
            "Producción con 0 kg de carne",
            $"RECHAZADA: {result.ErrorMessage} La carne siguió en {result.MeatOnHand:0.###} kg y no se creó lote.",
            result.Rejected && result.MeatOnHand == 5m && result.FinishedOnHand == 0m && result.BatchCount == 0);
    }

    private static async Task<CaseResult> ProduceBeyondAvailableIngredientStockAsync()
    {
        var result = await AttemptProductionAsync(5m, 12m);
        return new CaseResult(
            "Producción que requiere 12 kg cuando hay 5 kg",
            $"RECHAZADA: {result.ErrorMessage} No se descontó carne ({result.MeatOnHand:0.###} kg restantes), no se añadieron asaditos ({result.FinishedOnHand:0.###}) y no se creó lote.",
            result.Rejected && result.MeatOnHand == 5m && result.FinishedOnHand == 0m && result.BatchCount == 0);
    }

    private static async Task<ProductionAttemptResult> AttemptProductionAsync(decimal meatStock, decimal meatUsed)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDatabaseAsync(connection);
        var meat = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Carne", Unit = "kg", OnHand = meatStock, UnitCost = 10m
        };
        var finished = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Asaditos congelados", Unit = "unidad", OnHand = 0m
        };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = $"EXT-ASADITO-{Guid.NewGuid():N}"[..20],
            Name = "Asadito congelado", Price = 5m, StockItemId = finished.Id
        };
        db.AddRange(meat, finished, product);
        db.ProductDetails.Add(new ProductDetails
        {
            ProductId = product.Id,
            Department = ProductDepartment.FrozenBatch
        });
        await db.SaveChangesAsync();

        var request = new RecordProductionRequest(
            Guid.NewGuid(), product.Id, 100m, [new ProductionIngredientInput(meat.Id, meatUsed)]);
        string? errorMessage = null;
        try
        {
            await new ProductionService(db).RecordProductionAsync(request, Actor, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            errorMessage = exception.Message;
        }
        return new ProductionAttemptResult(
            errorMessage is not null,
            errorMessage ?? "La producción fue aceptada.",
            await db.StockItems.Where(item => item.Id == meat.Id).Select(item => item.OnHand).SingleAsync(),
            await db.StockItems.Where(item => item.Id == finished.Id).Select(item => item.OnHand).SingleAsync(),
            await db.StockProductionBatches.CountAsync());
    }

    private static async Task<TiendaDbContext> CreateDatabaseAsync(SqliteConnection connection)
    {
        var db = new TiendaDbContext(
            new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        return db;
    }

    private sealed record CaseResult(string Name, string Details, bool Passed);
    private sealed record ClaimResult(bool Accepted, string Details);
    private sealed record ProductionAttemptResult(
        bool Rejected,
        string ErrorMessage,
        decimal MeatOnHand,
        decimal FinishedOnHand,
        int BatchCount);
}
