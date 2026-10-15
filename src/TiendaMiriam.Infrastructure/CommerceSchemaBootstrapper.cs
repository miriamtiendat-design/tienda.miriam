using Microsoft.EntityFrameworkCore;
using System.Data;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class CommerceSchemaBootstrapper
{
    public static async Task EnsureCommerceTablesAsync(
        TiendaDbContext db,
        CancellationToken cancellationToken = default)
    {
        var isPostgres = db.Database.IsNpgsql();
        var id = isPostgres ? "uuid" : "TEXT";
        var amount = isPostgres ? "numeric(18,6)" : "NUMERIC";
        var money = isPostgres ? "numeric(18,2)" : "NUMERIC";
        var date = isPostgres ? "timestamp with time zone" : "TEXT";
        var boolean = isPostgres ? "boolean" : "INTEGER";
        var blob = isPostgres ? "bytea" : "BLOB";
        var statements = new[]
        {
            $"""CREATE TABLE IF NOT EXISTS "ProductPhotos" ("ProductId" {id} PRIMARY KEY REFERENCES "Products" ("Id") ON DELETE CASCADE, "ContentType" varchar(64) NOT NULL, "Data" {blob} NOT NULL, "UpdatedAtUtc" {date} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "BusinessAssets" ("Key" varchar(80) PRIMARY KEY, "ContentType" varchar(64) NOT NULL, "Data" {blob} NOT NULL, "UpdatedAtUtc" {date} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "ProductDetails" ("ProductId" {id} PRIMARY KEY REFERENCES "Products" ("Id") ON DELETE CASCADE, "Department" integer NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "PriceLists" ("Id" {id} PRIMARY KEY, "Name" varchar(80) NOT NULL, "IsActive" {boolean} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "ProductListPrices" ("PriceListId" {id} NOT NULL REFERENCES "PriceLists" ("Id") ON DELETE CASCADE, "ProductId" {id} NOT NULL REFERENCES "Products" ("Id") ON DELETE CASCADE, "Price" numeric(18,4) NOT NULL, PRIMARY KEY ("PriceListId", "ProductId"))""",
            $"""CREATE TABLE IF NOT EXISTS "Promotions" ("Id" {id} PRIMARY KEY, "ProductId" {id} NOT NULL REFERENCES "Products" ("Id") ON DELETE CASCADE, "Name" varchar(100) NOT NULL, "Kind" integer NOT NULL, "Value" numeric(18,4) NOT NULL, "StartsAtUtc" {date} NOT NULL, "EndsAtUtc" {date} NOT NULL, "IsActive" {boolean} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "PromotionBundles" ("Id" {id} PRIMARY KEY, "Name" varchar(120) NOT NULL, "Price" {money} NOT NULL, "StartsAtUtc" {date} NULL, "EndsAtUtc" {date} NULL, "IsActive" {boolean} NOT NULL, "IsDeleted" {boolean} NOT NULL, "CreatedAtUtc" {date} NOT NULL, "UpdatedAtUtc" {date} NOT NULL, "DeletedAtUtc" {date} NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "PromotionBundleComponents" ("PromotionBundleId" {id} NOT NULL REFERENCES "PromotionBundles" ("Id") ON DELETE CASCADE, "ProductId" {id} NOT NULL REFERENCES "Products" ("Id") ON DELETE RESTRICT, "Quantity" {amount} NOT NULL, PRIMARY KEY ("PromotionBundleId", "ProductId"))""",
            $"""CREATE TABLE IF NOT EXISTS "PromotionBundleSaleSnapshots" ("SaleLineId" {id} PRIMARY KEY REFERENCES "SaleLines" ("Id") ON DELETE CASCADE, "RegularAmount" {money} NOT NULL, "DiscountAmount" {money} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "PromotionBundleOutbox" ("Id" {id} PRIMARY KEY, "PromotionBundleId" {id} NOT NULL, "Payload" TEXT NOT NULL, "CreatedAtUtc" {date} NOT NULL, "Attempts" integer NOT NULL DEFAULT 0, "LastError" varchar(1000) NULL, "IsDelivered" {boolean} NOT NULL DEFAULT false)""",
            $"""CREATE TABLE IF NOT EXISTS "StockLots" ("Id" {id} PRIMARY KEY, "StockItemId" {id} NOT NULL REFERENCES "StockItems" ("Id") ON DELETE RESTRICT, "LotCode" varchar(80) NOT NULL, "ReceivedQuantity" {amount} NOT NULL, "RemainingQuantity" {amount} NOT NULL, "UnitCost" numeric(18,4) NOT NULL, "ReceivedAtUtc" {date} NOT NULL, "ExpiresAtUtc" {date} NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "StockProductionBatches" ("Id" {id} PRIMARY KEY, "ProductId" {id} NOT NULL REFERENCES "Products" ("Id") ON DELETE RESTRICT, "FinishedStockItemId" {id} NOT NULL REFERENCES "StockItems" ("Id") ON DELETE RESTRICT, "QuantityProduced" {amount} NOT NULL, "TotalCost" numeric(18,4) NOT NULL, "Actor" varchar(160) NOT NULL, "OccurredAtUtc" {date} NOT NULL, "SyncPending" {boolean} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "StockProductionLines" ("Id" {id} PRIMARY KEY, "ProductionBatchId" {id} NOT NULL REFERENCES "StockProductionBatches" ("Id") ON DELETE CASCADE, "StockItemId" {id} NOT NULL REFERENCES "StockItems" ("Id") ON DELETE RESTRICT, "QuantityConsumed" {amount} NOT NULL, "UnitCost" numeric(18,4) NOT NULL, "TotalCost" numeric(18,4) NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "StockCounts" ("Id" {id} PRIMARY KEY, "Actor" varchar(160) NOT NULL, "Reason" varchar(500) NOT NULL, "OccurredAtUtc" {date} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "StockCountLines" ("Id" {id} PRIMARY KEY, "StockCountId" {id} NOT NULL REFERENCES "StockCounts" ("Id") ON DELETE CASCADE, "StockItemId" {id} NOT NULL REFERENCES "StockItems" ("Id") ON DELETE RESTRICT, "ExpectedQuantity" {amount} NOT NULL, "CountedQuantity" {amount} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "CustomPaymentTypes" ("Id" {id} PRIMARY KEY, "Name" varchar(80) NOT NULL, "IsActive" {boolean} NOT NULL)""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CustomPaymentTypes_Name" ON "CustomPaymentTypes" ("Name")""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PriceLists_Name" ON "PriceLists" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_Promotions_ProductId_IsActive_StartsAtUtc_EndsAtUtc" ON "Promotions" ("ProductId", "IsActive", "StartsAtUtc", "EndsAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_PromotionBundles_IsActive_IsDeleted_StartsAtUtc_EndsAtUtc" ON "PromotionBundles" ("IsActive", "IsDeleted", "StartsAtUtc", "EndsAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_PromotionBundleOutbox_IsDelivered_CreatedAtUtc" ON "PromotionBundleOutbox" ("IsDelivered", "CreatedAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_StockLots_StockItemId_ExpiresAtUtc" ON "StockLots" ("StockItemId", "ExpiresAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_StockProductionBatches_ProductId_OccurredAtUtc" ON "StockProductionBatches" ("ProductId", "OccurredAtUtc")""",
            $"""CREATE TABLE IF NOT EXISTS "Users" ("Id" {id} PRIMARY KEY, "Username" varchar(64) NOT NULL, "PasswordHash" varchar(512) NOT NULL, "Role" integer NOT NULL, "AuthVersion" {id} NOT NULL, "IsActive" {boolean} NOT NULL, "CreatedAtUtc" {date} NOT NULL)""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Username" ON "Users" ("Username")""",
            $"""CREATE TABLE IF NOT EXISTS "CashRegisterSessions" ("Id" {id} PRIMARY KEY, "TerminalId" varchar(80) NOT NULL, "OpenedBy" varchar(64) NOT NULL, "OpenedAtUtc" {date} NOT NULL, "OpeningCash" {money} NOT NULL, "ClosedAtUtc" {date} NULL, "ClosedBy" varchar(64) NULL, "CountedCash" {money} NULL, "ExpectedCash" {money} NULL, "Variance" {money} NULL, "ClosingNote" varchar(300) NULL, "Version" integer NOT NULL DEFAULT 0)""",
            """CREATE INDEX IF NOT EXISTS "IX_CashRegisterSessions_TerminalId_ClosedAtUtc" ON "CashRegisterSessions" ("TerminalId", "ClosedAtUtc")""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashRegisterSessions_OpenTerminal" ON "CashRegisterSessions" ("TerminalId") WHERE "ClosedAtUtc" IS NULL""",
            $"""CREATE TABLE IF NOT EXISTS "Customers" ("Id" {id} PRIMARY KEY, "Name" varchar(160) NOT NULL, "Phone" varchar(32) NULL, "Address" varchar(240) NULL, "TaxId" varchar(40) NULL, "Notes" varchar(500) NULL, "WalletBalance" {money} NOT NULL, "CreditBalance" {money} NOT NULL, "CreditLimit" {money} NOT NULL, "IsActive" {boolean} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "CustomerLedger" ("Id" {id} PRIMARY KEY, "CustomerId" {id} NOT NULL REFERENCES "Customers" ("Id") ON DELETE RESTRICT, "SaleId" {id} NULL REFERENCES "Sales" ("Id") ON DELETE RESTRICT, "Kind" integer NOT NULL, "WalletDelta" {money} NOT NULL, "CreditDelta" {money} NOT NULL, "PaymentMethod" integer NOT NULL DEFAULT 0, "CashRegisterSessionId" {id} NULL, "OccurredAtUtc" {date} NOT NULL, "Actor" varchar(160) NOT NULL, "Description" varchar(300) NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "CustomerChangeOutbox" ("Id" {id} PRIMARY KEY, "CustomerId" {id} NOT NULL, "Kind" integer NOT NULL, "Payload" TEXT NOT NULL, "CreatedAtUtc" {date} NOT NULL, "Attempts" integer NOT NULL DEFAULT 0, "LastError" varchar(1000) NULL, "IsDelivered" {boolean} NOT NULL DEFAULT false)""",
            """CREATE INDEX IF NOT EXISTS "IX_CustomerChangeOutbox_Kind_IsDelivered_CreatedAtUtc" ON "CustomerChangeOutbox" ("Kind", "IsDelivered", "CreatedAtUtc")""",
            $"""CREATE TABLE IF NOT EXISTS "Suppliers" ("Id" {id} PRIMARY KEY, "Name" varchar(160) NOT NULL, "TaxId" varchar(40) NULL, "Phone" varchar(32) NULL, "IsActive" {boolean} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "SupplierStockItems" ("SupplierId" {id} NOT NULL REFERENCES "Suppliers" ("Id") ON DELETE CASCADE, "StockItemId" {id} NOT NULL REFERENCES "StockItems" ("Id") ON DELETE RESTRICT, "LastUnitCost" numeric(18,4) NOT NULL, "LastPurchasedAtUtc" {date} NOT NULL, PRIMARY KEY ("SupplierId", "StockItemId"))""",
            $"""CREATE TABLE IF NOT EXISTS "PurchaseOrders" ("Id" {id} PRIMARY KEY, "SupplierId" {id} NOT NULL REFERENCES "Suppliers" ("Id") ON DELETE RESTRICT, "CashRegisterSessionId" {id} NULL REFERENCES "CashRegisterSessions" ("Id") ON DELETE RESTRICT, "PaymentMethod" integer NOT NULL DEFAULT 3, "ReceivedAtUtc" {date} NOT NULL, "DueAtUtc" {date} NULL, "Total" {money} NOT NULL, "InvoiceReference" varchar(100) NOT NULL, "Actor" varchar(160) NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "SupplierPayments" ("Id" {id} PRIMARY KEY, "PurchaseOrderId" {id} NOT NULL REFERENCES "PurchaseOrders" ("Id") ON DELETE RESTRICT, "CashRegisterSessionId" {id} NOT NULL REFERENCES "CashRegisterSessions" ("Id") ON DELETE RESTRICT, "Amount" {money} NOT NULL, "PaymentMethod" integer NOT NULL, "OccurredAtUtc" {date} NOT NULL, "Actor" varchar(160) NOT NULL, "Note" varchar(160) NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "Expenses" ("Id" {id} PRIMARY KEY, "Description" varchar(300) NOT NULL, "Amount" {money} NOT NULL, "PaymentMethod" integer NOT NULL DEFAULT 0, "CashRegisterSessionId" {id} NULL REFERENCES "CashRegisterSessions" ("Id") ON DELETE RESTRICT, "PurchaseOrderId" {id} NULL UNIQUE REFERENCES "PurchaseOrders" ("Id") ON DELETE RESTRICT, "OccurredAtUtc" {date} NOT NULL, "Actor" varchar(160) NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "PurchaseLines" ("Id" {id} PRIMARY KEY, "PurchaseOrderId" {id} NOT NULL REFERENCES "PurchaseOrders" ("Id") ON DELETE CASCADE, "StockItemId" {id} NOT NULL REFERENCES "StockItems" ("Id") ON DELETE RESTRICT, "Quantity" {amount} NOT NULL, "UnitCost" {isPostgres switch { true => "numeric(18,4)", false => "NUMERIC" }} NOT NULL, "LineTotal" {money} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "HeldSales" ("Id" {id} PRIMARY KEY, "TerminalId" varchar(80) NOT NULL, "Payload" TEXT NOT NULL, "DiningTable" varchar(40) NULL, "KitchenNotes" varchar(160) NULL, "UpdatedAtUtc" {date} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "KitchenTickets" ("Id" {id} PRIMARY KEY, "SaleId" {id} NOT NULL UNIQUE REFERENCES "Sales" ("Id") ON DELETE RESTRICT, "State" integer NOT NULL, "DiningTable" varchar(40) NULL, "CreatedAtUtc" {date} NOT NULL, "UpdatedAtUtc" {date} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "KitchenTicketLines" ("Id" {id} PRIMARY KEY, "KitchenTicketId" {id} NOT NULL REFERENCES "KitchenTickets" ("Id") ON DELETE CASCADE, "ProductName" varchar(160) NOT NULL, "Quantity" {amount} NOT NULL, "Notes" varchar(160) NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "AuditEntries" ("Id" {id} PRIMARY KEY, "Actor" varchar(160) NOT NULL, "Action" varchar(80) NOT NULL, "EntityType" varchar(80) NOT NULL, "EntityId" {id} NOT NULL, "Reason" varchar(500) NOT NULL, "OccurredAtUtc" {date} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "AuditOutbox" ("Id" {id} PRIMARY KEY, "AuditEntryId" {id} NOT NULL, "Payload" TEXT NOT NULL, "CreatedAtUtc" {date} NOT NULL, "Attempts" integer NOT NULL DEFAULT 0, "LastError" varchar(1000) NULL, "IsDelivered" {boolean} NOT NULL DEFAULT false)""",
            $"""CREATE TABLE IF NOT EXISTS "SaleCostSnapshots" ("SaleId" {id} PRIMARY KEY REFERENCES "Sales" ("Id") ON DELETE RESTRICT, "Cost" {money} NOT NULL)""",
            $"""CREATE TABLE IF NOT EXISTS "SaleLineFinancialSnapshots" ("SaleLineId" {id} PRIMARY KEY REFERENCES "SaleLines" ("Id") ON DELETE CASCADE, "UnitCost" {(isPostgres ? "numeric(28,10)" : "NUMERIC")} NULL, "CostOfGoods" {(isPostgres ? "numeric(28,10)" : "NUMERIC")} NULL, "GrossMargin" {(isPostgres ? "numeric(28,10)" : "NUMERIC")} NULL, "GrossMarginPercent" {(isPostgres ? "numeric(18,6)" : "NUMERIC")} NULL)""",
            """CREATE INDEX IF NOT EXISTS "IX_CustomerLedger_CustomerId_OccurredAtUtc" ON "CustomerLedger" ("CustomerId", "OccurredAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_Expenses_OccurredAtUtc" ON "Expenses" ("OccurredAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_PurchaseOrders_SupplierId_ReceivedAtUtc" ON "PurchaseOrders" ("SupplierId", "ReceivedAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_SupplierStockItems_SupplierId_LastPurchasedAtUtc" ON "SupplierStockItems" ("SupplierId", "LastPurchasedAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_HeldSales_TerminalId_UpdatedAtUtc" ON "HeldSales" ("TerminalId", "UpdatedAtUtc")""",
            """CREATE INDEX IF NOT EXISTS "IX_AuditEntries_EntityType_EntityId_OccurredAtUtc" ON "AuditEntries" ("EntityType", "EntityId", "OccurredAtUtc")"""
        };

        foreach (var statement in statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        await EnsureColumnAsync(db, "Sales", "DiningTable", "varchar(40) NULL", cancellationToken);
        await EnsureColumnAsync(db, "AuditEntries", "TerminalId", "varchar(80) NULL", cancellationToken);
        await EnsureColumnAsync(db, "AuditEntries", "PreviousValue", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AuditEntries", "NewValue", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "AuditEntries", "Observation", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Suppliers", "Address", "varchar(240) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Suppliers", "ContactName", "varchar(120) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Sales", "NamedOrder", "varchar(80) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Sales", "FulfillmentMode", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Sales", "PriceListId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "SaleLines", "AppliedPromotionId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "SaleLines", "PromotionBundleId", $"{id} NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_SaleLines_PromotionBundleId" ON "SaleLines" ("PromotionBundleId")""",
            cancellationToken);
        await EnsureColumnAsync(db, "Products", "Barcode", "varchar(64) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Users", "FailedLoginAttempts", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Users", "LockedUntilUtc", $"{date} NULL", cancellationToken);
        await EnsureColumnAsync(db, "Customers", "Address", "varchar(240) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Customers", "TaxId", "varchar(40) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Customers", "Notes", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "Sales", "CashRegisterSessionId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "Sales", "HeldSaleId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "SaleLines", "Notes", "varchar(160) NULL", cancellationToken);
        await EnsureColumnAsync(db, "CashRegisterSessions", "Version", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "CashRegisterSessions", "SyncPending", "boolean NOT NULL DEFAULT false", cancellationToken);
        await EnsureColumnAsync(db, "CustomerLedger", "PaymentMethod", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "CustomerLedger", "CashRegisterSessionId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "Expenses", "PaymentMethod", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Expenses", "CashRegisterSessionId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "Expenses", "PurchaseOrderId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "PurchaseOrders", "CashRegisterSessionId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "PurchaseOrders", "PaymentMethod", "integer NOT NULL DEFAULT 3", cancellationToken);
        await EnsureColumnAsync(db, "PurchaseOrders", "AmountPaid", $"{money} NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "PurchaseOrders", "DueAtUtc", $"{date} NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_PurchaseOrders_SupplierId_DueAtUtc" ON "PurchaseOrders" ("SupplierId", "DueAtUtc")""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "PurchaseOrders" SET "AmountPaid" = "Total" WHERE "AmountPaid" = 0 AND "PaymentMethod" IN (0, 1)""",
            cancellationToken);
        await EnsureColumnAsync(db, "ProductDetails", "PrintStation", "varchar(80) NULL", cancellationToken);
        await EnsureColumnAsync(db, "ProductDetails", "ServiceUnitCost", "numeric(18,4) NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Payments", "PaymentTypeId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "Payments", "PaymentTypeName", "varchar(80) NULL", cancellationToken);
        await EnsureColumnAsync(db, "StockMovements", "StockLotId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "StockMovements", "ProductionBatchId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "DiningTable", "varchar(40) NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "KitchenNotes", "varchar(160) NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "NamedOrder", "varchar(80) NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "FulfillmentMode", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "PriceListId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "CustomerId", $"{id} NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "Version", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "SyncPending", "boolean NOT NULL DEFAULT false", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "ExpectedVersion", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "ClosePending", "boolean NOT NULL DEFAULT false", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "StartedAtUtc", $"{date} NULL", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "CheckoutRequested", "boolean NOT NULL DEFAULT false", cancellationToken);
        await EnsureColumnAsync(db, "HeldSales", "DeletePending", "boolean NOT NULL DEFAULT false", cancellationToken);
        await EnsureColumnAsync(db, "KitchenTickets", "DiningTable", "varchar(40) NULL", cancellationToken);
        await EnsureColumnAsync(db, "KitchenTickets", "NamedOrder", "varchar(80) NULL", cancellationToken);
        await EnsureColumnAsync(db, "KitchenTickets", "FulfillmentMode", "integer NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "KitchenTicketLines", "Notes", "varchar(160) NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Products_Barcode" ON "Products" ("Barcode")""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """DROP INDEX IF EXISTS "UX_HeldSales_DiningTable" """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX \"UX_HeldSales_DiningTable\" ON \"HeldSales\" (\"DiningTable\") WHERE \"DiningTable\" IS NOT NULL AND NOT \"ClosePending\" AND NOT \"DeletePending\"",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_StockMovements_ProductionBatchId" ON "StockMovements" ("ProductionBatchId")""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Expenses_PurchaseOrderId" ON "Expenses" ("PurchaseOrderId") WHERE "PurchaseOrderId" IS NOT NULL""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_SupplierPayments_PurchaseOrderId_OccurredAtUtc" ON "SupplierPayments" ("PurchaseOrderId", "OccurredAtUtc")""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_CashRegisterSessions_SyncPending" ON "CashRegisterSessions" ("SyncPending")""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_AuditEntries_OccurredAtUtc" ON "AuditEntries" ("OccurredAtUtc")""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_AuditOutbox_IsDelivered_CreatedAtUtc" ON "AuditOutbox" ("IsDelivered", "CreatedAtUtc")""",
            cancellationToken);
        await MigrateProductDepartmentsAsync(db, cancellationToken);
    }

    private static async Task MigrateProductDepartmentsAsync(
        TiendaDbContext db,
        CancellationToken cancellationToken)
    {
        const string markerKey = "product-departments-v2";
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.BusinessAssets.AnyAsync(item => item.Key == markerKey, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ProductDetails"
            SET "Department" = CASE
                WHEN "Department" = 0 THEN 0
                WHEN "Department" = 1 THEN CASE
                    WHEN EXISTS (
                        SELECT 1 FROM "Products" AS product
                        WHERE product."Id" = "ProductDetails"."ProductId" AND product."IsRecipe" = false)
                    THEN 4 ELSE 1 END
                WHEN "Department" IN (2, 4) THEN 2
                WHEN "Department" IN (3, 5) THEN 0
                ELSE "Department"
            END
            """,
            cancellationToken);
        db.BusinessAssets.Add(new BusinessAsset
        {
            Key = markerKey,
            ContentType = "text/plain",
            Data = System.Text.Encoding.UTF8.GetBytes(
                "Migración de departamentos a insumos, preparados directos, lotes congelados, tienda y servicios."),
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        TiendaDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            if (db.Database.IsNpgsql())
            {
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema() AND table_name = @table AND column_name = @column)
                    """;
                var tableParameter = command.CreateParameter();
                tableParameter.ParameterName = "@table";
                tableParameter.Value = table;
                command.Parameters.Add(tableParameter);
                var columnParameter = command.CreateParameter();
                columnParameter.ParameterName = "@column";
                columnParameter.Value = column;
                command.Parameters.Add(columnParameter);
            }
            else
            {
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
            }

            var exists = false;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (db.Database.IsNpgsql())
                {
                    exists = await reader.ReadAsync(cancellationToken) && reader.GetBoolean(0);
                }
                else
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }
            }

            if (!exists)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeAfter)
            {
                await connection.CloseAsync();
            }
        }
    }
}
