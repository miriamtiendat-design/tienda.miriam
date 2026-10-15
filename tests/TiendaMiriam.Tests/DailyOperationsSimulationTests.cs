using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TiendaMiriam.Tests;

public sealed class DailyOperationsSimulationTests(ITestOutputHelper output)
{
    private const string TerminalId = "SIMULATION-01";
    private const string Actor = "simulation-owner";

    [Fact]
    public async Task SimulateNormalBusinessDayAndReportStockCashQrAndProfit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var cashRegister = new CashRegisterService(db);
        var session = await cashRegister.OpenAsync(TerminalId, Actor, 500m);
        var commerce = new CommerceService(db);

        var meat = await CreateStockAsync(commerce, "Carne", "kg", 30m, 10m);
        var salt = await CreateStockAsync(commerce, "Sal", "kg", 2m, 2m);
        var seasoning = await CreateStockAsync(commerce, "Condimento", "kg", 1m, 2m);
        var flour = await CreateStockAsync(commerce, "Harina", "kg", 10m, 4m);
        var cheese = await CreateStockAsync(commerce, "Queso", "kg", 5m, 25m);
        var potato = await CreateStockAsync(commerce, "Papa", "kg", 5m, 3m);
        var sausage = await CreateStockAsync(commerce, "Salchicha", "kg", 3m, 8m);
        var asaditoStock = await CreateStockAsync(commerce, "Asadito congelado", "unidad", 0m, 0m);
        var empanadaStock = await CreateStockAsync(commerce, "Empanada congelada", "unidad", 0m, 0m);
        var drinkStock = await CreateStockAsync(commerce, "Bebida", "unidad", 50m, 1.25m);

        var salchipapa = await commerce.CreateProductAsync(
            new CreateProductRequest(
                "SIM-SALCHIPAPA", "Salchipapa", 20m, true, null, ProductDepartment.PreparedDirect,
                [
                    new RecipeComponentInput(potato.Id, 0.15m),
                    new RecipeComponentInput(sausage.Id, 0.08m),
                    new RecipeComponentInput(salt.Id, 0.002m)
                ]),
            Actor,
            CancellationToken.None);
        var asadito = await commerce.CreateProductAsync(
            new CreateProductRequest(
                "SIM-ASADITO", "Asadito Congelado", 5m, false, asaditoStock.Id,
                ProductDepartment.FrozenBatch, []),
            Actor,
            CancellationToken.None);
        var empanada = await commerce.CreateProductAsync(
            new CreateProductRequest(
                "SIM-EMPANADA", "Empanada Congelada", 6m, false, empanadaStock.Id,
                ProductDepartment.FrozenBatch, []),
            Actor,
            CancellationToken.None);
        var drink = await commerce.CreateProductAsync(
            new CreateProductRequest(
                "SIM-BEBIDA", "Bebida", 3m, false, drinkStock.Id,
                ProductDepartment.Retail, []),
            Actor,
            CancellationToken.None);

        var production = new ProductionService(db);
        await production.RecordProductionAsync(
            new RecordProductionRequest(
                Guid.NewGuid(),
                asadito.Id,
                80m,
                [
                    new ProductionIngredientInput(meat.Id, 10m),
                    new ProductionIngredientInput(salt.Id, 1m),
                    new ProductionIngredientInput(seasoning.Id, 0.5m)
                ]),
            Actor,
            CancellationToken.None);
        await production.RecordProductionAsync(
            new RecordProductionRequest(
                Guid.NewGuid(),
                empanada.Id,
                50m,
                [
                    new ProductionIngredientInput(flour.Id, 5m),
                    new ProductionIngredientInput(cheese.Id, 2m),
                    new ProductionIngredientInput(meat.Id, 1m)
                ]),
            Actor,
            CancellationToken.None);

        var plannedSales = Enumerable.Repeat(salchipapa, 20)
            .Concat(Enumerable.Repeat(asadito, 15))
            .Concat(Enumerable.Repeat(empanada, 10))
            .Concat(Enumerable.Repeat(drink, 5))
            .ToArray();
        var random = new Random(20261010);
        for (var index = plannedSales.Length - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (plannedSales[index], plannedSales[swapIndex]) = (plannedSales[swapIndex], plannedSales[index]);
        }

        var saleService = new SaleService(db);
        var cashSaleCount = 0;
        var qrSaleCount = 0;
        var notedSaleCount = 0;
        var expectedCashSales = 0m;
        var expectedQrSales = 0m;
        var notes = new[] { "sin cebolla", "bien cocido" };
        for (var index = 0; index < plannedSales.Length; index++)
        {
            var product = plannedSales[index];
            var method = index % 2 == 0 ? PaymentMethod.Cash : PaymentMethod.Transfer;
            var note = index % 5 == 0 ? notes[(index / 5) % notes.Length] : null;
            await saleService.CreateAsync(
                new CreateSaleRequest(
                    Guid.NewGuid(),
                    TerminalId,
                    [new SaleLineRequest(product.Id, 1m, product.Price, Notes: note)],
                    method,
                    product.Price,
                    DateTimeOffset.UtcNow,
                    CashRegisterSessionId: session.Id),
                true,
                CancellationToken.None);

            if (method == PaymentMethod.Cash)
            {
                cashSaleCount++;
                expectedCashSales += product.Price;
            }
            else
            {
                qrSaleCount++;
                expectedQrSales += product.Price;
            }
            if (note is not null)
            {
                notedSaleCount++;
            }
        }

