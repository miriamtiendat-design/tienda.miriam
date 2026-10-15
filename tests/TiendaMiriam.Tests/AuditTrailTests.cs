using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;

namespace TiendaMiriam.Tests;

public sealed class AuditTrailTests
{
    [Fact]
    public async Task ProductChangesAreAuditedAndAuditDeletionAttemptsAreRecorded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        AuditExecutionContext.Set("admin-test", "terminal-test");

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Salchipapa de prueba",
            Sku = "AUDIT-TEST-1",
            Price = 12m
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        AuditExecutionContext.SetObservation("Ajuste de precio autorizado.");
        product.Price = 15m;
        await db.SaveChangesAsync();

        var priceChange = await db.AuditEntries
            .SingleAsync(entry => entry.Action == "PRODUCT_PRICE_CHANGED");
        Assert.Equal("admin-test", priceChange.Actor);
        Assert.Equal("terminal-test", priceChange.TerminalId);
        Assert.Equal("12", priceChange.PreviousValue);
        Assert.Equal("15", priceChange.NewValue);
        Assert.Equal("Ajuste de precio autorizado.", priceChange.Observation);
        Assert.Contains(await db.AuditOutbox.ToListAsync(), item => item.AuditEntryId == priceChange.Id);

        db.AuditEntries.Remove(priceChange);
        await db.SaveChangesAsync();
        Assert.True(await db.AuditEntries.AnyAsync(entry => entry.Id == priceChange.Id));
        Assert.Contains(await db.AuditEntries.ToListAsync(), entry => entry.Action == "AUDIT_TAMPER_ATTEMPT");
    }

    [Fact]
    public async Task RetentionDeletesExpiredDeliveredEntriesButPreservesPendingSync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        AuditExecutionContext.Set("admin-test", "terminal-test");
        var deliveredId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var expiredAt = DateTime.UtcNow.AddDays(-31);
        db.AuditEntries.AddRange(
            new AuditEntry
            {
                Id = deliveredId,
                Actor = "admin-test",
                Action = "PRODUCT_UPDATED",
                EntityType = "Product",
                EntityId = Guid.NewGuid(),
                Reason = "expired delivered",
                OccurredAtUtc = expiredAt
            },
            new AuditEntry
            {
                Id = pendingId,
                Actor = "admin-test",
                Action = "PRODUCT_UPDATED",
                EntityType = "Product",
                EntityId = Guid.NewGuid(),
                Reason = "expired pending",
                OccurredAtUtc = expiredAt
            });
        db.AuditOutbox.AddRange(
            new AuditOutboxItem
            {
                Id = Guid.NewGuid(),
                AuditEntryId = deliveredId,
                Payload = "{}",
                CreatedAtUtc = expiredAt,
                IsDelivered = true
            },
            new AuditOutboxItem
            {
                Id = Guid.NewGuid(),
                AuditEntryId = pendingId,
                Payload = "{}",
                CreatedAtUtc = expiredAt,
                IsDelivered = false
            });
        await db.SaveChangesAsync();
        await AuditRetentionService.SaveRetentionDaysAsync(db, 30);
        db.ChangeTracker.Clear();

        var deleted = await AuditRetentionService.PruneExpiredAsync(db);

        Assert.Equal(1, deleted);
        Assert.False(await db.AuditEntries.AnyAsync(entry => entry.Id == deliveredId));
        Assert.True(await db.AuditEntries.AnyAsync(entry => entry.Id == pendingId));
        Assert.True(await db.AuditOutbox.AnyAsync(item => item.AuditEntryId == pendingId && !item.IsDelivered));
        Assert.Equal(30, await AuditRetentionService.GetRetentionDaysAsync(db));
    }

    [Fact]
    public async Task ManualStockAndPasswordChangesAreAuditedWithoutExposingHashes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        AuditExecutionContext.Set("admin-test", "terminal-test");
        var stock = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Papa de prueba",
            Unit = "kg",
            OnHand = 10m,
            UnitCost = 5m
        };
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "cashier-test",
            PasswordHash = "old-sensitive-hash",
            Role = AppRole.Cashier,
            AuthVersion = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.AddRange(stock, user);
        await db.SaveChangesAsync();

        stock.OnHand = 7m;
        user.PasswordHash = "new-sensitive-hash";
        await db.SaveChangesAsync();

        var stockChange = await db.AuditEntries.SingleAsync(entry => entry.Action == "STOCK_MANUAL_ADJUSTMENT");
        Assert.Equal("10", stockChange.PreviousValue);
        Assert.Equal("7", stockChange.NewValue);
        var passwordChange = await db.AuditEntries.SingleAsync(
            entry => entry.EntityType == nameof(AppUser) && entry.Action == "USER_UPDATED");
        Assert.Contains("protegido", passwordChange.NewValue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old-sensitive-hash", passwordChange.Reason);
        Assert.DoesNotContain("new-sensitive-hash", passwordChange.Reason);
    }
}
