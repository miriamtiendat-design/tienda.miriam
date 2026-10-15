using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TiendaMiriam.Tests;

public sealed class ConcurrentSalesStressSimulationTests(ITestOutputHelper output)
{
    private const string Actor = "stress-simulation";

    [Fact]
    public async Task ConcurrentSalesAcrossThreeRegistersNeverOversellAndCloseAccurately()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"stress-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var options = new DbContextOptionsBuilder<TiendaDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Guid asaditoId;
        Guid asaditoStockId;
        Guid drinkId;
        Guid drinkStockId;
        var sessionIds = new Guid[3];
        var terminalIds = new[] { "Caja 1", "Caja 2", "Caja 3" };

        await using (var setup = new TiendaDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(setup);
            var commerce = new CommerceService(setup);
            var meat = await commerce.CreateStockItemAsync(
                new CreateStockItemRequest("Carne", "kg", 20m, 0m, 10m), Actor, CancellationToken.None);
            var asaditoStock = await commerce.CreateStockItemAsync(
                new CreateStockItemRequest("Asadito congelado", "unidad", 0m, 0m, 0m), Actor, CancellationToken.None);
            var drinkStock = await commerce.CreateStockItemAsync(
                new CreateStockItemRequest("Bebida", "unidad", 300m, 0m, 1m), Actor, CancellationToken.None);
            var asadito = await commerce.CreateProductAsync(
                new CreateProductRequest(
                    "STRESS-ASADITO", "Asadito congelado", 5m, false, asaditoStock.Id,
                    ProductDepartment.FrozenBatch, []),
                Actor,
                CancellationToken.None);
            var drink = await commerce.CreateProductAsync(
                new CreateProductRequest(
                    "STRESS-BEBIDA", "Bebida", 3m, false, drinkStock.Id,
                    ProductDepartment.Retail, []),
                Actor,
                CancellationToken.None);
            asaditoId = asadito.Id;
            asaditoStockId = asaditoStock.Id;
            drinkId = drink.Id;
            drinkStockId = drinkStock.Id;

            var production = new ProductionService(setup);
            await production.RecordProductionAsync(
                new RecordProductionRequest(
                    Guid.NewGuid(),
                    asadito.Id,
                    100m,
                    [new ProductionIngredientInput(meat.Id, 12m)]),
                Actor,
                CancellationToken.None);

            var cashRegister = new CashRegisterService(setup);
            for (var index = 0; index < terminalIds.Length; index++)
            {
                sessionIds[index] = (await cashRegister.OpenAsync(
                    terminalIds[index], Actor, 500m)).Id;
            }
        }

        var saleAttempts = new List<SaleAttempt>(300);
        for (var registerIndex = 0; registerIndex < terminalIds.Length; registerIndex++)
        {
            for (var saleIndex = 0; saleIndex < 100; saleIndex++)
            {
                var isAsadito = saleIndex < 50;
                var productId = isAsadito ? asaditoId : drinkId;
                var unitPrice = isAsadito ? 5m : 3m;
                saleAttempts.Add(new SaleAttempt(
                    registerIndex,
                    productId,
                    unitPrice,
                    Guid.NewGuid(),
                    new DateTimeOffset(DateTime.UtcNow.AddTicks(saleAttempts.Count), TimeSpan.Zero)));
            }
        }

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentAttempts = saleAttempts.Select(attempt => Task.Run(async () =>
        {
            await startGate.Task;
            return await SubmitSaleWithRetryAsync(options, attempt, terminalIds, sessionIds);
        })).ToArray();
        startGate.SetResult();
        var results = await Task.WhenAll(concurrentAttempts);

        var acceptedAsaditos = results.Count(result => result.Accepted && result.IsAsadito);
        var rejectedAsaditos = results.Count(result => !result.Accepted && result.IsAsadito);
        var acceptedDrinks = results.Count(result => result.Accepted && !result.IsAsadito);
        var completedSales = results.Count(result => result.Accepted);
        var remainingAsaditos = await GetStockOnHandAsync(options, asaditoStockId);
        var remainingDrinks = await GetStockOnHandAsync(options, drinkStockId);
        var queuedSales = await ReadSyncOutboxStateAsync(options);

        var closingResults = new List<CashSessionSummary>(terminalIds.Length);
        await using (var closingDb = new TiendaDbContext(options))
        {
            var cashRegister = new CashRegisterService(closingDb);
            for (var index = 0; index < terminalIds.Length; index++)
            {
                var openSummary = await cashRegister.GetOpenSummaryAsync(terminalIds[index])
                    ?? throw new InvalidOperationException($"No se encontró la caja abierta {terminalIds[index]}.");
                var close = await cashRegister.CloseAsync(
                    sessionIds[index],
                    terminalIds[index],
                    Actor,
                    openSummary.ExpectedCash,
                    "Cierre de simulación de estrés.");
                closingResults.Add(close);
            }
        }

