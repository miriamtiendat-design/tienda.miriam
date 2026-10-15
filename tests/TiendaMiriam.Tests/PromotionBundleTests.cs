using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;

namespace TiendaMiriam.Tests;

public sealed class PromotionBundleTests
{
    [Fact]
    public async Task SellingBundleAtomicallyConsumesEveryComponentAndKeepsHistoricalMetricsAfterDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var colaStock = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Bebida", Unit = "unidad", OnHand = 10, UnitCost = 2
        };
        var snackStock = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Snack", Unit = "unidad", OnHand = 10, UnitCost = 2
        };
        var cola = new Product
        {
            Id = Guid.NewGuid(), Sku = "BEB-01", Name = "Gaseosa", Price = 10,
            IsActive = true, StockItemId = colaStock.Id
        };
        var snack = new Product
        {
            Id = Guid.NewGuid(), Sku = "SNK-01", Name = "Snack", Price = 8,
            IsActive = true, StockItemId = snackStock.Id
        };
        db.StockItems.AddRange(colaStock, snackStock);
        db.Products.AddRange(cola, snack);
        await db.SaveChangesAsync();

        var bundleService = new PromotionBundleService(db);
        var bundle = await bundleService.UpsertAsync(
            new CreatePromotionBundleRequest(
                "Merienda", 15,
                [new PromotionBundleComponentInput(cola.Id, 2),
                    new PromotionBundleComponentInput(snack.Id, 1)]),
            "owner",
            enqueueForSync: true,
            CancellationToken.None);
        var saleRequest = new CreateSaleRequest(
            Guid.NewGuid(),
            "POS-1",
            [new SaleLineRequest(cola.Id, 1, 15, PromotionBundleId: bundle.Id)],
            PaymentMethod.Cash,
            15,
            DateTimeOffset.UtcNow);

        var saleResult = await new SaleService(db).CreateAsync(
            saleRequest, enqueueForSync: true, CancellationToken.None);

        Assert.Equal(8, colaStock.OnHand);
        Assert.Equal(9, snackStock.OnHand);
        Assert.Equal(15, saleResult.Total);
        var snapshot = await db.PromotionBundleSaleSnapshots.SingleAsync();
        Assert.Equal(28, snapshot.RegularAmount);
        Assert.Equal(13, snapshot.DiscountAmount);
        Assert.Equal(6, (await db.SaleLineFinancialSnapshots.SingleAsync()).CostOfGoods);

        var duplicate = await new SaleService(db).CreateAsync(
            saleRequest, enqueueForSync: true, CancellationToken.None);
        Assert.True(duplicate.AlreadyProcessed);
        Assert.Equal(8, colaStock.OnHand);
        Assert.Equal(9, snackStock.OnHand);

        snackStock.OnHand = 0;
        await db.SaveChangesAsync();
        var failedSale = saleRequest with
        {
            SaleId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaleService(db).CreateAsync(failedSale, enqueueForSync: true, CancellationToken.None));
        Assert.Equal(8, colaStock.OnHand);
        Assert.Equal(0, snackStock.OnHand);

        var deleted = await bundleService.DeleteAsync(
            bundle.Id, "owner", enqueueForSync: true, CancellationToken.None);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(2, (await db.PromotionBundles.Include(item => item.Components).SingleAsync()).Components.Count);
        var report = Assert.Single(await bundleService.GetSalesReportAsync(null, null, CancellationToken.None));
        Assert.Equal(1, report.TimesSold);
        Assert.Equal(15, report.Revenue);
        Assert.Equal(13, report.DiscountGiven);
        Assert.NotNull(report.DeletedAt);
        Assert.Equal(2, await db.PromotionBundleOutbox.CountAsync());
    }
}
