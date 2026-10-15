using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;

namespace TiendaMiriam.Tests;

public sealed class CommerceWorkflowTests
{
    [Fact]
    public async Task PaymentReportIncludesCashQrAndCreditSales()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var from = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var customer = new CustomerAccount { Id = Guid.NewGuid(), Name = "Cliente fiado" };
        db.Customers.Add(customer);
        var cashSale = CreateSale(1649m, PaymentMethod.Cash, 10m);
        var qrSale = CreateSale(267m, PaymentMethod.Transfer);
        var creditSale = CreateSale(104m, PaymentMethod.Credit);
        db.Sales.AddRange(cashSale, qrSale, creditSale);
        db.CustomerLedger.Add(new CustomerLedgerEntry
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SaleId = creditSale.Id,
            Kind = CustomerLedgerKind.CreditSale,
            CreditDelta = 104m,
            Actor = "cashier",
            Description = "Venta fiada"
        });
        await db.SaveChangesAsync();

        var result = await new PaymentReportService(db).LoadAsync(from, from.AddDays(1));

        Assert.Equal(
            new[]
            {
                new PaymentMethodReportPoint("Efectivo", 1639m),
                new PaymentMethodReportPoint("QR / transferencia", 267m),
                new PaymentMethodReportPoint("Fiado", 104m)
            },
            result);

        Sale CreateSale(decimal amount, PaymentMethod method, decimal change = 0m)
        {
            var sale = new Sale
            {
                Id = Guid.NewGuid(),
                TerminalId = "REGISTER-1",
                OccurredAtUtc = from.AddHours(12).UtcDateTime,
                Total = amount - change
            };
            sale.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                SaleId = sale.Id,
                Method = method,
                Amount = amount,
                Change = change,
                OccurredAtUtc = sale.OccurredAtUtc
            });
            return sale;
        }
    }

    [Fact]
    public async Task LocalAuthenticationHashesPasswordsAndLocksRepeatedFailures()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var auth = new LocalAuthenticationService(db);
        await auth.EnsureInitialAdministratorAsync("Owner", "correct-horse-battery");

        Assert.Null(await auth.AuthenticateAsync("owner", "incorrect-password"));
        Assert.NotNull(await auth.AuthenticateAsync("OWNER", "correct-horse-battery"));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Null(await auth.AuthenticateAsync("owner", "incorrect-password"));
        }
        Assert.Null(await auth.AuthenticateAsync("owner", "correct-horse-battery"));
        var account = await db.Users.SingleAsync();
        Assert.NotNull(account.LockedUntilUtc);
        Assert.StartsWith("pbkdf2-sha256$", account.PasswordHash);
    }

    [Fact]
    public async Task CashSessionReconcilesOpeningCashTenderAndChangeAndCannotCloseTwice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var cash = new CashRegisterService(db);
        var session = await cash.OpenAsync("REGISTER-1", "cashier", 100m);

        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            TerminalId = session.TerminalId,
            OccurredAtUtc = DateTime.UtcNow,
            Total = 60m,
            CashRegisterSessionId = session.Id
        };
        sale.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            Method = PaymentMethod.Cash,
            Amount = 50m,
            Change = 10m,
            OccurredAtUtc = DateTime.UtcNow
        });
        sale.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            Method = PaymentMethod.Transfer,
            Amount = 20m,
            OccurredAtUtc = DateTime.UtcNow
        });
        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        var openSummary = await cash.GetOpenSummaryAsync("REGISTER-1");
        Assert.NotNull(openSummary);
        Assert.Equal(140m, openSummary.ExpectedCash);
        Assert.Equal(20m, openSummary.QrReceived);
        var closed = await cash.CloseAsync(session.Id, "REGISTER-1", "cashier", 142m, "Diferencia verificada");
        Assert.Equal(140m, closed.ExpectedCash);
        Assert.Equal(2m, closed.Variance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cash.CloseAsync(session.Id, "REGISTER-1", "cashier", 142m, "Segundo cierre"));
    }

    [Fact]
    public async Task CashSessionPreventsTwoOpenShiftsForSameRegister()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var cash = new CashRegisterService(db);
        await cash.OpenAsync("REGISTER-1", "admin", 0m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cash.OpenAsync("REGISTER-1", "cashier", 0m));
    }

    [Fact]
    public async Task CashSessionIncludesExternalWalletCreditAndExpenseMovementsByTenderType()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var cash = new CashRegisterService(db);
        var session = await cash.OpenAsync("REGISTER-1", "cashier", 100m);
        var commerce = new CommerceService(db);
        var customer = await commerce.CreateCustomerAsync(
            new CreateCustomerRequest("Cliente de prueba", null, 100m), CancellationToken.None);
        var customerEntity = await db.Customers.SingleAsync(item => item.Id == customer.Id);
        customerEntity.CreditBalance = 40m;
        await db.SaveChangesAsync();

        await commerce.AddWalletFundsAsync(
            customer.Id, 25m, "Recarga en efectivo", "cashier", PaymentMethod.Cash, CancellationToken.None);
        await commerce.AddWalletFundsAsync(
            customer.Id, 50m, "Recarga por QR", "cashier", PaymentMethod.Transfer, CancellationToken.None);
        await commerce.RecordCreditPaymentAsync(
            customer.Id, 30m, "Abono en efectivo", "cashier", PaymentMethod.Cash, CancellationToken.None);
        await commerce.RecordCreditPaymentAsync(
            customer.Id, 10m, "Abono por QR", "cashier", PaymentMethod.Transfer, CancellationToken.None);
        await commerce.RecordExpenseAsync(
            new CreateExpenseRequest("Compra menor", 12m, DateTimeOffset.UtcNow), "cashier", CancellationToken.None);
        await commerce.RecordExpenseAsync(
            new CreateExpenseRequest("Pago por QR", 40m, DateTimeOffset.UtcNow, PaymentMethod.Transfer),
            "cashier", CancellationToken.None);

        var summary = await cash.GetOpenSummaryAsync("REGISTER-1");

        Assert.NotNull(summary);
        Assert.Equal(25m, summary.WalletTopUpsCash);
        Assert.Equal(50m, summary.WalletTopUpsQr);
        Assert.Equal(30m, summary.CreditPaymentsCash);
        Assert.Equal(10m, summary.CreditPaymentsQr);
        Assert.Equal(12m, summary.CashExpenses);
        Assert.Equal(40m, summary.QrExpenses);
        Assert.Equal(143m, summary.ExpectedCash);
        await cash.CloseAsync(session.Id, "REGISTER-1", "cashier", 143m, null);
        Assert.True(await db.CashRegisterSessions
            .Where(item => item.Id == session.Id)
            .Select(item => item.SyncPending)
            .SingleAsync());
    }

    [Fact]
    public async Task CustomerProfilesAndCreditPaymentsAreQueuedAndAssignedToTheCorrectCashSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var commerce = new CommerceService(db);
        var customer = await commerce.CreateCustomerAsync(
            new CreateCustomerRequest(
                "Cliente local", "70000001", 200m, "Av. Central 123", "1234567",
                "Prefiere pagar los viernes"),
            CancellationToken.None,
            enqueueForSync: true);
        var customerEntity = await db.Customers.SingleAsync(item => item.Id == customer.Id);
        customerEntity.CreditBalance = 70m;
        await db.SaveChangesAsync();

        var cash = new CashRegisterService(db);
        var firstSession = await cash.OpenAsync("REGISTER-1", "cashier", 100m);
        var secondSession = await cash.OpenAsync("REGISTER-2", "cashier", 200m);
        await commerce.RecordCreditPaymentAsync(
            customer.Id, 20m, "Abono en efectivo", "cashier", PaymentMethod.Cash,
            CancellationToken.None, firstSession.Id, enqueueForSync: true);
        await commerce.RecordCreditPaymentAsync(
            customer.Id, 15m, "Abono por QR", "cashier", PaymentMethod.Transfer,
            CancellationToken.None, secondSession.Id, enqueueForSync: true);

        var firstSummary = await cash.GetOpenSummaryAsync("REGISTER-1");
        var secondSummary = await cash.GetOpenSummaryAsync("REGISTER-2");

        Assert.NotNull(firstSummary);
        Assert.Equal(20m, firstSummary.CreditPaymentsCash);
        Assert.Equal(0m, firstSummary.CreditPaymentsQr);
        Assert.Equal(120m, firstSummary.ExpectedCash);
        Assert.NotNull(secondSummary);
        Assert.Equal(0m, secondSummary.CreditPaymentsCash);
        Assert.Equal(15m, secondSummary.CreditPaymentsQr);
        Assert.Equal(200m, secondSummary.ExpectedCash);
        Assert.Equal(35m, (await db.Customers.SingleAsync(item => item.Id == customer.Id)).CreditBalance);
        Assert.Equal(1, await db.CustomerChangeOutbox.CountAsync(
            item => item.Kind == CustomerSyncChangeKind.ProfileUpsert && !item.IsDelivered));
        Assert.Equal(2, await db.CustomerChangeOutbox.CountAsync(
            item => item.Kind == CustomerSyncChangeKind.CreditPayment && !item.IsDelivered));
        var paymentChanges = await db.CustomerChangeOutbox
            .Where(item => item.Kind == CustomerSyncChangeKind.CreditPayment)
            .Select(item => item.Payload)
            .ToListAsync();
        var syncedSessionIds = paymentChanges
            .Select(payload => System.Text.Json.JsonSerializer.Deserialize<CustomerSyncChange>(payload)!
                .CreditPayment!.CashRegisterSessionId)
            .ToArray();
        Assert.Contains(firstSession.Id, syncedSessionIds);
        Assert.Contains(secondSession.Id, syncedSessionIds);
        Assert.Equal("Av. Central 123", customer.Address);
        Assert.Equal("1234567", customer.TaxId);
        Assert.Equal("Prefiere pagar los viernes", customer.Notes);
    }

    [Fact]
    public async Task SupplierPurchasesRegisterCashAndQrAsSessionExpensesButCreditDoesNotAffectCash()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var cash = new CashRegisterService(db);
        var session = await cash.OpenAsync("REGISTER-1", "cashier", 100m);
        var commerce = new CommerceService(db);
        var stock = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Harina", "kg", 0m, 0m, 1m), "owner", CancellationToken.None);
        var supplier = await commerce.CreateSupplierAsync(
            new CreateSupplierRequest("Proveedor", "123", "70000000", "Calle", "Contacto"),
            CancellationToken.None);

        foreach (var (reference, method, amount) in new[]
                 {
                     ("EF-1", PaymentMethod.Cash, 12m),
                     ("QR-1", PaymentMethod.Transfer, 8m),
                     ("CR-1", PaymentMethod.Credit, 4m)
                 })
        {
            await commerce.ReceivePurchaseAsync(
                new ReceivePurchaseRequest(
                    supplier.Id,
                    reference,
                    DateTimeOffset.UtcNow,
                    [new PurchaseLineRequest(stock.Id, 1m, amount)],
                    method,
                    method == PaymentMethod.Credit ? null : session.Id),
                "owner",
                CancellationToken.None);
        }

        var summary = await cash.GetOpenSummaryAsync("REGISTER-1");
        Assert.NotNull(summary);
        Assert.Equal(12m, summary.CashExpenses);
        Assert.Equal(8m, summary.QrExpenses);
        Assert.Equal(88m, summary.ExpectedCash);
        Assert.Equal(3, await db.PurchaseOrders.CountAsync());
        Assert.Equal(2, await db.Expenses.CountAsync());
        Assert.Equal(2, await db.Expenses.CountAsync(expense => expense.CashRegisterSessionId == session.Id));
        Assert.Equal(3m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
    }

    [Fact]
    public async Task CashAndQrPurchasesWithoutOpenRegisterReturnClearErrorWithoutPersisting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var stock = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Harina", "kg", 0m, 0m, 1m), "owner", CancellationToken.None);
        var initialStock = (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand;
        var supplier = await commerce.CreateSupplierAsync(
            new CreateSupplierRequest("Proveedor", "123", "70000000", "Calle", "Contacto"),
            CancellationToken.None);

        foreach (var method in new[] { PaymentMethod.Cash, PaymentMethod.Transfer })
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                commerce.ReceivePurchaseAsync(
                    new ReceivePurchaseRequest(
                        supplier.Id,
                        $"SIN-CAJA-{method}",
                        DateTimeOffset.UtcNow,
                        [new PurchaseLineRequest(stock.Id, 1m, 2m)],
                        method),
                    "owner",
                    CancellationToken.None));

            Assert.Equal(
                "Debes abrir una caja antes de registrar una compra en efectivo.",
                exception.Message);
        }

        Assert.Empty(await db.PurchaseOrders.ToListAsync());
        Assert.Empty(await db.Expenses.ToListAsync());
        Assert.Equal(initialStock, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
    }

    [Fact]
    public async Task SupplierCreditPaymentsSupportPartialAndFinalCashOrQrWithoutChangingInventory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var cashRegister = new CashRegisterService(db);
        var session = await cashRegister.OpenAsync("REGISTER-1", "cashier", 100m);
        var commerce = new CommerceService(db);
        var stock = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Papa", "kg", 0m, 0m, 1m), "owner", CancellationToken.None);
        var supplier = await commerce.CreateSupplierAsync(
            new CreateSupplierRequest("Proveedor", "123", "70000000", "Calle", "Contacto"),
            CancellationToken.None);
        var receivedAt = DateTimeOffset.UtcNow;
        var dueAt = receivedAt.AddDays(30);
        var purchase = await commerce.ReceivePurchaseAsync(
            new ReceivePurchaseRequest(
                supplier.Id,
                "CRED-1",
                receivedAt,
                [new PurchaseLineRequest(stock.Id, 10m, 2m)],
                PaymentMethod.Credit,
                DueAt: dueAt),
            "owner",
            CancellationToken.None);
        var purchaseId = await db.PurchaseOrders
            .Where(item => item.Id == purchase.Id)
            .Select(item => item.Id)
            .SingleAsync();
        Assert.Equal(0m, await db.PurchaseOrders
            .Where(item => item.Id == purchaseId)
            .Select(item => item.AmountPaid)
            .SingleAsync());
        Assert.Equal(dueAt.UtcDateTime, await db.PurchaseOrders
            .Where(item => item.Id == purchaseId)
            .Select(item => item.DueAtUtc)
            .SingleAsync());

        var partial = await commerce.RecordSupplierPurchasePaymentAsync(
            new SupplierPurchasePaymentRequest(purchaseId, 5m, PaymentMethod.Cash, session.Id, "primer abono"),
            "owner",
            CancellationToken.None);
        Assert.Equal(5m, partial.AmountPaid);
        Assert.Equal(15m, partial.OutstandingBalance);
        var final = await commerce.RecordSupplierPurchasePaymentAsync(
            new SupplierPurchasePaymentRequest(purchaseId, 15m, PaymentMethod.Transfer, session.Id, "saldo final"),
            "owner",
            CancellationToken.None);
        Assert.Equal(20m, final.AmountPaid);
        Assert.Equal(0m, final.OutstandingBalance);
        Assert.Equal(2, await db.SupplierPayments.CountAsync(item => item.PurchaseOrderId == purchaseId));

        var summary = await cashRegister.GetOpenSummaryAsync("REGISTER-1");
        Assert.NotNull(summary);
        Assert.Equal(5m, summary.CashExpenses);
        Assert.Equal(15m, summary.QrExpenses);
        Assert.Equal(95m, summary.ExpectedCash);
        Assert.Equal(10m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
        await Assert.ThrowsAsync<InvalidOperationException>(() => commerce.RecordSupplierPurchasePaymentAsync(
            new SupplierPurchasePaymentRequest(purchaseId, 1m, PaymentMethod.Cash, session.Id),
            "owner",
            CancellationToken.None));
    }

    [Fact]
    public async Task SupplierSchemaUpgradeAddsCreditDueDateToExistingPurchaseTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        await db.Database.ExecuteSqlRawAsync(
            """DROP INDEX "IX_PurchaseOrders_SupplierId_DueAtUtc"; ALTER TABLE "PurchaseOrders" DROP COLUMN "DueAtUtc";""");
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var columns = await db.Database.SqlQueryRaw<string>(
            """SELECT name AS Value FROM pragma_table_info('PurchaseOrders') WHERE name = 'DueAtUtc'""")
            .ToListAsync();
        Assert.Contains("DueAtUtc", columns);
    }

    [Fact]
    public async Task SharedTableOrdersUseOptimisticVersionsAndRejectDuplicateTableClaims()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var orderId = Guid.NewGuid();
        const string originalPayload = """[{"ProductId":"73d11cd6-2d4f-4b24-9e55-8d19cbaae8c1","Quantity":1,"UnitPriceAtSale":12.5}]""";
        var created = await commerce.SaveHeldSaleAsync(
            new HoldSaleRequest(orderId, "REGISTER-1", originalPayload, "Mesa 1"), CancellationToken.None);
        Assert.Equal(1, created.Version);
        Assert.NotNull(created.StartedAt);
        Assert.False(created.CheckoutRequested);

        var updated = await commerce.SaveHeldSaleAsync(
            new HoldSaleRequest(
                orderId, "REGISTER-2", originalPayload, "Mesa 1", NamedOrder: "Familia",
                ExpectedVersion: 1, StartedAt: created.StartedAt, CheckoutRequested: true),
            CancellationToken.None);
        Assert.Equal(2, updated.Version);
        Assert.Equal("REGISTER-2", updated.TerminalId);
        Assert.Equal(created.StartedAt, updated.StartedAt);
        Assert.True(updated.CheckoutRequested);

        await Assert.ThrowsAsync<InvalidOperationException>(() => commerce.SaveHeldSaleAsync(
            new HoldSaleRequest(orderId, "REGISTER-1", originalPayload, "Mesa 1", ExpectedVersion: 1),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commerce.SaveHeldSaleAsync(
            new HoldSaleRequest(Guid.NewGuid(), "REGISTER-3", originalPayload, "Mesa 1"),
            CancellationToken.None));

        var sharedForAnotherRegister = await commerce.GetHeldSalesAsync("REGISTER-3", CancellationToken.None);
        Assert.Single(sharedForAnotherRegister);
        Assert.Equal("Mesa 1", sharedForAnotherRegister[0].DiningTable);
        Assert.Equal(2, sharedForAnotherRegister[0].Version);
        Assert.True(sharedForAnotherRegister[0].CheckoutRequested);

        await Assert.ThrowsAsync<InvalidOperationException>(() => commerce.DeleteHeldSaleAsync(
            orderId, "REGISTER-2", CancellationToken.None, expectedVersion: 1));
        await commerce.DeleteHeldSaleAsync(orderId, "REGISTER-2", CancellationToken.None, expectedVersion: 2);
        Assert.Empty(await commerce.GetHeldSalesAsync("REGISTER-3", CancellationToken.None));
    }

    [Fact]
    public async Task SqliteBackupCreatesIntegrityCheckedRestorableCopy()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tienda-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.db");
        var backupPath = Path.Combine(directory, "backup.db");
        try
        {
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Pooling = false
            }.ToString();
            await using (var source = new SqliteConnection(sourceConnectionString))
            {
                await source.OpenAsync();
                await using var command = source.CreateCommand();
                command.CommandText = "CREATE TABLE sales (id INTEGER PRIMARY KEY, total TEXT NOT NULL); INSERT INTO sales VALUES (1, '12.50');";
                await command.ExecuteNonQueryAsync();
            }

            await SqliteBackupService.CreateBackupAsync(sourceConnectionString, backupPath);

            Assert.True(File.Exists(backupPath));
            await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await backup.OpenAsync();
                await using var verify = backup.CreateCommand();
                verify.CommandText = "SELECT total FROM sales WHERE id = 1;";
                Assert.Equal("12.50", await verify.ExecuteScalarAsync());
                verify.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", await verify.ExecuteScalarAsync());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteBackupRejectsOverwritingItsSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tienda-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.db");
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Pooling = false
            }.ToString()))
            {
                await source.OpenAsync();
                await using var command = source.CreateCommand();
                command.CommandText = "CREATE TABLE test (id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                SqliteBackupService.CreateBackupAsync($"Data Source={sourcePath}", sourcePath));
            Assert.Contains("no puede sobrescribir", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://api.example.com", false, true)]
    [InlineData("http://api.example.com", true, false)]
    [InlineData("http://localhost:5000", false, false)]
    [InlineData("http://localhost:5000", true, true)]
    [InlineData("http://127.0.0.1:5000", true, true)]
    [InlineData("ftp://localhost:5000", true, false)]
    [InlineData("http://user:password@localhost:5000", true, false)]
    [InlineData("https://api.example.com/#token", false, false)]
    [InlineData("not-a-url", false, false)]
    public void CloudApiUrlPolicyRejectsUnsafeTransportAndUriComponents(
        string url,
        bool allowInsecureLoopback,
        bool expected)
    {
        var isValid = CloudApiUrlPolicy.TryValidate(url, allowInsecureLoopback, out var apiUri);

        Assert.Equal(expected, isValid);
        Assert.Equal(expected, apiUri is not null);
    }

    [Fact]
    public void ScaleBarcodeParserDecodesKilogramsAndRejectsInvalidCheckDigit()
    {
        const string body = "201234501250";
        var sum = body.Select((digit, index) =>
        {
            var value = digit - '0';
            return value * (index % 2 == 0 ? 1 : 3);
        }).Sum();
        var barcode = body + ((10 - sum % 10) % 10).ToString();

        Assert.True(ScaleBarcodeParser.TryParseEan13(barcode, out var itemCode, out var quantity));
        Assert.Equal("12345", itemCode);
        Assert.Equal(1.25m, quantity);
        Assert.False(ScaleBarcodeParser.TryParseEan13(barcode[..^1] + (barcode[^1] == '0' ? "1" : "0"),
            out _, out _));
    }

    [Fact]
    public async Task SaleUsesPriceListAndActivePromotionAndRejectsStaleQuote()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = "BEB-01",
            Name = "Jugo",
            Price = 20m
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var commerce = new CommerceService(db);
        var priceList = await commerce.CreatePriceListAsync(
            new CreatePriceListRequest("Mayorista"), "admin", CancellationToken.None);
        await commerce.SetProductListPriceAsync(
            priceList.Id,
            new SetProductListPriceRequest(product.Id, 15m),
            "admin",
            CancellationToken.None);
        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var promotion = await commerce.CreatePromotionAsync(
            new CreatePromotionRequest(
                product.Id, "Precio especial", PromotionKind.FixedPrice, 12.5m,
                startsAt, startsAt.AddHours(1)),
            "admin",
            CancellationToken.None);

        var occurredAt = DateTimeOffset.UtcNow;
        var line = new SaleLineRequest(
            product.Id, 1m, 12.5m, AppliedPromotionId: promotion.Id);
        var request = new CreateSaleRequest(
            Guid.NewGuid(), "POS-PRICE", [line], PaymentMethod.Cash, 12.5m,
            occurredAt, PriceListId: priceList.Id);
        var result = await new SaleService(db).CreateAsync(request, true, CancellationToken.None);
        var savedLine = await db.SaleLines.SingleAsync(item => item.SaleId == result.SaleId);
        Assert.Equal(12.5m, savedLine.UnitPrice);
        Assert.Equal(promotion.Id, savedLine.AppliedPromotionId);

        var staleRequest = request with
        {
            SaleId = Guid.NewGuid(),
            Lines = [line with { UnitPriceAtSale = 15m, AppliedPromotionId = null }],
            AmountTendered = 15m
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaleService(db).CreateAsync(staleRequest, true, CancellationToken.None));
        Assert.False(await db.Sales.AnyAsync(item => item.Id == staleRequest.SaleId));
    }

    [Fact]
    public async Task HeldSalePreservesCustomerAndPriceListSelection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var customer = await commerce.CreateCustomerAsync(
            new CreateCustomerRequest("Cliente de prueba", null, 100m), CancellationToken.None);
        var priceList = await commerce.CreatePriceListAsync(
            new CreatePriceListRequest("Preferencial"), "admin", CancellationToken.None);
        var heldId = Guid.NewGuid();
        await commerce.SaveHeldSaleAsync(
            new HoldSaleRequest(
                heldId, "POS-HOLD", "[]", "Mesa 2", "Sin cebolla", "Orden Ana",
                FulfillmentMode.DineIn, priceList.Id, customer.Id),
            CancellationToken.None);

        var held = Assert.Single(await commerce.GetHeldSalesAsync("POS-HOLD", CancellationToken.None));
        Assert.Equal(heldId, held.Id);
        Assert.Equal("Mesa 2", held.DiningTable);
        Assert.Equal("Orden Ana", held.NamedOrder);
        Assert.Equal(FulfillmentMode.DineIn, held.FulfillmentMode);
        Assert.Equal(priceList.Id, held.PriceListId);
        Assert.Equal(customer.Id, held.CustomerId);
        Assert.Equal("Sin cebolla", held.KitchenNotes);
    }

    [Fact]
    public async Task PaidTablesDisappearLocallyBeforeSyncAndCanBeOpenedAgain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var commerce = new CommerceService(db);
        var stock = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Refresco", "unidad", 10m, 0m, 1m), "owner", CancellationToken.None);
        var product = await commerce.CreateProductAsync(new CreateProductRequest(
            "TABLE-DRINK", "Refresco", 5m, false, stock.Id, ProductDepartment.Retail, []),
            "owner", CancellationToken.None);
        var cash = new CashRegisterService(db);
        var session = await cash.OpenAsync("TABLE-POS", "cashier", 0m);
        var openedOrders = new List<Guid>();
        var saleService = new SaleService(db);

        for (var index = 1; index <= 5; index++)
        {
            var table = $"Mesa {index}";
            var heldId = Guid.NewGuid();
            var held = await commerce.SaveHeldSaleAsync(new HoldSaleRequest(
                heldId, session.TerminalId, """{"items":[{"name":"Refresco"}]}""",
                DiningTable: table, FulfillmentMode: FulfillmentMode.DineIn), CancellationToken.None);
            openedOrders.Add(held.Id);

            await saleService.CreateAsync(new CreateSaleRequest(
                Guid.NewGuid(), session.TerminalId,
                [new SaleLineRequest(product.Id, 1m, product.Price)],
                PaymentMethod.Cash, product.Price, DateTimeOffset.UtcNow,
                DiningTable: table, FulfillmentMode: FulfillmentMode.DineIn,
                CashRegisterSessionId: session.Id, HeldSaleId: held.Id, HeldSaleVersion: held.Version),
                enqueueForSync: true, CancellationToken.None);
        }

        Assert.Empty(await commerce.GetHeldSalesAsync(session.TerminalId, CancellationToken.None));
        Assert.Equal(5, await db.HeldSales.CountAsync(item => item.ClosePending));
        Assert.Equal(5, await db.SyncOutbox.CountAsync());

        var reopened = await commerce.SaveHeldSaleAsync(new HoldSaleRequest(
            Guid.NewGuid(), session.TerminalId, """{"items":[{"name":"Nueva orden"}]}""",
            DiningTable: "Mesa 1", FulfillmentMode: FulfillmentMode.DineIn), CancellationToken.None);
        var openTables = await commerce.GetHeldSalesAsync(session.TerminalId, CancellationToken.None);
        Assert.Single(openTables);
        Assert.Equal(reopened.Id, openTables[0].Id);
        Assert.Equal(5, openedOrders.Count);
    }

    [Fact]
    public async Task CombiningThreeDiningTablePairsMovesLinesFreesSourcesAndWritesAuditOutbox()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var service = new DiningTableService(db);
        var pairs = new[] { ("Mesa A1", "Mesa B1"), ("Mesa A2", "Mesa B2"), ("Mesa A3", "Mesa B3") };
        var combinedDestinationIds = new List<Guid>();

        foreach (var (sourceTable, destinationTable) in pairs)
        {
            var productId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var destinationId = Guid.NewGuid();
            var sourceLine = new SaleLineRequest(productId, 2m, 12m, Notes: "bien cocido");
            var destinationLine = new SaleLineRequest(productId, 1m, 12m, Notes: "bien cocido");
            var source = await commerce.SaveHeldSaleAsync(new HoldSaleRequest(
                sourceId, "TABLE-MERGE", System.Text.Json.JsonSerializer.Serialize(new[] { sourceLine }),
                DiningTable: sourceTable, KitchenNotes: "bien cocido",
                FulfillmentMode: FulfillmentMode.DineIn), CancellationToken.None);
            var destination = await commerce.SaveHeldSaleAsync(new HoldSaleRequest(
                destinationId, "TABLE-MERGE", System.Text.Json.JsonSerializer.Serialize(new[] { destinationLine }),
                DiningTable: destinationTable, KitchenNotes: "bien cocido",
                FulfillmentMode: FulfillmentMode.DineIn), CancellationToken.None);
            combinedDestinationIds.Add(destination.Id);

            await service.CombineAsync(sourceTable, destinationTable, "owner", CancellationToken.None);
            var savedDestination = await db.HeldSales.SingleAsync(item => item.Id == destination.Id);
            var mergedLines = System.Text.Json.JsonSerializer.Deserialize<List<SaleLineRequest>>(savedDestination.Payload)!;
            Assert.Single(mergedLines);
            Assert.Equal(3m, mergedLines[0].Quantity);
            Assert.Equal(destinationTable, savedDestination.DiningTable);
            Assert.True(savedDestination.SyncPending);
            var savedSource = await db.HeldSales.SingleAsync(item => item.Id == source.Id);
            Assert.Null(savedSource.DiningTable);
            Assert.True(savedSource.DeletePending);
            Assert.Contains(destinationTable,
                (await commerce.GetHeldSalesAsync("TABLE-MERGE", CancellationToken.None))
                    .Select(item => item.DiningTable));
            Assert.DoesNotContain(sourceTable,
                (await commerce.GetHeldSalesAsync("TABLE-MERGE", CancellationToken.None))
                    .Select(item => item.DiningTable));
        }

        var combinationAudits = await db.AuditEntries.AsNoTracking()
            .Where(item => item.Action == "DINING_TABLES_COMBINED")
            .OrderBy(item => item.Reason)
            .ToListAsync();
        Assert.Equal(3, combinationAudits.Count);
        Assert.Equal(
            new[] { "Combinación de mesas Mesa A1 y Mesa B1", "Combinación de mesas Mesa A2 y Mesa B2",
                "Combinación de mesas Mesa A3 y Mesa B3" },
            combinationAudits.Select(item => item.Reason).OrderBy(reason => reason));
        Assert.Equal(3, await db.AuditOutbox.CountAsync(item =>
            combinationAudits.Select(audit => audit.Id).Contains(item.AuditEntryId)));
        Assert.Equal(3, combinedDestinationIds.Count);
    }

    [Fact]
    public async Task SaleUsesUnexpiredLotsSplitPaymentsAndNamedFulfillmentAndVoidRestoresLot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var now = DateTime.UtcNow;
        var stock = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Carne",
            Unit = "kg",
            OnHand = 2m,
            UnitCost = 10m
        };
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = "PLATO-01",
            Name = "Plato por peso",
            Price = 10m,
            StockItemId = stock.Id
        };
        var customPayment = new CustomPaymentType { Id = Guid.NewGuid(), Name = "Tarjeta" };
        var expired = new StockLot
        {
            Id = Guid.NewGuid(),
            StockItemId = stock.Id,
            LotCode = "VENCIDO",
            ReceivedQuantity = 1m,
            RemainingQuantity = 1m,
            UnitCost = 10m,
            ReceivedAtUtc = now.AddDays(-20),
            ExpiresAtUtc = now.AddDays(-1)
        };
        var fresh = new StockLot
        {
            Id = Guid.NewGuid(),
            StockItemId = stock.Id,
            LotCode = "VIGENTE",
            ReceivedQuantity = 1m,
            RemainingQuantity = 1m,
            UnitCost = 10m,
            ReceivedAtUtc = now.AddDays(-1),
            ExpiresAtUtc = now.AddDays(10)
        };
        db.AddRange(stock, product, customPayment, expired, fresh);
        await db.SaveChangesAsync();

        var request = new CreateSaleRequest(
            Guid.NewGuid(), "POS-LOT",
            [new SaleLineRequest(product.Id, 1m, product.Price)],
            PaymentMethod.Cash, 10m, new DateTimeOffset(now, TimeSpan.Zero),
            NamedOrder: "Pedido Ana", FulfillmentMode: FulfillmentMode.Takeaway,
            Payments:
            [
                new SalePaymentRequest(PaymentMethod.Cash, 4m),
                new SalePaymentRequest(PaymentMethod.Custom, 6m, customPayment.Id)
            ]);
        var result = await new SaleService(db).CreateAsync(request, true, CancellationToken.None);

        var sale = await db.Sales.Include(item => item.Payments).SingleAsync(item => item.Id == result.SaleId);
        Assert.Equal("Pedido Ana", sale.NamedOrder);
        Assert.Equal(FulfillmentMode.Takeaway, sale.FulfillmentMode);
        Assert.Equal(2, sale.Payments.Count);
        Assert.Equal("Tarjeta", sale.Payments.Single(item => item.Method == PaymentMethod.Custom).PaymentTypeName);
        Assert.Equal(1m, (await db.StockLots.SingleAsync(item => item.Id == expired.Id)).RemainingQuantity);
        Assert.Equal(0m, (await db.StockLots.SingleAsync(item => item.Id == fresh.Id)).RemainingQuantity);

        var unavailable = request with { SaleId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaleService(db).CreateAsync(unavailable, true, CancellationToken.None));
        Assert.False(await db.Sales.AnyAsync(item => item.Id == unavailable.SaleId));

        await new CommerceService(db).VoidSaleAsync(result.SaleId, "Devolución de prueba", "admin", CancellationToken.None);
        Assert.Equal(2m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
        Assert.Equal(1m, (await db.StockLots.SingleAsync(item => item.Id == fresh.Id)).RemainingQuantity);
    }

    [Fact]
    public async Task PhysicalStockCountPersistsVarianceAndUpdatesLotsAtomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var stock = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Harina",
            Unit = "kg",
            OnHand = 5m,
            UnitCost = 8m
        };
        var lot = new StockLot
        {
            Id = Guid.NewGuid(),
            StockItemId = stock.Id,
            LotCode = "HAR-01",
            ReceivedQuantity = 4m,
            RemainingQuantity = 4m,
            UnitCost = 8m,
            ReceivedAtUtc = DateTime.UtcNow
        };
        db.AddRange(stock, lot);
        await db.SaveChangesAsync();

        var result = await new CommerceService(db).CreateStockCountAsync(
            new CreateStockCountRequest("Conteo al cierre", [new StockCountLineRequest(stock.Id, 3m)]),
            "admin", CancellationToken.None);

        Assert.Equal(1, result.ItemsCounted);
        Assert.Equal(2m, result.AbsoluteVariance);
        Assert.Equal(3m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
        Assert.Equal(2m, (await db.StockLots.SingleAsync(item => item.Id == lot.Id)).RemainingQuantity);
        Assert.Contains(await db.AuditEntries.ToListAsync(), entry => entry.Action == "STOCK_COUNTED");
    }

    [Fact]
    public async Task CommerceBootstrapAddsNewTableAndKitchenColumnsToExistingSqliteDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var legacyCustomer = new CustomerAccount
        {
            Id = Guid.NewGuid(),
            Name = "Cliente anterior",
            Phone = "70000002",
            CreditBalance = 50m,
            CreditLimit = 100m
        };
        db.Customers.Add(legacyCustomer);
        await db.SaveChangesAsync();

        foreach (var statement in new[]
        {
            """ALTER TABLE "Sales" DROP COLUMN "DiningTable" """,
            """DROP INDEX "UX_HeldSales_DiningTable" """,
            """ALTER TABLE "HeldSales" DROP COLUMN "DiningTable" """,
            """ALTER TABLE "HeldSales" DROP COLUMN "KitchenNotes" """,
            """ALTER TABLE "KitchenTickets" DROP COLUMN "DiningTable" """,
            """ALTER TABLE "KitchenTicketLines" DROP COLUMN "Notes" """,
            """ALTER TABLE "Customers" DROP COLUMN "Address" """,
            """ALTER TABLE "Customers" DROP COLUMN "TaxId" """,
            """ALTER TABLE "Customers" DROP COLUMN "Notes" """,
            """DROP INDEX "IX_CustomerLedger_CashRegisterSessionId" """,
            """ALTER TABLE "CustomerLedger" DROP COLUMN "CashRegisterSessionId" """
        })
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        Assert.Empty(await db.Sales.Select(item => item.DiningTable).ToListAsync());
        Assert.Empty(await db.HeldSales.Select(item => new { item.DiningTable, item.KitchenNotes }).ToListAsync());
        Assert.Empty(await db.KitchenTickets.Select(item => item.DiningTable).ToListAsync());
        Assert.Empty(await db.KitchenTicketLines.Select(item => item.Notes).ToListAsync());
        var migratedCustomer = await db.Customers.SingleAsync(item => item.Id == legacyCustomer.Id);
        Assert.Equal("Cliente anterior", migratedCustomer.Name);
        Assert.Equal(50m, migratedCustomer.CreditBalance);
        Assert.Null(migratedCustomer.Address);
        Assert.Null(migratedCustomer.TaxId);
        Assert.Null(migratedCustomer.Notes);
        Assert.Equal(0, await db.CustomerChangeOutbox.CountAsync());
    }

    [Fact]
    public async Task FractionalRecipeConsumptionIsExactAndInsufficientStockRollsBackSale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var flour = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Harina",
            Unit = "kg",
            OnHand = 1.000m,
            UnitCost = 10m
        };
        var cheese = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Queso",
            Unit = "kg",
            OnHand = 0.300m,
            UnitCost = 20m
        };
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = "EMP-FRAC",
            Name = "Empanada fraccionaria",
            Price = 3m,
            IsRecipe = true
        };
        var finishedProduct = new Product
        {
            Id = Guid.NewGuid(),
            Sku = "ASADITO-NOTA",
            Name = "Asadito terminado",
            Price = 3m
        };
        db.AddRange(flour, cheese, product, finishedProduct);
        db.RecipeComponents.AddRange(
            new RecipeComponent { ProductId = product.Id, StockItemId = flour.Id, Quantity = 0.125m },
            new RecipeComponent { ProductId = product.Id, StockItemId = cheese.Id, Quantity = 0.0375m });
        await db.SaveChangesAsync();
        var stockItems = new Dictionary<Guid, StockItem> { [flour.Id] = flour, [cheese.Id] = cheese };
        var recipeComponents = new Dictionary<Guid, RecipeComponent[]>
        {
            [product.Id] = await db.RecipeComponents.ToArrayAsync()
        };
        Assert.Equal(8m, ProductStockCalculator.GetAvailableQuantity(product, stockItems, recipeComponents));

        var saleId = Guid.NewGuid();
        var service = new SaleService(db);
        var request = new CreateSaleRequest(
            saleId, "POS-FRAC",
            [
                new SaleLineRequest(product.Id, 3m, product.Price, Notes: "Sin cebolla"),
                new SaleLineRequest(finishedProduct.Id, 1m, finishedProduct.Price, Notes: "Bien cocido")
            ],
            PaymentMethod.Cash, 20m, DateTimeOffset.UtcNow, DiningTable: "Mesa 2", KitchenNotes: "Sin papa");

        await service.CreateAsync(request, true, CancellationToken.None);

        Assert.Equal(0.625m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);
        Assert.Equal(0.1875m, (await db.StockItems.SingleAsync(item => item.Id == cheese.Id)).OnHand);
        Assert.Equal(-0.375m, await db.StockMovements.Where(item =>
                item.SaleId == saleId && item.StockItemId == flour.Id)
            .Select(item => item.QuantityDelta).SingleAsync());
        Assert.Equal(-0.1125m, await db.StockMovements.Where(item =>
                item.SaleId == saleId && item.StockItemId == cheese.Id)
            .Select(item => item.QuantityDelta).SingleAsync());
        var ticket = await db.KitchenTickets.Include(item => item.Lines).SingleAsync(item => item.SaleId == saleId);
        Assert.Equal("Mesa 2", ticket.DiningTable);
        Assert.Equal("Sin cebolla", ticket.Lines.Single(line => line.ProductName == product.Name).Notes);
        Assert.Equal("Bien cocido", ticket.Lines.Single(line => line.ProductName == finishedProduct.Name).Notes);
        Assert.Contains(db.Sales.SelectMany(sale => sale.Lines),
            line => line.ProductId == product.Id && line.Notes == "Sin cebolla");
        Assert.Equal("Mesa 2", (await db.Sales.SingleAsync(item => item.Id == saleId)).DiningTable);
        var originalLines = request.Lines!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request with
            {
                Lines = [originalLines[0] with { Notes = "Sin papa" }, originalLines[1]]
            }, true, CancellationToken.None));

        var insufficientRequest = request with
        {
            SaleId = Guid.NewGuid(),
            Lines = [new SaleLineRequest(product.Id, 20m, product.Price)],
            AmountTendered = 100m
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(insufficientRequest, true, CancellationToken.None));

        Assert.Equal(0.625m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);
        Assert.Equal(0.1875m, (await db.StockItems.SingleAsync(item => item.Id == cheese.Id)).OnHand);
        Assert.False(await db.Sales.AnyAsync(item => item.Id == insufficientRequest.SaleId));
        Assert.False(await db.SyncOutbox.AnyAsync(item => item.SaleId == insufficientRequest.SaleId));
    }

    [Fact]
    public async Task RecipeConvertsGramsToKilogramsBeforeCheckingAndDeductingStock()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var potatoes = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Papa",
            Unit = "kg",
            OnHand = 10m,
            UnitCost = 4m
        };
        var sausage = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Salchicha",
            Unit = "g",
            OnHand = 1000m,
            UnitCost = .01m
        };
        var salt = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Sal",
            Unit = "g",
            OnHand = 20m,
            UnitCost = .002m
        };
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = "SALCHI-01",
            Name = "Salchipapa",
            Price = 20m,
            IsRecipe = true
        };
        db.AddRange(potatoes, sausage, salt, product);
        db.RecipeComponents.AddRange(
            new RecipeComponent
            {
                ProductId = product.Id,
                StockItemId = potatoes.Id,
                Quantity = StockUnitConversion.Convert(50m, "g", potatoes.Unit)
            },
            new RecipeComponent
            {
                ProductId = product.Id,
                StockItemId = sausage.Id,
                Quantity = StockUnitConversion.Convert(10m, "g", sausage.Unit)
            },
            new RecipeComponent
            {
                ProductId = product.Id,
                StockItemId = salt.Id,
                Quantity = StockUnitConversion.Convert(2m, "g", salt.Unit)
            });
        await db.SaveChangesAsync();

        var saleId = Guid.NewGuid();
        await new SaleService(db).CreateAsync(
            new CreateSaleRequest(
                saleId, "POS-RECIPE-UNITS",
                [new SaleLineRequest(product.Id, 1m, product.Price)],
                PaymentMethod.Cash, 20m, DateTimeOffset.UtcNow),
            true,
            CancellationToken.None);

        Assert.Equal(9.95m, (await db.StockItems.SingleAsync(item => item.Id == potatoes.Id)).OnHand);
        Assert.Equal(990m, (await db.StockItems.SingleAsync(item => item.Id == sausage.Id)).OnHand);
        Assert.Equal(18m, (await db.StockItems.SingleAsync(item => item.Id == salt.Id)).OnHand);
        Assert.Equal(-.05m, await db.StockMovements
            .Where(item => item.SaleId == saleId && item.StockItemId == potatoes.Id)
            .Select(item => item.QuantityDelta).SingleAsync());
        Assert.Equal(.05m, StockUnitConversion.Convert(50m, "g", "kg"));
        Assert.Throws<InvalidOperationException>(() => StockUnitConversion.Convert(50m, "g", "ml"));
    }

    [Fact]
    public async Task RecipeSaleIsAtomicIdempotentAndVoidRestoresInventory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using (var db = new TiendaDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
            var flour = new StockItem
            {
                Id = Guid.NewGuid(),
                Name = "Harina",
                Unit = "g",
                OnHand = 1000m,
                UnitCost = 0.02m
            };
            var cheese = new StockItem
            {
                Id = Guid.NewGuid(),
                Name = "Queso",
                Unit = "g",
                OnHand = 500m,
                UnitCost = 0.04m
            };
            var product = new Product
            {
                Id = Guid.NewGuid(),
                Sku = "EMP-01",
                Name = "Empanada",
                Price = 5m,
                IsRecipe = true
            };
            db.AddRange(flour, cheese, product);
            db.RecipeComponents.AddRange(
                new RecipeComponent { ProductId = product.Id, StockItemId = flour.Id, Quantity = 100m },
                new RecipeComponent { ProductId = product.Id, StockItemId = cheese.Id, Quantity = 30m });
            await db.SaveChangesAsync();

            var request = new CreateSaleRequest(
                Guid.NewGuid(),
                "POS-01",
                [new SaleLineRequest(product.Id, 2m, product.Price)],
                PaymentMethod.Cash,
                20m,
                DateTime.UtcNow);
            var service = new SaleService(db);
            var result = await service.CreateAsync(request, true, CancellationToken.None);
            Assert.Equal(10m, result.Total);
            Assert.Equal(10m, result.Change);
            Assert.Equal(800m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);
            Assert.Equal(440m, (await db.StockItems.SingleAsync(item => item.Id == cheese.Id)).OnHand);
            Assert.Equal(6.4m, await db.SaleCostSnapshots.Where(item => item.SaleId == result.SaleId)
                .Select(item => item.Cost).SingleAsync());
            var financial = await db.SaleLineFinancialSnapshots.SingleAsync();
            Assert.Equal(3.2m, financial.UnitCost);
            Assert.Equal(6.4m, financial.CostOfGoods);
            Assert.Equal(3.6m, financial.GrossMargin);
            Assert.Equal(36m, financial.GrossMarginPercent);
            Assert.Single(await db.KitchenTickets.Where(item => item.SaleId == result.SaleId).ToListAsync());
            Assert.Single(await db.SyncOutbox.Where(item => item.SaleId == result.SaleId).ToListAsync());
            var queuedRequest = System.Text.Json.JsonSerializer.Deserialize<CreateSaleRequest>(
                (await db.SyncOutbox.SingleAsync(item => item.SaleId == result.SaleId)).Payload)
                ?? throw new InvalidOperationException("La venta offline no serializó su solicitud.");
            var queuedLines = queuedRequest.Lines
                ?? throw new InvalidOperationException("La venta offline no serializó sus líneas.");
            Assert.Equal(result.SaleId, queuedRequest.SaleId);
            Assert.False(string.IsNullOrWhiteSpace(queuedLines.Single().RecipeFingerprint));
            Assert.Equal(2, await db.StockMovements.CountAsync(item => item.SaleId == result.SaleId));

            var duplicate = await service.CreateAsync(request, true, CancellationToken.None);
            Assert.True(duplicate.AlreadyProcessed);
            Assert.Equal(800m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);

            await new CommerceService(db).VoidSaleAsync(result.SaleId, "captura equivocada", "owner", CancellationToken.None);
            Assert.Equal(1000m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);
            Assert.Equal(SaleState.Voided, (await db.Sales.SingleAsync(item => item.Id == result.SaleId)).State);
            Assert.Equal("SALE_VOIDED", await db.AuditEntries.Where(item => item.EntityId == result.SaleId)
                .OrderByDescending(item => item.OccurredAtUtc).Select(item => item.Action).FirstAsync());
        }
    }

    [Fact]
    public async Task WalletAndCreditTransactionsAreRecordedWithSale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var product = new Product { Id = Guid.NewGuid(), Sku = "BEB-01", Name = "Agua", Price = 4m };
        var customer = new CustomerAccount
        {
            Id = Guid.NewGuid(),
            Name = "Cliente",
            WalletBalance = 20m,
            CreditLimit = 50m
        };
        db.AddRange(product, customer);
        await db.SaveChangesAsync();

        var request = new CreateSaleRequest(
            Guid.NewGuid(), "POS-02", [new SaleLineRequest(product.Id, 2, product.Price)],
            PaymentMethod.CustomerWallet, 8m, DateTime.UtcNow, customer.Id);
        var result = await new SaleService(db).CreateAsync(request, true, CancellationToken.None);
        Assert.Equal(12m, (await db.Customers.SingleAsync(item => item.Id == customer.Id)).WalletBalance);
        Assert.Equal(-8m, await db.CustomerLedger.Where(item => item.SaleId == result.SaleId)
            .Select(item => item.WalletDelta).SingleAsync());

        var credit = request with
        {
            SaleId = Guid.NewGuid(),
            PaymentMethod = PaymentMethod.Credit,
            AmountTendered = 0m
        };
        await new SaleService(db).CreateAsync(credit, true, CancellationToken.None);
        Assert.Equal(8m, (await db.Customers.SingleAsync(item => item.Id == customer.Id)).CreditBalance);
    }

    [Fact]
    public async Task VoidingIsRejectedAfterKitchenStarts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var product = new Product { Id = Guid.NewGuid(), Sku = "COM-02", Name = "Plato", Price = 9m, IsRecipe = true };
        var stock = new StockItem { Id = Guid.NewGuid(), Name = "Arroz", Unit = "g", OnHand = 500m, UnitCost = .01m };
        db.AddRange(product, stock);
        db.RecipeComponents.Add(new RecipeComponent { ProductId = product.Id, StockItemId = stock.Id, Quantity = 100 });
        await db.SaveChangesAsync();
        var result = await new SaleService(db).CreateAsync(
            new CreateSaleRequest(Guid.NewGuid(), "POS-01",
                [new SaleLineRequest(product.Id, 1, product.Price)], PaymentMethod.Cash, 9, DateTime.UtcNow),
            true, CancellationToken.None);
        var ticket = await db.KitchenTickets.SingleAsync(item => item.SaleId == result.SaleId);
        ticket.State = KitchenTicketState.Preparing;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommerceService(db).VoidSaleAsync(result.SaleId, "late cancel", "owner", CancellationToken.None));
        Assert.Equal(SaleState.Completed, (await db.Sales.SingleAsync(item => item.Id == result.SaleId)).State);
    }

    [Fact]
    public async Task PurchasesUpdateAverageCostAndReportsUseExactTimeRange()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var stock = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Arroz",
            Unit = "kg",
            OnHand = 0m,
            UnitCost = 1m
        };
        db.StockItems.Add(stock);
        await db.SaveChangesAsync();
        var commerce = new CommerceService(db);
        var supplier = await commerce.CreateSupplierAsync(
            new CreateSupplierRequest("Proveedor", "123", "70000000", "Calle Central", "María"),
            CancellationToken.None);
        Assert.Equal("Calle Central", supplier.Address);
        Assert.Equal("María", supplier.ContactName);
        supplier = await commerce.UpdateSupplierAsync(
            supplier.Id,
            new CreateSupplierRequest("Proveedor actualizado", "456", "71111111", "Av. Principal", "Juan"),
            "owner",
            CancellationToken.None);
        Assert.Equal("Proveedor actualizado", supplier.Name);
        Assert.Equal("Av. Principal", supplier.Address);
        Assert.Equal("Juan", supplier.ContactName);
        await commerce.ReceivePurchaseAsync(
            new ReceivePurchaseRequest(
                supplier.Id, "F-100", DateTimeOffset.UtcNow,
                [new PurchaseLineRequest(stock.Id, 10m, 1m)]),
            "owner", CancellationToken.None);
        var supplierItem = await db.SupplierStockItems.SingleAsync();
        Assert.Equal(supplier.Id, supplierItem.SupplierId);
        Assert.Equal(stock.Id, supplierItem.StockItemId);
        Assert.Equal(1m, supplierItem.LastUnitCost);
        Assert.Equal(10m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
        Assert.Equal(10m, await db.StockMovements.Where(item => item.StockItemId == stock.Id)
            .SumAsync(item => item.QuantityDelta));
        var product = await commerce.CreateProductAsync(
            new CreateProductRequest("ARR-01", "Bolsa de arroz", 10m, false, stock.Id,
                ProductDepartment.Grocery, []),
            "owner", CancellationToken.None);
        var sale = await new SaleService(db).CreateAsync(
            new CreateSaleRequest(
                Guid.NewGuid(), "POS-03", [new SaleLineRequest(product.Id, 2m, product.Price)],
                PaymentMethod.Cash, 20m, DateTimeOffset.UtcNow),
            false, CancellationToken.None);
        await commerce.RecordExpenseAsync(
            new CreateExpenseRequest("Gas", 3m, DateTimeOffset.UtcNow), "owner", CancellationToken.None);

        var report = await commerce.GetReportAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Equal(20m, report.Revenue);
        Assert.Equal(2m, report.CostOfGoods);
        Assert.Equal(3m, report.Expenses);
        Assert.Equal(18m, report.GrossProfit);
        Assert.Equal(15m, report.NetProfit);
        Assert.Equal(10m, report.PurchaseInvestment);
        Assert.Equal(0, report.SalesWithoutCostSnapshot);
        Assert.Equal(sale.SaleId, await db.SaleCostSnapshots.Select(item => item.SaleId).SingleAsync());

        await commerce.ReceivePurchaseAsync(
            new ReceivePurchaseRequest(
                supplier.Id, "F-101", DateTimeOffset.UtcNow,
                [
                    new PurchaseLineRequest(stock.Id, 1m, 2.2m),
                    new PurchaseLineRequest(stock.Id, 1m, 2.4m)
                ]),
            "owner", CancellationToken.None);
        Assert.Equal(2.4m, (await db.SupplierStockItems.SingleAsync()).LastUnitCost);
        Assert.Equal(10m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
        Assert.Equal(10m, await db.StockMovements.Where(item => item.StockItemId == stock.Id)
            .SumAsync(item => item.QuantityDelta));
    }

    [Fact]
    public async Task InventoryEditsAreAuditedAndSupplierCatalogLinksDoNotChangeStock()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var stock = await commerce.CreateStockItemAsync(
            new CreateStockItemRequest("Harina", "kg", 10m, 2m, 3m), "owner", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => commerce.CreateSupplierAsync(
            new CreateSupplierRequest("Proveedor incompleto", null, null),
            CancellationToken.None));

        stock = await commerce.UpdateStockItemAsync(
            stock.Id,
            new UpdateStockItemRequest("Harina integral", "kg", 12.5m, 3m, 3.5m, "Conteo físico"),
            "owner",
            CancellationToken.None);
        Assert.Equal("Harina integral", stock.Name);
        Assert.Equal(12.5m, stock.OnHand);
        Assert.Equal(12.5m, await db.StockMovements.Where(item => item.StockItemId == stock.Id)
            .SumAsync(item => item.QuantityDelta));
        Assert.Contains(await db.AuditEntries.ToListAsync(), item => item.Action == "STOCK_ITEM_UPDATED");
        await Assert.ThrowsAsync<InvalidOperationException>(() => commerce.UpdateStockItemAsync(
            stock.Id,
            new UpdateStockItemRequest("Harina integral", "g", 12.5m, 3m, 3.5m, "Cambio de unidad"),
            "owner",
            CancellationToken.None));

        var supplier = await commerce.CreateSupplierAsync(
            new CreateSupplierRequest(
                "Distribuidora", "NIT-1", "70000000", "Calle 1", "Ana",
                [new SupplierStockItemInput(stock.Id, 3.75m)]),
            CancellationToken.None);
        var supplierItem = await db.SupplierStockItems.SingleAsync();
        Assert.Equal(supplier.Id, supplierItem.SupplierId);
        Assert.Equal(3.75m, supplierItem.LastUnitCost);
        Assert.Equal(12.5m, (await db.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);

        var product = await commerce.CreateProductAsync(
            new CreateProductRequest("HAR-01", "Harina", 6m, false, stock.Id, ProductDepartment.Grocery, []),
            "owner", CancellationToken.None);
        product = await commerce.UpdateProductAsync(
            product.Id,
            new CreateProductRequest("HAR-02", "Harina premium", 7.5m, false, stock.Id,
                ProductDepartment.Cleaning, [], "123456", "Bodega"),
            "owner",
            CancellationToken.None);
        Assert.Equal("Harina premium", product.Name);
        Assert.Equal(7.5m, product.Price);
        Assert.Equal("123456", product.Barcode);
        var details = await db.ProductDetails.SingleAsync(item => item.ProductId == product.Id);
        Assert.Equal(ProductDepartment.Cleaning, details.Department);
        Assert.Equal("Bodega", details.PrintStation);
    }

    [Fact]
    public async Task CloudRejectsOfflineSaleWhenRecipeChanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var stock = new StockItem { Id = Guid.NewGuid(), Name = "Harina", Unit = "g", OnHand = 1000m, UnitCost = .02m };
        var product = new Product { Id = Guid.NewGuid(), Sku = "PAN-01", Name = "Pan", Price = 2m, IsRecipe = true };
        db.AddRange(stock, product);
        db.RecipeComponents.Add(new RecipeComponent
        {
            ProductId = product.Id,
            StockItemId = stock.Id,
            Quantity = 100m
        });
        await db.SaveChangesAsync();
        var request = new CreateSaleRequest(
            Guid.NewGuid(), "POS-04", [new SaleLineRequest(product.Id, 1m, product.Price)],
            PaymentMethod.Cash, 2m, DateTimeOffset.UtcNow);
        await new SaleService(db).CreateAsync(request, true, CancellationToken.None);
        var pending = await db.SyncOutbox.SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<CreateSaleRequest>(pending.Payload)!;
        await using var cloudConnection = new SqliteConnection("Data Source=:memory:");
        await cloudConnection.OpenAsync();
        var cloudOptions = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(cloudConnection).Options;
        await using var cloud = new TiendaDbContext(cloudOptions);
        await cloud.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(cloud);
        cloud.Add(new StockItem
        {
            Id = stock.Id,
            Name = stock.Name,
            Unit = stock.Unit,
            OnHand = 1000m,
            UnitCost = stock.UnitCost
        });
        cloud.Add(new Product
        {
            Id = product.Id,
            Sku = product.Sku,
            Name = product.Name,
            Price = product.Price,
            IsRecipe = true
        });
        cloud.RecipeComponents.Add(new RecipeComponent
        {
            ProductId = product.Id,
            StockItemId = stock.Id,
            Quantity = 200m
        });
        await cloud.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SaleService(cloud).CreateAsync(payload, false, CancellationToken.None));
        Assert.Equal(1000m, (await cloud.StockItems.SingleAsync(item => item.Id == stock.Id)).OnHand);
        Assert.Empty(await cloud.Sales.Where(item => item.Id == request.SaleId).ToListAsync());
    }

    [Fact]
    public async Task UncostedSaleDoesNotReportInventedProfitAndFinancialSchemaCanBeBootstrapped()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("""DROP TABLE "SaleLineFinancialSnapshots" """);
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = "SERV-01",
            Name = "Servicio sin costo asociado",
            Price = 10m
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        await new SaleService(db).CreateAsync(
            new CreateSaleRequest(Guid.NewGuid(), "POS-05",
                [new SaleLineRequest(product.Id, 1m, product.Price)],
                PaymentMethod.Cash, 10m, DateTimeOffset.UtcNow),
            false, CancellationToken.None);

        var report = await new CommerceService(db).GetReportAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Equal(1, report.SalesWithoutCostSnapshot);
        Assert.Null(report.GrossProfit);
        Assert.Null(report.NetProfit);
        var financial = await db.SaleLineFinancialSnapshots.SingleAsync();
        Assert.Null(financial.UnitCost);
    }

    [Fact]
    public async Task ServiceProductsSupportZeroOrConfiguredUnitCostAndRemainCostedInFinancialReports()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var freeService = await commerce.CreateProductAsync(new CreateProductRequest(
            "SERVICE-ZERO", "Recarga sin costo", 10m, false, null, ProductDepartment.Service, [],
            ServiceUnitCost: 0m), "owner", CancellationToken.None);
        var costedService = await commerce.CreateProductAsync(new CreateProductRequest(
            "SERVICE-COST", "Fotocopia", 12m, false, null, ProductDepartment.Service, [],
            ServiceUnitCost: 2.5m), "owner", CancellationToken.None);
        var startedAt = DateTimeOffset.UtcNow;
        var saleService = new SaleService(db);

        await saleService.CreateAsync(new CreateSaleRequest(
            Guid.NewGuid(), "POS-SERVICE",
            [new SaleLineRequest(freeService.Id, 1m, freeService.Price)],
            PaymentMethod.Cash, freeService.Price, startedAt), true, CancellationToken.None);
        await saleService.CreateAsync(new CreateSaleRequest(
            Guid.NewGuid(), "POS-SERVICE",
            [new SaleLineRequest(costedService.Id, 1m, costedService.Price)],
            PaymentMethod.Cash, costedService.Price, startedAt.AddSeconds(1)), true, CancellationToken.None);

        var report = await commerce.GetReportAsync(
            startedAt.AddMinutes(-1), startedAt.AddMinutes(1), CancellationToken.None);
        var costs = await db.SaleLineFinancialSnapshots.AsNoTracking()
            .OrderBy(item => item.UnitCost)
            .Select(item => item.UnitCost)
            .ToListAsync();

        Assert.Equal(0m, costs[0]);
        Assert.Equal(2.5m, costs[1]);
        Assert.Equal(0, report.SalesWithoutCostSnapshot);
        Assert.Equal(22m, report.Revenue);
        Assert.Equal(2.5m, report.CostOfGoods);
        Assert.Equal(19.5m, report.GrossProfit);
        Assert.Equal(19.5m, report.NetProfit);
    }

    [Fact]
    public async Task DemoDataSeederCreatesRealisticLocalDemoOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        Assert.True(await DemoDataSeeder.EnsureSeededAsync(db));
        Assert.Equal(7, await db.Products.CountAsync());
        Assert.Equal(15, await db.StockItems.CountAsync());
        Assert.Equal(3, await db.Customers.CountAsync());
        Assert.Equal(2, await db.Suppliers.CountAsync());
        Assert.Equal(3, await db.PurchaseOrders.CountAsync());
        Assert.Equal(2, await db.Expenses.CountAsync());
        Assert.Equal(3, await db.Sales.CountAsync());
        Assert.Equal(2, await db.KitchenTickets.CountAsync());
        Assert.Equal(3, await db.SaleCostSnapshots.CountAsync());
        Assert.Equal(3, await db.SyncOutbox.CountAsync(item => item.IsDelivered));
        Assert.Equal(0, await db.RecipeComponents.Where(item => item.ProductId
            == Guid.Parse("20000000-0000-0000-0000-000000000005")).CountAsync());
        Assert.True(await db.BusinessAssets.AnyAsync(item => item.Key == DemoDataSeeder.LogoAssetKey));

        Assert.False(await DemoDataSeeder.EnsureSeededAsync(db));
        Assert.Equal(7, await db.Products.CountAsync());
        Assert.Equal(3, await db.Sales.CountAsync());
    }

    [Fact]
    public async Task ProductionBatchConsumesRawStockAddsFinishedStockAndSaleConsumesOnlyFinishedStock()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var meat = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Carne", Unit = "kg", OnHand = 15m, UnitCost = 20m
        };
        var frozenStock = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Asaditos congelados", Unit = "unidad", OnHand = 0m
        };
        var asadito = new Product
        {
            Id = Guid.NewGuid(), Sku = "ASA-01", Name = "Asadito congelado",
            Price = 5m, StockItemId = frozenStock.Id
        };
        var rawLot = new StockLot
        {
            Id = Guid.NewGuid(), StockItemId = meat.Id, LotCode = "CARNE-01",
            ReceivedQuantity = 15m, RemainingQuantity = 15m, UnitCost = 20m,
            ReceivedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.AddRange(meat, frozenStock, asadito, rawLot);
        db.ProductDetails.Add(new ProductDetails
        {
            ProductId = asadito.Id,
            Department = ProductDepartment.FrozenBatch
        });
        await db.SaveChangesAsync();

        var production = new ProductionService(db);
        var firstEstimate = await production.GetProductionCapacityAsync(asadito.Id, CancellationToken.None);
        Assert.Null(firstEstimate.MaximumAdditionalQuantity);

        var request = new RecordProductionRequest(
            Guid.NewGuid(), asadito.Id, 80m, [new ProductionIngredientInput(meat.Id, 10m)]);
        var result = await production.RecordProductionAsync(request, "owner", CancellationToken.None);

        Assert.Equal(80m, result.QuantityProduced);
        Assert.Equal(5m, (await db.StockItems.SingleAsync(item => item.Id == meat.Id)).OnHand);
        Assert.Equal(80m, (await db.StockItems.SingleAsync(item => item.Id == frozenStock.Id)).OnHand);
        Assert.Equal(5m, (await db.StockLots.SingleAsync(item => item.Id == rawLot.Id)).RemainingQuantity);
        Assert.Equal(80m, await db.StockLots.Where(item => item.StockItemId == frozenStock.Id)
            .SumAsync(item => item.RemainingQuantity));
        Assert.Equal(request.BatchId, (await db.StockLots.SingleAsync(item => item.StockItemId == frozenStock.Id)).Id);
        Assert.Equal(-10m, await db.StockMovements.Where(item => item.ProductionBatchId == request.BatchId
            && item.StockItemId == meat.Id).SumAsync(item => item.QuantityDelta));
        Assert.Equal(80m, await db.StockMovements.Where(item => item.ProductionBatchId == request.BatchId
            && item.StockItemId == frozenStock.Id).SumAsync(item => item.QuantityDelta));
        Assert.True((await db.StockProductionBatches.SingleAsync(item => item.Id == request.BatchId)).SyncPending);

        var estimate = await production.GetProductionCapacityAsync(asadito.Id, CancellationToken.None);
        Assert.Equal(40m, estimate.MaximumAdditionalQuantity);
        Assert.Equal(0.125m, Assert.Single(estimate.Ingredients).QuantityPerFinishedUnit);

        var retry = await production.RecordProductionAsync(request, "owner", CancellationToken.None);
        Assert.Equal(result.BatchId, retry.BatchId);
        Assert.Equal(80m, (await db.StockItems.SingleAsync(item => item.Id == frozenStock.Id)).OnHand);

        await new SaleService(db).CreateAsync(
            new CreateSaleRequest(
                Guid.NewGuid(), "POS-PRODUCTION",
                [new SaleLineRequest(asadito.Id, 1m, asadito.Price)],
                PaymentMethod.Cash, asadito.Price, DateTimeOffset.UtcNow),
            true,
            CancellationToken.None);

        Assert.Equal(5m, (await db.StockItems.SingleAsync(item => item.Id == meat.Id)).OnHand);
        Assert.Equal(79m, (await db.StockItems.SingleAsync(item => item.Id == frozenStock.Id)).OnHand);
    }

    [Fact]
    public async Task ProductionBatchRejectsInsufficientIngredientsAndRecipeProducts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var raw = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Papa", Unit = "kg", OnHand = 0.5m, UnitCost = 3m
        };
        var finished = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Empanadas congeladas", Unit = "unidad", OnHand = 0m
        };
        var frozenProduct = new Product
        {
            Id = Guid.NewGuid(), Sku = "EMP-01", Name = "Empanada congelada",
            Price = 4m, StockItemId = finished.Id
        };
        var recipeProduct = new Product
        {
            Id = Guid.NewGuid(), Sku = "REC-01", Name = "Salchipapa",
            Price = 15m, IsRecipe = true
        };
        db.AddRange(raw, finished, frozenProduct, recipeProduct);
        db.ProductDetails.Add(new ProductDetails
        {
            ProductId = frozenProduct.Id,
            Department = ProductDepartment.FrozenBatch
        });
        db.ProductDetails.Add(new ProductDetails
        {
            ProductId = recipeProduct.Id,
            Department = ProductDepartment.PreparedDirect
        });
        await db.SaveChangesAsync();

        var service = new ProductionService(db);
        var insufficient = new RecordProductionRequest(
            Guid.NewGuid(), frozenProduct.Id, 10m, [new ProductionIngredientInput(raw.Id, 1m)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordProductionAsync(insufficient, "owner", CancellationToken.None));
        Assert.Equal(0.5m, (await db.StockItems.SingleAsync(item => item.Id == raw.Id)).OnHand);
        Assert.Equal(0m, (await db.StockItems.SingleAsync(item => item.Id == finished.Id)).OnHand);
        Assert.Empty(await db.StockProductionBatches.ToListAsync());

        var recipeProduction = new RecordProductionRequest(
            Guid.NewGuid(), recipeProduct.Id, 10m, [new ProductionIngredientInput(raw.Id, 0.1m)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordProductionAsync(recipeProduction, "owner", CancellationToken.None));
        Assert.Empty(await db.StockProductionBatches.ToListAsync());
    }
}