        var cashSummary = await cashRegister.GetOpenSummaryAsync(TerminalId);
        Assert.NotNull(cashSummary);
        var closeResult = await cashRegister.CloseAsync(
            session.Id, TerminalId, Actor, cashSummary.ExpectedCash, "Simulación diaria completada.");
        var reportFrom = new DateTimeOffset(session.OpenedAtUtc.AddMinutes(-1), TimeSpan.Zero);
        var reportTo = DateTimeOffset.UtcNow.AddMinutes(1);
        var report = await commerce.GetReportAsync(reportFrom, reportTo, CancellationToken.None);

        var remainingStocks = new Dictionary<string, decimal>
        {
            ["Carne (kg)"] = await GetStockOnHandAsync(db, meat.Id),
            ["Sal (kg)"] = await GetStockOnHandAsync(db, salt.Id),
            ["Condimento (kg)"] = await GetStockOnHandAsync(db, seasoning.Id),
            ["Harina (kg)"] = await GetStockOnHandAsync(db, flour.Id),
            ["Queso (kg)"] = await GetStockOnHandAsync(db, cheese.Id),
            ["Papa (kg)"] = await GetStockOnHandAsync(db, potato.Id),
            ["Salchicha (kg)"] = await GetStockOnHandAsync(db, sausage.Id),
            ["Bebidas (unidad)"] = await GetStockOnHandAsync(db, drinkStock.Id)
        };
        var remainingFinishedGoods = new Dictionary<string, decimal>
        {
            ["Asaditos congelados"] = await GetStockOnHandAsync(db, asaditoStock.Id),
            ["Empanadas congeladas"] = await GetStockOnHandAsync(db, empanadaStock.Id)
        };
        var inventoryMatchesExpected = remainingStocks["Carne (kg)"] == 19m
            && remainingStocks["Sal (kg)"] == 0.96m
            && remainingStocks["Condimento (kg)"] == 0.5m
            && remainingStocks["Harina (kg)"] == 5m
            && remainingStocks["Queso (kg)"] == 3m
            && remainingStocks["Papa (kg)"] == 2m
            && remainingStocks["Salchicha (kg)"] == 1.4m
            && remainingStocks["Bebidas (unidad)"] == 45m
            && remainingFinishedGoods["Asaditos congelados"] == 65m
            && remainingFinishedGoods["Empanadas congeladas"] == 40m;
        var netProfitReconciles = report.NetProfit is decimal netProfit
            && report.SalesWithoutCostSnapshot == 0
            && netProfit == report.Revenue - report.CostOfGoods - report.Expenses;

        output.WriteLine("SIMULACIÓN DE DÍA NORMAL");
        output.WriteLine($"Ventas: {report.CompletedSales} (efectivo: {cashSaleCount}; QR: {qrSaleCount}; con nota: {notedSaleCount})");
        output.WriteLine("Stock restante de insumos y tienda:");
        foreach (var (name, quantity) in remainingStocks)
        {
            output.WriteLine($"  {name}: {quantity:0.######}");
        }
        output.WriteLine("Stock restante de productos congelados:");
        foreach (var (name, quantity) in remainingFinishedGoods)
        {
            output.WriteLine($"  {name}: {quantity:0.######} unidades");
        }
        output.WriteLine($"Efectivo esperado en caja: {closeResult.ExpectedCash:0.00} Bs");
        output.WriteLine($"QR recibido: {closeResult.QrReceived:0.00} Bs");
        output.WriteLine($"Inventario descontado según lo esperado: {inventoryMatchesExpected}");
        output.WriteLine(
            $"Ingresos: {report.Revenue:0.00} Bs · Costos: {report.CostOfGoods:0.00} Bs · Gastos: {report.Expenses:0.00} Bs · Utilidad neta: {report.NetProfit:0.00} Bs");
        output.WriteLine($"Utilidad neta cuadra y costos completos: {netProfitReconciles}");

        Assert.Equal(50, report.CompletedSales);
        Assert.Equal(25, cashSaleCount);
        Assert.Equal(25, qrSaleCount);
        Assert.True(notedSaleCount >= 10);
        Assert.Equal(0m, closeResult.Variance);
        Assert.Equal(500m + expectedCashSales, closeResult.ExpectedCash);
        Assert.Equal(expectedQrSales, closeResult.QrReceived);
        Assert.Equal(550m, report.Revenue);
        Assert.Equal(0m, report.Expenses);
        Assert.Equal(report.Revenue - report.CostOfGoods, report.NetProfit);
        Assert.True(inventoryMatchesExpected);
        Assert.True(netProfitReconciles);
    }

    private static Task<StockItem> CreateStockAsync(
        CommerceService commerce, string name, string unit, decimal openingStock, decimal unitCost) =>
        commerce.CreateStockItemAsync(
            new CreateStockItemRequest(name, unit, openingStock, 0m, unitCost),
            Actor,
            CancellationToken.None);

    private static Task<decimal> GetStockOnHandAsync(TiendaDbContext db, Guid stockId) =>
        db.StockItems.AsNoTracking()
            .Where(item => item.Id == stockId)
            .Select(item => item.OnHand)
            .SingleAsync();
}
