using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class DemoDataSeeder
{
    public const string SeedMarker = "demo-seed-v1";
    public const string LogoAssetKey = "official-logo";

    private static readonly Guid CocaStockId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WaterStockId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid SoapStockId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid TissueStockId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid BeefStockId = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly Guid FlourStockId = Guid.Parse("10000000-0000-0000-0000-000000000006");
    private static readonly Guid EggStockId = Guid.Parse("10000000-0000-0000-0000-000000000007");
    private static readonly Guid SaltStockId = Guid.Parse("10000000-0000-0000-0000-000000000008");
    private static readonly Guid GarlicStockId = Guid.Parse("10000000-0000-0000-0000-000000000009");
    private static readonly Guid OnionStockId = Guid.Parse("10000000-0000-0000-0000-000000000010");
    private static readonly Guid PotatoStockId = Guid.Parse("10000000-0000-0000-0000-000000000011");
    private static readonly Guid SausageStockId = Guid.Parse("10000000-0000-0000-0000-000000000012");
    private static readonly Guid RiceStockId = Guid.Parse("10000000-0000-0000-0000-000000000013");
    private static readonly Guid ChickenStockId = Guid.Parse("10000000-0000-0000-0000-000000000014");
    private static readonly Guid AsaditoFinishedStockId = Guid.Parse("10000000-0000-0000-0000-000000000015");
    private static readonly Guid SodaProductId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid WaterProductId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid SoapProductId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid TissueProductId = Guid.Parse("20000000-0000-0000-0000-000000000004");
    private static readonly Guid AsaditoProductId = Guid.Parse("20000000-0000-0000-0000-000000000005");
    private static readonly Guid SalchipapaProductId = Guid.Parse("20000000-0000-0000-0000-000000000006");
    private static readonly Guid LunchProductId = Guid.Parse("20000000-0000-0000-0000-000000000007");
    private static readonly Guid SupplierOneId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid SupplierTwoId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid CustomerOneId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid CustomerTwoId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid CustomerThreeId = Guid.Parse("40000000-0000-0000-0000-000000000003");

    public static async Task<bool> EnsureSeededAsync(
        TiendaDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.BusinessAssets.AnyAsync(item => item.Key == SeedMarker, cancellationToken))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var stock = new[]
        {
            NewStock(CocaStockId, "Refresco cola 500 ml", "unidad", 8m, 20m),
            NewStock(WaterStockId, "Agua sin gas 600 ml", "unidad", 3.5m, 20m),
            NewStock(SoapStockId, "Jabón de tocador", "unidad", 4.2m, 12m),
            NewStock(TissueStockId, "Papel higiénico", "rollo", 2.8m, 24m),
            NewStock(BeefStockId, "Carne molida", "g", 0.045m, 30000m),
            NewStock(FlourStockId, "Harina", "g", 0.012m, 20000m),
            NewStock(EggStockId, "Huevo", "unidad", 1.1m, 120m),
            NewStock(SaltStockId, "Sal", "g", 0.006m, 3000m),
            NewStock(GarlicStockId, "Ajo", "g", 0.035m, 1200m),
            NewStock(OnionStockId, "Cebolla", "g", 0.014m, 5000m),
            NewStock(PotatoStockId, "Papa", "g", 0.009m, 25000m),
            NewStock(SausageStockId, "Salchicha", "unidad", 0.85m, 160m),
            NewStock(RiceStockId, "Arroz", "g", 0.011m, 15000m),
            NewStock(ChickenStockId, "Pollo", "g", 0.032m, 20000m),
            NewStock(AsaditoFinishedStockId, "Asadito congelado", "unidad", 5m, 10m)
        };
        db.StockItems.AddRange(stock);
        db.Suppliers.AddRange(
            new Supplier { Id = SupplierOneId, Name = "Distribuidora Oriente (DEMO)", TaxId = "DEMO-001", Phone = "70000001" },
            new Supplier { Id = SupplierTwoId, Name = "Mercado Abasto Central (DEMO)", TaxId = "DEMO-002", Phone = "70000002" });

        var products = new[]
        {
            NewProduct(SodaProductId, "BEB-001", "Refresco cola 500 ml", 8m, CocaStockId, false),
            NewProduct(WaterProductId, "BEB-002", "Agua sin gas 600 ml", 5m, WaterStockId, false),
            NewProduct(SoapProductId, "ASE-001", "Jabón de tocador", 8m, SoapStockId, false),
            NewProduct(TissueProductId, "ASE-002", "Papel higiénico", 5m, TissueStockId, false),
            NewProduct(AsaditoProductId, "COM-001", "Asadito congelado", 12m, AsaditoFinishedStockId, false),
            NewProduct(SalchipapaProductId, "COM-002", "Salchipapa personal", 18m, null, true),
            NewProduct(LunchProductId, "COM-003", "Almuerzo de pollo", 25m, null, true)
        };
        db.Products.AddRange(products);
        db.ProductDetails.AddRange(products.Select(product =>
            new ProductDetails
            {
                ProductId = product.Id,
                Department = product.Id == AsaditoProductId
                    ? ProductDepartment.FrozenBatch
                    : product.Id == SalchipapaProductId || product.Id == LunchProductId
                    ? ProductDepartment.PreparedDirect
                    : product.Id == SoapProductId || product.Id == TissueProductId
                        ? ProductDepartment.Retail
                        : ProductDepartment.Retail
            }));
        db.RecipeComponents.AddRange(
            Recipe(SalchipapaProductId, (PotatoStockId, 220m), (SausageStockId, 2m), (SaltStockId, 3m))
            .Concat(Recipe(LunchProductId, (ChickenStockId, 180m), (RiceStockId, 120m), (SaltStockId, 2m),
                (GarlicStockId, 2m), (OnionStockId, 12m))));
        db.Customers.AddRange(
            new CustomerAccount { Id = CustomerOneId, Name = "Juan Pérez (DEMO)", Phone = "70010001", WalletBalance = 80m, CreditLimit = 150m },
            new CustomerAccount { Id = CustomerTwoId, Name = "María Gómez (DEMO)", Phone = "70010002", WalletBalance = 35m, CreditLimit = 100m },
            new CustomerAccount { Id = CustomerThreeId, Name = "Carlos Rojas (DEMO)", Phone = "70010003", CreditLimit = 80m });
        db.CustomerLedger.AddRange(
            NewLedger(CustomerOneId, CustomerLedgerKind.WalletTopUp, 80m, 0m, now.AddDays(-2), "Abono de monedero demo"),
            NewLedger(CustomerTwoId, CustomerLedgerKind.WalletTopUp, 35m, 0m, now.AddDays(-1), "Abono de monedero demo"));

        AddPurchase(db, SupplierOneId, "DEMO-FAC-001", now.AddHours(-5),
            (CocaStockId, 48m, 8m), (WaterStockId, 48m, 3.5m), (SoapStockId, 24m, 4.2m), (TissueStockId, 48m, 2.8m));
        AddPurchase(db, SupplierTwoId, "DEMO-FAC-002", now.AddHours(-4),
            (BeefStockId, 30000m, 0.045m), (FlourStockId, 20000m, 0.012m),
            (EggStockId, 120m, 1.1m), (SaltStockId, 3000m, 0.006m),
            (GarlicStockId, 1200m, 0.035m), (OnionStockId, 5000m, 0.014m));
        AddPurchase(db, SupplierTwoId, "DEMO-FAC-003", now.AddHours(-3),
            (PotatoStockId, 25000m, 0.009m), (SausageStockId, 160m, 0.85m),
            (RiceStockId, 15000m, 0.011m), (ChickenStockId, 20000m, 0.032m));
        db.Expenses.AddRange(
            new Expense { Id = Guid.Parse("50000000-0000-0000-0000-000000000001"), Description = "Gas de cocina (DEMO)", Amount = 45m, OccurredAtUtc = now.AddHours(-2), Actor = "demo-seed" },
            new Expense { Id = Guid.Parse("50000000-0000-0000-0000-000000000002"), Description = "Transporte de mercadería (DEMO)", Amount = 20m, OccurredAtUtc = now.AddHours(-1), Actor = "demo-seed" });
        await db.SaveChangesAsync(cancellationToken);

        await new ProductionService(db).RecordProductionAsync(
            new RecordProductionRequest(
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                AsaditoProductId,
                80m,
                [
                    new ProductionIngredientInput(BeefStockId, 10000m),
                    new ProductionIngredientInput(SaltStockId, 1000m),
                    new ProductionIngredientInput(GarlicStockId, 500m)
                ]),
                "demo-seed",
                cancellationToken);

        var saleService = new SaleService(db);
        var demoSales = new[]
        {
            new CreateSaleRequest(
                Guid.Parse("60000000-0000-0000-0000-000000000001"), "DEMO-POS",
                [new SaleLineRequest(SodaProductId, 2m, 8m), new SaleLineRequest(AsaditoProductId, 1m, 12m)],
                PaymentMethod.Cash, 50m, new DateTimeOffset(now.AddMinutes(-35), TimeSpan.Zero)),
            new CreateSaleRequest(
                Guid.Parse("60000000-0000-0000-0000-000000000002"), "DEMO-POS",
                [new SaleLineRequest(SalchipapaProductId, 1m, 18m), new SaleLineRequest(WaterProductId, 1m, 5m)],
                PaymentMethod.Cash, 30m, new DateTimeOffset(now.AddMinutes(-20), TimeSpan.Zero)),
            new CreateSaleRequest(
                Guid.Parse("60000000-0000-0000-0000-000000000003"), "DEMO-POS",
                [new SaleLineRequest(LunchProductId, 1m, 25m), new SaleLineRequest(SoapProductId, 1m, 8m)],
                PaymentMethod.Transfer, 33m, new DateTimeOffset(now.AddMinutes(-8), TimeSpan.Zero))
        };
        foreach (var request in demoSales)
        {
            if (!await db.Sales.AnyAsync(sale => sale.Id == request.SaleId, cancellationToken))
            {
                await saleService.CreateAsync(request, enqueueForSync: true, cancellationToken);
                var outbox = await db.SyncOutbox.SingleAsync(item => item.SaleId == request.SaleId, cancellationToken);
                outbox.IsDelivered = true;
                outbox.LastError = "Venta de demostración local; no sincronizar.";
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        db.BusinessAssets.Add(new BusinessAsset
        {
            Key = SeedMarker,
            ContentType = "text/plain",
            Data = System.Text.Encoding.UTF8.GetBytes("Datos ficticios de demostración; no son registros comerciales."),
            UpdatedAtUtc = DateTime.UtcNow
        });
        if (!await db.BusinessAssets.AnyAsync(item => item.Key == LogoAssetKey, cancellationToken))
        {
            db.BusinessAssets.Add(new BusinessAsset
            {
                Key = LogoAssetKey,
                ContentType = "image/svg+xml",
                Data = System.Text.Encoding.UTF8.GetBytes(
                    """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 520 180"><defs><linearGradient id="brand" x2="1" y2="1"><stop stop-color="#ff9e3e"/><stop offset="1" stop-color="#cd2b1e"/></linearGradient></defs><rect width="520" height="180" rx="24" fill="#191616"/><rect x="18" y="18" width="144" height="144" rx="30" fill="url(#brand)"/><text x="37" y="111" fill="white" font-family="Segoe UI,sans-serif" font-size="57" font-weight="700">TM</text><text x="185" y="78" fill="#ffa755" font-family="Segoe UI,sans-serif" font-size="34" font-weight="700">TIENDA</text><text x="185" y="119" fill="white" font-family="Segoe UI,sans-serif" font-size="34" font-weight="700">MIRIAM</text><text x="188" y="151" fill="#ddd" font-family="Segoe UI,sans-serif" font-size="16">SANTA CRUZ · BOLIVIA</text></svg>"""),
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static StockItem NewStock(Guid id, string name, string unit, decimal unitCost, decimal reorderAt) => new()
    {
        Id = id,
        Name = name,
        Unit = unit,
        UnitCost = unitCost,
        ReorderAt = reorderAt,
        OnHand = 0m
    };

    private static Product NewProduct(
        Guid id, string sku, string name, decimal price, Guid? stockItemId, bool isRecipe) => new()
    {
        Id = id,
        Sku = sku,
        Name = name,
        Price = price,
        StockItemId = stockItemId,
        IsRecipe = isRecipe
    };

    private static RecipeComponent[] Recipe(
        Guid productId, params (Guid StockItemId, decimal Quantity)[] components) =>
        components.Select(component => new RecipeComponent
        {
            ProductId = productId,
            StockItemId = component.StockItemId,
            Quantity = component.Quantity
        }).ToArray();

    private static CustomerLedgerEntry NewLedger(
        Guid customerId, CustomerLedgerKind kind, decimal walletDelta, decimal creditDelta,
        DateTime occurredAtUtc, string description) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        Kind = kind,
        WalletDelta = walletDelta,
        CreditDelta = creditDelta,
        OccurredAtUtc = occurredAtUtc,
        Actor = "demo-seed",
        Description = description
    };

    private static void AddPurchase(
        TiendaDbContext db,
        Guid supplierId,
        string invoiceReference,
        DateTime receivedAtUtc,
        params (Guid StockItemId, decimal Quantity, decimal UnitCost)[] lines)
    {
        var orderId = invoiceReference switch
        {
            "DEMO-FAC-001" => Guid.Parse("70000000-0000-0000-0000-000000000001"),
            "DEMO-FAC-002" => Guid.Parse("70000000-0000-0000-0000-000000000002"),
            _ => Guid.Parse("70000000-0000-0000-0000-000000000003")
        };
        var order = new PurchaseOrder
        {
            Id = orderId,
            SupplierId = supplierId,
            ReceivedAtUtc = receivedAtUtc,
            InvoiceReference = invoiceReference,
            Actor = "demo-seed"
        };
        foreach (var line in lines)
        {
            var item = db.ChangeTracker.Entries<StockItem>()
                .Single(entry => entry.Entity.Id == line.StockItemId).Entity;
            var oldValue = item.OnHand * item.UnitCost;
            item.OnHand += line.Quantity;
            item.UnitCost = decimal.Round(
                (oldValue + line.Quantity * line.UnitCost) / item.OnHand,
                4,
                MidpointRounding.AwayFromZero);
            var lineTotal = decimal.Round(line.Quantity * line.UnitCost, 2, MidpointRounding.AwayFromZero);
            order.Total += lineTotal;
            order.Lines.Add(new PurchaseLine
            {
                Id = Guid.NewGuid(),
                PurchaseOrderId = order.Id,
                PurchaseOrder = order,
                StockItemId = line.StockItemId,
                Quantity = line.Quantity,
                UnitCost = line.UnitCost,
                LineTotal = lineTotal
            });
        }
        db.PurchaseOrders.Add(order);
    }
}
