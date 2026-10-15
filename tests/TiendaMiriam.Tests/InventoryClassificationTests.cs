using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;
using Xunit;

namespace TiendaMiriam.Tests;

public sealed class InventoryClassificationTests
{
    [Fact]
    public async Task LegacyDepartmentsAreMigratedOnceToTheFiveCanonicalCategories()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var stock = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = "Existencia de prueba",
            Unit = "unidad",
            OnHand = 5m,
            UnitCost = 1m
        };
        var products = new[]
        {
            LegacyProduct("LEG-GROCERY", false, 0, stock.Id),
            LegacyProduct("LEG-RECIPE", true, 1, null),
            LegacyProduct("LEG-FROZEN", false, 1, stock.Id),
            LegacyProduct("LEG-RECHARGE", false, 2, stock.Id),
            LegacyProduct("LEG-CLEANING", false, 3, stock.Id),
            LegacyProduct("LEG-SERVICE", false, 4, null),
            LegacyProduct("LEG-OTHER", false, 5, stock.Id)
        };
        db.StockItems.Add(stock);
        db.Products.AddRange(products.Select(item => item.Product));
        db.ProductDetails.AddRange(products.Select(item => new ProductDetails
        {
            ProductId = item.Product.Id,
            Department = (ProductDepartment)item.LegacyDepartment
        }));
        await db.SaveChangesAsync();

        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var migrated = await db.ProductDetails.AsNoTracking()
            .ToDictionaryAsync(item => item.ProductId, item => item.Department);
        Assert.Equal(ProductDepartment.Retail, migrated[products[0].Product.Id]);
        Assert.Equal(ProductDepartment.PreparedDirect, migrated[products[1].Product.Id]);
        Assert.Equal(ProductDepartment.FrozenBatch, migrated[products[2].Product.Id]);
        Assert.Equal(ProductDepartment.Service, migrated[products[3].Product.Id]);
        Assert.Equal(ProductDepartment.Retail, migrated[products[4].Product.Id]);
        Assert.Equal(ProductDepartment.Service, migrated[products[5].Product.Id]);
        Assert.Equal(ProductDepartment.Retail, migrated[products[6].Product.Id]);

        var raw = db.ProductDetails.Single(item => item.ProductId == products[0].Product.Id);
        raw.Department = ProductDepartment.RawIngredient;
        await db.SaveChangesAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        Assert.Equal(ProductDepartment.RawIngredient,
            await db.ProductDetails.Where(item => item.ProductId == raw.ProductId)
                .Select(item => item.Department).SingleAsync());
    }

    [Fact]
    public async Task ProductionDirectRecipeRetailAndServiceUseOnlyTheirOwnInventoryRules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);

        var flour = await CreateStockAsync(commerce, "Harina", "g", 1000m);
        var cheese = await CreateStockAsync(commerce, "Queso", "g", 1000m);
        var frozenStock = await CreateStockAsync(commerce, "Empanada congelada", "unidad", 0m);
        var frozen = await commerce.CreateProductAsync(
            new CreateProductRequest("EMP-FROZEN", "Empanada congelada", 5m, false, frozenStock.Id,
                ProductDepartment.FrozenBatch, []),
            "owner", CancellationToken.None);

        await new ProductionService(db).RecordProductionAsync(
            new RecordProductionRequest(
                Guid.NewGuid(),
                frozen.Id,
                10m,
                [
                    new ProductionIngredientInput(flour.Id, 1000m),
                    new ProductionIngredientInput(cheese.Id, 1000m)
                ]),
            "owner",
            CancellationToken.None);

        Assert.Equal(0m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);
        Assert.Equal(0m, (await db.StockItems.SingleAsync(item => item.Id == cheese.Id)).OnHand);
        Assert.Equal(10m, (await db.StockItems.SingleAsync(item => item.Id == frozenStock.Id)).OnHand);
        await SellAsync(db, frozen, 5m);
        Assert.Equal(0m, (await db.StockItems.SingleAsync(item => item.Id == flour.Id)).OnHand);
        Assert.Equal(0m, (await db.StockItems.SingleAsync(item => item.Id == cheese.Id)).OnHand);
        Assert.Equal(5m, (await db.StockItems.SingleAsync(item => item.Id == frozenStock.Id)).OnHand);

        var chicken = await CreateStockAsync(commerce, "Pollo", "g", 2000m);
        var rice = await CreateStockAsync(commerce, "Arroz", "g", 2000m);
        var potato = await CreateStockAsync(commerce, "Papa", "g", 2000m);
        var tomato = await CreateStockAsync(commerce, "Tomate", "g", 1000m);
        var onion = await CreateStockAsync(commerce, "Cebolla", "g", 1000m);
        var lunch = await commerce.CreateProductAsync(
            new CreateProductRequest(
                "LUNCH-DIRECT", "Almuerzo de pollo", 25m, true, null, ProductDepartment.PreparedDirect,
                [
                    new RecipeComponentInput(chicken.Id, 180m),
                    new RecipeComponentInput(rice.Id, 120m),
                    new RecipeComponentInput(potato.Id, 100m),
                    new RecipeComponentInput(tomato.Id, 50m),
                    new RecipeComponentInput(onion.Id, 30m)
                ]),
            "owner", CancellationToken.None);
        await SellAsync(db, lunch, 1m);
        Assert.Equal(1820m, (await db.StockItems.SingleAsync(item => item.Id == chicken.Id)).OnHand);
        Assert.Equal(1880m, (await db.StockItems.SingleAsync(item => item.Id == rice.Id)).OnHand);
        Assert.Equal(1900m, (await db.StockItems.SingleAsync(item => item.Id == potato.Id)).OnHand);
        Assert.Equal(950m, (await db.StockItems.SingleAsync(item => item.Id == tomato.Id)).OnHand);
        Assert.Equal(970m, (await db.StockItems.SingleAsync(item => item.Id == onion.Id)).OnHand);
        Assert.Equal(5m, (await db.StockItems.SingleAsync(item => item.Id == frozenStock.Id)).OnHand);

        var potatoForSale = await CreateStockAsync(commerce, "Papa a granel", "kg", 5m);
        var rawPotatoProduct = await commerce.CreateProductAsync(
            new CreateProductRequest("POTATO-BULK", "Papa por kilo", 6m, false, potatoForSale.Id,
                ProductDepartment.RawIngredient, []),
            "owner", CancellationToken.None);
        await SellAsync(db, rawPotatoProduct, 2m);
        Assert.Equal(3m, (await db.StockItems.SingleAsync(item => item.Id == potatoForSale.Id)).OnHand);

        var drinkStock = await CreateStockAsync(commerce, "Refresco cola", "unidad", 3m);
        var drink = await commerce.CreateProductAsync(
            new CreateProductRequest("DRINK-RETAIL", "Refresco cola", 8m, false, drinkStock.Id,
                ProductDepartment.Retail, []),
            "owner", CancellationToken.None);
        var service = await commerce.CreateProductAsync(
            new CreateProductRequest("MOBILE-RECHARGE", "Recarga móvil", 10m, false, null,
                ProductDepartment.Service, []),
            "owner", CancellationToken.None);
        await SellAsync(db, drink, 1m);
        await SellAsync(db, service, 1m);
        Assert.Equal(2m, (await db.StockItems.SingleAsync(item => item.Id == drinkStock.Id)).OnHand);
        Assert.Null(service.StockItemId);
    }

    [Fact]
    public async Task ProductReclassificationIsAuditedAndRejectsIncompatibleStructure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
        var commerce = new CommerceService(db);
        var stock = await CreateStockAsync(commerce, "Detergente", "unidad", 2m);
        var product = await commerce.CreateProductAsync(
            new CreateProductRequest("CLEAN-01", "Detergente", 10m, false, stock.Id,
                ProductDepartment.Retail, []),
            "owner", CancellationToken.None);

        await commerce.UpdateProductDepartmentAsync(
            product.Id, ProductDepartment.RawIngredient, "owner", CancellationToken.None);
        Assert.Equal(ProductDepartment.RawIngredient,
            await db.ProductDetails.Where(item => item.ProductId == product.Id)
                .Select(item => item.Department).SingleAsync());
        Assert.Contains(await db.AuditEntries.ToListAsync(),
            item => item.Action == "PRODUCT_DEPARTMENT_CHANGED");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commerce.UpdateProductDepartmentAsync(
                product.Id, ProductDepartment.Service, "owner", CancellationToken.None));
    }

    [Fact]
    public async Task ExistingDemoCatalogIsCorrectedWithoutLeavingRecipesOnFrozenProducts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TiendaDbContext>().UseSqlite(connection).Options;
        await using var db = new TiendaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);

        var raw = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Carne", Unit = "g", OnHand = 20000m, UnitCost = 0.04m
        };
        var rechargeStock = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Saldo de recargas de Bs 10", Unit = "recarga",
            OnHand = 50m, UnitCost = 10m
        };
        var frozenStock = new StockItem
        {
            Id = Guid.NewGuid(), Name = "Empanada congelada de queso", Unit = "unidad",
            OnHand = 45m, UnitCost = 3m
        };
        var asadito = new Product
        {
            Id = Guid.NewGuid(), Sku = "COM-001", Name = "Asadito de carne", Price = 12m, IsRecipe = true
        };
        var empanada = new Product
        {
            Id = Guid.NewGuid(), Sku = "DEMO-X-EMPANADA", Name = "Empanada de queso (receta)",
            Price = 7m, IsRecipe = true
        };
        var duplicateFrozen = new Product
        {
            Id = Guid.NewGuid(), Sku = "DEMO-X-EMP-FROZEN", Name = "Empanada congelada de queso",
            Price = 5m, StockItemId = frozenStock.Id
        };
        var recharge = new Product
        {
            Id = Guid.NewGuid(), Sku = "DEMO-X-RECARGA10", Name = "Recarga móvil Bs 10",
            Price = 12m, StockItemId = rechargeStock.Id
        };
        var chickenEmpanada = new Product
        {
            Id = Guid.NewGuid(), Sku = "TM-C787128C", Name = "Empanada de pollo",
            Price = 6m, IsRecipe = true
        };
        db.StockItems.AddRange(raw, rechargeStock, frozenStock);
        db.Products.AddRange(asadito, empanada, duplicateFrozen, recharge, chickenEmpanada);
        db.ProductDetails.AddRange(new[] { asadito, empanada, duplicateFrozen, recharge, chickenEmpanada }
            .Select(item => new ProductDetails
            {
                ProductId = item.Id,
                Department = item.IsRecipe ? ProductDepartment.PreparedDirect : ProductDepartment.Service
            }));
        db.RecipeComponents.AddRange(
            new RecipeComponent { ProductId = asadito.Id, StockItemId = raw.Id, Quantity = 100m },
            new RecipeComponent { ProductId = empanada.Id, StockItemId = raw.Id, Quantity = 80m },
            new RecipeComponent { ProductId = chickenEmpanada.Id, StockItemId = raw.Id, Quantity = 70m });
        db.BusinessAssets.Add(new BusinessAsset
        {
            Key = DemoCatalogSeeder.SeedMarker,
            ContentType = "text/plain",
            Data = [],
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.False(await DemoCatalogSeeder.EnsureSeededAsync(db));

        var updatedAsadito = await db.Products.SingleAsync(item => item.Id == asadito.Id);
        var updatedEmpanada = await db.Products.SingleAsync(item => item.Id == empanada.Id);
        var updatedChickenEmpanada = await db.Products.SingleAsync(item => item.Id == chickenEmpanada.Id);
        Assert.False(updatedAsadito.IsRecipe);
        Assert.NotNull(updatedAsadito.StockItemId);
        Assert.False(updatedEmpanada.IsRecipe);
        Assert.Equal(frozenStock.Id, updatedEmpanada.StockItemId);
        Assert.False(updatedChickenEmpanada.IsRecipe);
        Assert.NotNull(updatedChickenEmpanada.StockItemId);
        Assert.Null((await db.Products.SingleAsync(item => item.Id == recharge.Id)).StockItemId);
        Assert.False((await db.Products.SingleAsync(item => item.Id == duplicateFrozen.Id)).IsActive);
        Assert.Empty(await db.RecipeComponents.ToListAsync());
        Assert.False(await db.StockItems.AnyAsync(item => item.Id == rechargeStock.Id));
        var departments = await db.ProductDetails.ToDictionaryAsync(item => item.ProductId, item => item.Department);
        Assert.Equal(ProductDepartment.FrozenBatch, departments[asadito.Id]);
        Assert.Equal(ProductDepartment.FrozenBatch, departments[empanada.Id]);
        Assert.Equal(ProductDepartment.FrozenBatch, departments[chickenEmpanada.Id]);
        Assert.Equal(ProductDepartment.Service, departments[recharge.Id]);
    }

    private static (Product Product, int LegacyDepartment) LegacyProduct(
        string sku, bool isRecipe, int department, Guid? stockItemId) =>
        (new Product
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            Name = sku,
            Price = 1m,
            IsRecipe = isRecipe,
            StockItemId = stockItemId
        }, department);

    private static Task<StockItem> CreateStockAsync(
        CommerceService commerce, string name, string unit, decimal quantity) =>
        commerce.CreateStockItemAsync(
            new CreateStockItemRequest(name, unit, quantity, 0m, 1m),
            "owner", CancellationToken.None);

    private static async Task SellAsync(TiendaDbContext db, Product product, decimal quantity)
    {
        await new SaleService(db).CreateAsync(
            new CreateSaleRequest(
                Guid.NewGuid(),
                "CLASSIFICATION-POS",
                [new SaleLineRequest(product.Id, quantity, product.Price)],
                PaymentMethod.Cash,
                product.Price * quantity,
                DateTimeOffset.UtcNow),
            true,
            CancellationToken.None);
    }
}