        var allClosingsCorrect = closingResults.Count == 3
            && closingResults.All(result => result.Variance == 0m && result.ClosedAt is not null);
        var soldExactlyAvailableStock = acceptedAsaditos == 100 && remainingAsaditos == 0m;
        var noOversell = acceptedAsaditos <= 100 && remainingAsaditos >= 0m;
        var outboxMatchesAcceptedSales = queuedSales.Pending == completedSales && queuedSales.Delivered == 0;

        output.WriteLine("SIMULACIÓN DE ESTRÉS: VENTAS SIMULTÁNEAS");
        output.WriteLine($"Intentos de venta: {results.Length} (100 por caja)");
        output.WriteLine($"Asaditos: {acceptedAsaditos} vendidos, {rejectedAsaditos} rechazados, {remainingAsaditos} en stock");
        output.WriteLine($"Bebidas: {acceptedDrinks} vendidas; stock restante: {remainingDrinks}");
        output.WriteLine($"Ventas completadas: {completedSales}; rechazadas por falta de stock: {results.Length - completedSales}");
        foreach (var close in closingResults)
        {
            output.WriteLine(
                $"{close.TerminalId}: efectivo esperado {close.ExpectedCash:0.00} Bs, contado {close.CountedCash:0.00} Bs, diferencia {close.Variance:0.00} Bs");
        }
        output.WriteLine(
            $"Outbox local: {queuedSales.Pending} pendientes, {queuedSales.Delivered} entregadas. Sin conexión a nube en esta simulación.");
        output.WriteLine($"Descuento exacto de asaditos: {soldExactlyAvailableStock}");
        output.WriteLine($"Sobreventa: {!noOversell}");
        output.WriteLine($"Cierres correctos: {allClosingsCorrect}");
        output.WriteLine($"Estado de sincronización local consistente: {outboxMatchesAcceptedSales}");

        Assert.Equal(300, results.Length);
        Assert.Equal(100, acceptedAsaditos);
        Assert.Equal(50, rejectedAsaditos);
        Assert.Equal(150, acceptedDrinks);
        Assert.Equal(250, completedSales);
        Assert.Equal(0m, remainingAsaditos);
        Assert.Equal(150m, remainingDrinks);
        Assert.True(noOversell);
        Assert.True(allClosingsCorrect);
        Assert.True(outboxMatchesAcceptedSales);
    }

    private static async Task<SaleAttemptResult> SubmitSaleWithRetryAsync(
        DbContextOptions<TiendaDbContext> options,
        SaleAttempt attempt,
        IReadOnlyList<string> terminalIds,
        IReadOnlyList<Guid> sessionIds)
    {
        var isAsadito = attempt.UnitPrice == 5m;
        var request = new CreateSaleRequest(
            attempt.SaleId,
            terminalIds[attempt.RegisterIndex],
            [new SaleLineRequest(attempt.ProductId, 1m, attempt.UnitPrice)],
            PaymentMethod.Cash,
            attempt.UnitPrice,
            attempt.OccurredAt,
            CashRegisterSessionId: sessionIds[attempt.RegisterIndex]);

        for (var retry = 0; ; retry++)
        {
            try
            {
                await using var db = new TiendaDbContext(options);
                await new SaleService(db).CreateAsync(request, true, CancellationToken.None);
                return new SaleAttemptResult(true, isAsadito);
            }
            catch (InvalidOperationException exception) when (IsConcurrencyConflict(exception) && retry < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2 + retry * 2, 40)));
            }
            catch (Exception exception) when (IsSqliteLock(exception) && retry < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2 + retry * 2, 40)));
            }
            catch (InvalidOperationException exception) when (IsInsufficientStock(exception))
            {
                return new SaleAttemptResult(false, isAsadito);
            }
        }
    }

    private static bool IsConcurrencyConflict(InvalidOperationException exception) =>
        exception.Message.Contains("cambió durante el cobro", StringComparison.OrdinalIgnoreCase);

    private static bool IsInsufficientStock(InvalidOperationException exception) =>
        exception.Message.StartsWith("Stock insuficiente", StringComparison.OrdinalIgnoreCase)
        || exception.Message.StartsWith("Stock vencido o insuficiente", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqliteLock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<decimal> GetStockOnHandAsync(
        DbContextOptions<TiendaDbContext> options,
        Guid stockItemId)
    {
        await using var db = new TiendaDbContext(options);
        return await db.StockItems.AsNoTracking()
            .Where(item => item.Id == stockItemId)
            .Select(item => item.OnHand)
            .SingleAsync();
    }

    private static async Task<SyncOutboxState> ReadSyncOutboxStateAsync(
        DbContextOptions<TiendaDbContext> options)
    {
        await using var db = new TiendaDbContext(options);
        return new SyncOutboxState(
            await db.SyncOutbox.CountAsync(item => !item.IsDelivered),
            await db.SyncOutbox.CountAsync(item => item.IsDelivered));
    }

    private sealed record SaleAttempt(
        int RegisterIndex,
        Guid ProductId,
        decimal UnitPrice,
        Guid SaleId,
        DateTimeOffset OccurredAt);

    private sealed record SaleAttemptResult(bool Accepted, bool IsAsadito);

    private sealed record SyncOutboxState(int Pending, int Delivered);
}
