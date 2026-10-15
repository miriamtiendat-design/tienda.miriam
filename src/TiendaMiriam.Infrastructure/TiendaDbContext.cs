using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class TiendaDbContext(DbContextOptions<TiendaDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<RecipeComponent> RecipeComponents => Set<RecipeComponent>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockProductionBatch> StockProductionBatches => Set<StockProductionBatch>();
    public DbSet<StockProductionLine> StockProductionLines => Set<StockProductionLine>();
    public DbSet<StockLot> StockLots => Set<StockLot>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<CustomPaymentType> CustomPaymentTypes => Set<CustomPaymentType>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<ProductListPrice> ProductListPrices => Set<ProductListPrice>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<PromotionBundle> PromotionBundles => Set<PromotionBundle>();
    public DbSet<PromotionBundleComponent> PromotionBundleComponents => Set<PromotionBundleComponent>();
    public DbSet<PromotionBundleSaleSnapshot> PromotionBundleSaleSnapshots => Set<PromotionBundleSaleSnapshot>();
    public DbSet<SyncOutboxItem> SyncOutbox => Set<SyncOutboxItem>();
    public DbSet<CustomerChangeOutboxItem> CustomerChangeOutbox => Set<CustomerChangeOutboxItem>();
    public DbSet<PromotionBundleOutboxItem> PromotionBundleOutbox => Set<PromotionBundleOutboxItem>();
    public DbSet<TerminalStatus> TerminalStatuses => Set<TerminalStatus>();
    public DbSet<ProductPhoto> ProductPhotos => Set<ProductPhoto>();
    public DbSet<BusinessAsset> BusinessAssets => Set<BusinessAsset>();
    public DbSet<ProductDetails> ProductDetails => Set<ProductDetails>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<CashRegisterSession> CashRegisterSessions => Set<CashRegisterSession>();
    public DbSet<CustomerAccount> Customers => Set<CustomerAccount>();
    public DbSet<CustomerLedgerEntry> CustomerLedger => Set<CustomerLedgerEntry>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierStockItem> SupplierStockItems => Set<SupplierStockItem>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<HeldSale> HeldSales => Set<HeldSale>();
    public DbSet<KitchenTicket> KitchenTickets => Set<KitchenTicket>();
    public DbSet<KitchenTicketLine> KitchenTicketLines => Set<KitchenTicketLine>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AuditOutboxItem> AuditOutbox => Set<AuditOutboxItem>();
    public DbSet<SaleCostSnapshot> SaleCostSnapshots => Set<SaleCostSnapshot>();
    public DbSet<SaleLineFinancialSnapshot> SaleLineFinancialSnapshots => Set<SaleLineFinancialSnapshot>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareAuditChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareAuditChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareAuditChanges()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<AuditEntry>()
            .Where(entry => entry.State == EntityState.Added).ToArray())
        {
            QueueAuditForSync(entry.Entity);
        }
        var trackedAuditEntries = ChangeTracker.Entries<AuditEntry>()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .ToArray();
        foreach (var entry in trackedAuditEntries)
        {
            var auditId = entry.Entity.Id;
            var attemptedState = entry.State.ToString();
            entry.State = EntityState.Unchanged;
            AddAudit(
                "AUDIT_TAMPER_ATTEMPT",
                "AuditEntry",
                auditId,
                attemptedState,
                "Registro inmutable",
                "Intento de modificar o eliminar una auditoría bloqueado.");
        }

        var pending = ChangeTracker.Entries()
            .Where(entry => entry.Entity is not AuditEntry and not AuditOutboxItem
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(CreateAuditRecord)
            .Where(record => record is not null)
            .Cast<AuditEntry>()
            .ToArray();
        if (pending.Length > 0)
        {
            AuditExecutionContext.ClearObservation();
        }
        foreach (var audit in pending)
        {
            AddAudit(audit.Action, audit.EntityType, audit.EntityId,
                audit.PreviousValue, audit.NewValue, audit.Observation ?? audit.Reason,
                audit.Action, audit.Actor, audit.TerminalId, audit.OccurredAtUtc, audit.Observation);
        }
    }

    private AuditEntry? CreateAuditRecord(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var type = entry.Metadata.ClrType.Name;
        var entity = entry.Entity;
        var state = entry.State;
        if (type == nameof(StockItem) && state == EntityState.Modified
            && entry.Property(nameof(StockItem.OnHand)).IsModified
            && entry.Properties.Where(property => property.IsModified)
                .All(property => property.Metadata.Name == nameof(StockItem.OnHand))
            && ChangeTracker.Entries<StockMovement>().Any(movement =>
                movement.State == EntityState.Added && movement.Entity.StockItemId == ReadGuid(entity, "Id")))
        {
            return null;
        }
        var fields = ChangedValues(entry);
        var (action, previous, current) = DescribeAuditChange(type, entity, state, fields);
        if (action is null)
        {
            return null;
        }

        var entityId = ReadGuid(entity, "Id")
            ?? ReadGuid(entity, "ProductId")
            ?? ReadGuid(entity, "SupplierId")
            ?? ReadGuid(entity, "SaleId")
            ?? ReadGuid(entity, "CustomerId")
            ?? Guid.Empty;
        var actor = ReadString(entity, "Actor") ?? ReadString(entity, "OpenedBy")
            ?? ReadString(entity, "ClosedBy") ?? AuditExecutionContext.Actor;
        var terminal = ReadString(entity, "TerminalId") ?? ReadGuid(entity, "CashRegisterSessionId")?.ToString("N")
            ?? AuditExecutionContext.TerminalId;
        return new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = action,
            EntityType = type,
            EntityId = entityId,
            Reason = current ?? action,
            OccurredAtUtc = DateTime.UtcNow,
            TerminalId = terminal,
            PreviousValue = previous,
            NewValue = current,
            Observation = AuditExecutionContext.Observation
        };
    }

    private static (string? Action, string? Previous, string? Current) DescribeAuditChange(
        string type, object entity, EntityState state, string? changes)
    {
        var name = ReadString(entity, "Name") ?? ReadString(entity, "Description")
            ?? ReadString(entity, "InvoiceReference") ?? ReadString(entity, "Key")
            ?? ReadGuid(entity, "Id")?.ToString("N") ?? type;
        if (type == nameof(Product))
        {
            if (state == EntityState.Added) return ("PRODUCT_CREATED", null, name);
            if (state == EntityState.Deleted) return ("PRODUCT_DEACTIVATED", name, null);
            if (changes?.Contains("IsActive", StringComparison.Ordinal) == true)
                return ReadProperty(entity, "IsActive") is true
                    ? ("PRODUCT_ACTIVATED", FindValue(changes, "IsActive", false), "Activo")
                    : ("PRODUCT_DEACTIVATED", FindValue(changes, "IsActive", false), "Baja lógica");
            if (changes?.Contains("Price", StringComparison.Ordinal) == true)
                return ("PRODUCT_PRICE_CHANGED", FindValue(changes, "Price", false), FindValue(changes, "Price", true));
            return ("PRODUCT_UPDATED", "Datos previos registrados", changes);
        }
        if (type == nameof(StockItem))
        {
            if (state == EntityState.Added) return ("STOCK_ITEM_CREATED", null, name);
            if (state == EntityState.Deleted) return ("STOCK_ITEM_DELETED", name, null);
            if (changes?.Contains("OnHand", StringComparison.Ordinal) == true)
                return ("STOCK_MANUAL_ADJUSTMENT", FindValue(changes, "OnHand", false), FindValue(changes, "OnHand", true));
            if (changes?.Contains("UnitCost", StringComparison.Ordinal) == true)
                return ("STOCK_COST_CHANGED", FindValue(changes, "UnitCost", false), FindValue(changes, "UnitCost", true));
            return ("STOCK_ITEM_UPDATED", "Datos previos registrados", changes);
        }
        if (type == nameof(ProductDetails))
            return ("PRODUCT_CATEGORY_CHANGED", "Categoría anterior", changes);
        if (type == nameof(Sale) && state == EntityState.Modified)
        {
            if (changes?.Contains("State", StringComparison.Ordinal) == true
                && ReadProperty(entity, "State") is SaleState saleState && saleState == SaleState.Voided)
                return ("SALE_VOIDED", FindValue(changes!, "State", false), "Voided");
            if (changes?.Contains("Total", StringComparison.Ordinal) == true)
                return ("SALE_TOTAL_MODIFIED", FindValue(changes, "Total", false), FindValue(changes, "Total", true));
            return ("SALE_MODIFIED", "Venta anterior", changes);
        }
        if (type == nameof(SaleLine) && state == EntityState.Added
            && ReadDecimal(entity, "DiscountAmount") > 0)
            return ("MANUAL_DISCOUNT_APPLIED", null, changes);
        if (type == nameof(CashRegisterSession))
        {
            if (state == EntityState.Added) return ("CASH_REGISTER_OPENED", null, changes);
            if (changes?.Contains("ClosedAtUtc", StringComparison.Ordinal) == true)
                return ("CASH_REGISTER_CLOSED", "Caja abierta", "Caja cerrada");
            if (changes?.Contains("OpeningCash", StringComparison.Ordinal) == true)
                return ("CASH_REGISTER_OPENING_CASH_CHANGED",
                    FindValue(changes, "OpeningCash", false), FindValue(changes, "OpeningCash", true));
            return ("CASH_REGISTER_UPDATED", "Datos previos registrados", changes);
        }
        if (type == nameof(Expense) && state == EntityState.Added)
            return ("MANUAL_EXPENSE_RECORDED", null, changes);
        if (type == nameof(CustomerAccount))
            return state == EntityState.Added ? ("CUSTOMER_CREATED", null, name)
                : ("CUSTOMER_UPDATED", "Datos anteriores", changes);
        if (type == nameof(CustomerLedgerEntry) && state == EntityState.Added
            && ReadGuid(entity, "SaleId") is null)
            return ("CUSTOMER_BALANCE_CHANGED", null, changes);
        if (type == nameof(Supplier))
            return state == EntityState.Added ? ("SUPPLIER_CREATED", null, name)
                : ("SUPPLIER_UPDATED", "Datos anteriores", changes);
        if (type == nameof(PurchaseOrder) && state == EntityState.Added)
            return ("SUPPLIER_PURCHASE_RECORDED", null, changes);
        if (type == nameof(SupplierPayment) && state == EntityState.Added)
            return ("SUPPLIER_PAYMENT_RECORDED", null, changes);
        if (type == nameof(Promotion) || type == nameof(PromotionBundle))
        {
            if (state == EntityState.Added) return ("PROMOTION_CREATED", null, name);
            if (state == EntityState.Deleted) return ("PROMOTION_DELETED", name, null);
            if (changes?.Contains("IsDeleted", StringComparison.Ordinal) == true
                && ReadProperty(entity, "IsDeleted") is true)
                return ("PROMOTION_DELETED", "Activa", "Eliminada");
            return ("PROMOTION_UPDATED", "Datos anteriores", changes);
        }
        if (type == nameof(AppUser))
            return state == EntityState.Added ? ("USER_CREATED", null, ReadString(entity, "Username"))
                : ("USER_UPDATED", "Permisos/datos anteriores", changes?.Contains("PasswordHash", StringComparison.Ordinal) == true
                    ? "Contraseña modificada (valor protegido)" : changes);
        if (type == nameof(BusinessAsset) && state is EntityState.Added or EntityState.Modified)
        {
            var key = ReadString(entity, "Key") ?? "configuración";
            if (key.Contains("logo", StringComparison.OrdinalIgnoreCase))
                return ("BUSINESS_LOGO_CHANGED", "Imagen anterior", "Imagen actualizada");
            if (key.Contains("shortcut", StringComparison.OrdinalIgnoreCase))
                return ("SHORTCUTS_CHANGED", "Configuración anterior", "Configuración actualizada");
            return ("BUSINESS_SETTINGS_CHANGED", "Configuración anterior", key);
        }
        return (null, null, null);
    }

    private void AddAudit(
        string action, string entityType, Guid entityId, string? previous, string? current,
        string reason, string? syncAction = null, string? actor = null, string? terminalId = null,
        DateTime? occurredAt = null, string? observation = null)
    {
        var audit = new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor ?? AuditExecutionContext.Actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Reason = reason.Length > 500 ? reason[..500] : reason,
            OccurredAtUtc = occurredAt ?? DateTime.UtcNow,
            TerminalId = terminalId ?? AuditExecutionContext.TerminalId,
            PreviousValue = previous,
            NewValue = current,
            Observation = observation
        };
        AuditEntries.Add(audit);
        QueueAuditForSync(audit);
    }

    private void QueueAuditForSync(AuditEntry audit)
    {
        if (AuditOutbox.Local.Any(item => item.AuditEntryId == audit.Id))
        {
            return;
        }
        AuditOutbox.Add(new AuditOutboxItem
        {
            Id = Guid.NewGuid(),
            AuditEntryId = audit.Id,
            Payload = JsonSerializer.Serialize(audit),
            CreatedAtUtc = audit.OccurredAtUtc,
            IsDelivered = false
        });
    }

    private static string? ChangedValues(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
            return JsonSerializer.Serialize(entry.Properties
                .Where(property => property.Metadata.Name is not ("PasswordHash" or "Data" or "Payload"))
                .ToDictionary(property => property.Metadata.Name, property => property.CurrentValue));
        var changes = entry.Properties.Where(property => property.IsModified)
            .Where(property => property.Metadata.Name is not ("Data" or "Payload"))
            .ToDictionary(property => property.Metadata.Name, property => new
            {
                Previous = property.Metadata.Name is "PasswordHash" ? "Protegido" : property.OriginalValue,
                Current = property.Metadata.Name is "PasswordHash" ? "Protegido" : property.CurrentValue
            });
        return changes.Count == 0 ? null : JsonSerializer.Serialize(changes);
    }

    private static string? FindValue(string changes, string property, bool current)
    {
        try
        {
            using var document = JsonDocument.Parse(changes);
            if (document.RootElement.TryGetProperty(property, out var value)
                && value.TryGetProperty(current ? "Current" : "Previous", out var result))
                return result.ToString();
        }
        catch (JsonException) { }
        return null;
    }

    private static string? ReadString(object entity, string property) =>
        entity.GetType().GetProperty(property)?.GetValue(entity) as string;
    private static object? ReadProperty(object entity, string property) =>
        entity.GetType().GetProperty(property)?.GetValue(entity);
    private static Guid? ReadGuid(object entity, string property) =>
        entity.GetType().GetProperty(property)?.GetValue(entity) as Guid?;
    private static decimal ReadDecimal(object entity, string property) =>
        entity.GetType().GetProperty(property)?.GetValue(entity) is decimal value ? value : 0;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Sku).IsUnique();
            entity.Property(x => x.Price).HasPrecision(18, 4);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Sku).HasMaxLength(64);
            entity.Property(x => x.Barcode).HasMaxLength(64);
            entity.HasIndex(x => x.Barcode).IsUnique();
        });
        modelBuilder.Entity<PriceList>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.HasIndex(x => x.Name).IsUnique();
        });
        modelBuilder.Entity<ProductListPrice>(entity =>
        {
            entity.HasKey(x => new { x.PriceListId, x.ProductId });
            entity.Property(x => x.Price).HasPrecision(18, 4);
            entity.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Promotion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Value).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.ProductId, x.IsActive, x.StartsAtUtc, x.EndsAtUtc });
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PromotionBundle>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Price).HasPrecision(18, 2);
            entity.HasIndex(x => new { x.IsActive, x.IsDeleted, x.StartsAtUtc, x.EndsAtUtc });
            entity.HasMany(x => x.Components).WithOne()
                .HasForeignKey(x => x.PromotionBundleId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PromotionBundleComponent>(entity =>
        {
            entity.HasKey(x => new { x.PromotionBundleId, x.ProductId });
            entity.Property(x => x.Quantity).HasPrecision(18, 6);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PromotionBundleSaleSnapshot>(entity =>
        {
            entity.HasKey(x => x.SaleLineId);
            entity.Property(x => x.RegularAmount).HasPrecision(18, 2);
            entity.Property(x => x.DiscountAmount).HasPrecision(18, 2);
            entity.HasOne<SaleLine>().WithOne()
                .HasForeignKey<PromotionBundleSaleSnapshot>(x => x.SaleLineId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StockLot>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LotCode).HasMaxLength(80);
            entity.Property(x => x.ReceivedQuantity).HasPrecision(18, 6);
            entity.Property(x => x.RemainingQuantity).HasPrecision(18, 6).IsConcurrencyToken();
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.StockItemId, x.ExpiresAtUtc });
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<StockCount>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockCountId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StockCountLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExpectedQuantity).HasPrecision(18, 6);
            entity.Property(x => x.CountedQuantity).HasPrecision(18, 6);
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CustomPaymentType>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.HasIndex(x => x.Name).IsUnique();
        });
        modelBuilder.Entity<StockItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OnHand).HasPrecision(18, 6).IsConcurrencyToken();
            entity.Property(x => x.ReorderAt).HasPrecision(18, 6);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Unit).HasMaxLength(24);
        });
        modelBuilder.Entity<RecipeComponent>(entity =>
        {
            entity.HasKey(x => new { x.ProductId, x.StockItemId });
            entity.Property(x => x.Quantity).HasPrecision(18, 6);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Sale>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Total).HasPrecision(18, 2);
            entity.Property(x => x.TerminalId).HasMaxLength(80);
            entity.Property(x => x.DiningTable).HasMaxLength(40);
            entity.Property(x => x.NamedOrder).HasMaxLength(80);
            entity.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.NamedOrder).HasMaxLength(80);
            entity.HasIndex(x => x.CashRegisterSessionId);
            entity.HasIndex(x => x.HeldSaleId);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SaleLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Quantity).HasPrecision(18, 6);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 4);
            entity.Property(x => x.LineTotal).HasPrecision(18, 2);
            entity.Property(x => x.ProductName).HasMaxLength(160);
            entity.Property(x => x.Notes).HasMaxLength(160);
            entity.HasOne<Promotion>().WithMany().HasForeignKey(x => x.AppliedPromotionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PromotionBundle>().WithMany().HasForeignKey(x => x.PromotionBundleId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Change).HasPrecision(18, 2);
            entity.Property(x => x.PaymentTypeName).HasMaxLength(80);
            entity.HasOne<CustomPaymentType>().WithMany().HasForeignKey(x => x.PaymentTypeId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuantityDelta).HasPrecision(18, 6);
            entity.Property(x => x.Reason).HasMaxLength(80);
            entity.HasIndex(x => new { x.StockItemId, x.OccurredAtUtc });
        });
        modelBuilder.Entity<StockProductionBatch>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuantityProduced).HasPrecision(18, 6);
            entity.Property(x => x.TotalCost).HasPrecision(18, 4);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.FinishedStockItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ProductionBatchId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ProductId, x.OccurredAtUtc });
        });
        modelBuilder.Entity<StockProductionLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuantityConsumed).HasPrecision(18, 6);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.TotalCost).HasPrecision(18, 4);
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SyncOutboxItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.SaleId, x.IsDelivered });
            entity.Property(x => x.Payload).HasColumnType("TEXT");
        });
        modelBuilder.Entity<CustomerChangeOutboxItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Kind, x.IsDelivered, x.CreatedAtUtc });
            entity.Property(x => x.Payload).HasColumnType("TEXT");
            entity.Property(x => x.LastError).HasMaxLength(1000);
        });
        modelBuilder.Entity<PromotionBundleOutboxItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.IsDelivered, x.CreatedAtUtc });
            entity.Property(x => x.Payload).HasColumnType("TEXT");
            entity.Property(x => x.LastError).HasMaxLength(1000);
        });
        modelBuilder.Entity<TerminalStatus>(entity =>
        {
            entity.HasKey(x => x.TerminalId);
            entity.Property(x => x.TerminalId).HasMaxLength(80);
        });
        modelBuilder.Entity<ProductPhoto>(entity =>
        {
            entity.HasKey(x => x.ProductId);
            entity.Property(x => x.ContentType).HasMaxLength(64);
            entity.HasOne<Product>().WithOne().HasForeignKey<ProductPhoto>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BusinessAsset>(entity =>
        {
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(80);
            entity.Property(x => x.ContentType).HasMaxLength(64);
            entity.Property(x => x.Data).HasColumnType(
                Database.IsNpgsql() ? "bytea" : "BLOB");
        });
        modelBuilder.Entity<ProductDetails>(entity =>
        {
            entity.HasKey(x => x.ProductId);
            entity.Property(x => x.PrintStation).HasMaxLength(80);
            entity.Property(x => x.ServiceUnitCost).HasPrecision(18, 4);
            entity.HasOne<Product>().WithOne().HasForeignKey<ProductDetails>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(64);
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.Property(x => x.AuthVersion).IsConcurrencyToken();
            entity.HasIndex(x => x.Username).IsUnique();
        });
        modelBuilder.Entity<CashRegisterSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TerminalId).HasMaxLength(80);
            entity.Property(x => x.OpenedBy).HasMaxLength(64);
            entity.Property(x => x.ClosedBy).HasMaxLength(64);
            entity.Property(x => x.OpeningCash).HasPrecision(18, 2);
            entity.Property(x => x.CountedCash).HasPrecision(18, 2);
            entity.Property(x => x.ExpectedCash).HasPrecision(18, 2);
            entity.Property(x => x.Variance).HasPrecision(18, 2);
            entity.Property(x => x.ClosingNote).HasMaxLength(300);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.SyncPending);
            entity.HasIndex(x => new { x.TerminalId, x.ClosedAtUtc });
        });
        modelBuilder.Entity<CustomerAccount>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Phone).HasMaxLength(32);
            entity.Property(x => x.Address).HasMaxLength(240);
            entity.Property(x => x.TaxId).HasMaxLength(40);
            entity.Property(x => x.Notes).HasMaxLength(500);
            entity.Property(x => x.WalletBalance).HasPrecision(18, 2);
            entity.Property(x => x.WalletBalance).IsConcurrencyToken();
            entity.Property(x => x.CreditBalance).HasPrecision(18, 2).IsConcurrencyToken();
            entity.Property(x => x.CreditLimit).HasPrecision(18, 2);
            entity.HasIndex(x => x.Phone);
        });
        modelBuilder.Entity<CustomerLedgerEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WalletDelta).HasPrecision(18, 2);
            entity.Property(x => x.CreditDelta).HasPrecision(18, 2);
            entity.Property(x => x.PaymentMethod).IsRequired();
            entity.HasIndex(x => x.CashRegisterSessionId);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.Property(x => x.Description).HasMaxLength(300);
            entity.HasIndex(x => new { x.CustomerId, x.OccurredAtUtc });
            entity.HasOne<CustomerAccount>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Expense>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Description).HasMaxLength(300);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.PaymentMethod).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => x.PurchaseOrderId).IsUnique();
            entity.HasOne<CashRegisterSession>().WithMany()
                .HasForeignKey(x => x.CashRegisterSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PurchaseOrder>().WithOne()
                .HasForeignKey<Expense>(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.TaxId).HasMaxLength(40);
            entity.Property(x => x.Phone).HasMaxLength(32);
            entity.Property(x => x.Address).HasMaxLength(240);
            entity.Property(x => x.ContactName).HasMaxLength(120);
        });
        modelBuilder.Entity<SupplierStockItem>(entity =>
        {
            entity.HasKey(x => new { x.SupplierId, x.StockItemId });
            entity.Property(x => x.LastUnitCost).HasPrecision(18, 4);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.SupplierId, x.LastPurchasedAtUtc });
        });
        modelBuilder.Entity<PurchaseOrder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Total).HasPrecision(18, 2);
            entity.Property(x => x.AmountPaid).HasPrecision(18, 2).IsConcurrencyToken();
            entity.Property(x => x.InvoiceReference).HasMaxLength(100);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.HasIndex(x => new { x.SupplierId, x.ReceivedAtUtc });
            entity.HasIndex(x => new { x.SupplierId, x.DueAtUtc });
            entity.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CashRegisterSession>().WithMany()
                .HasForeignKey(x => x.CashRegisterSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Lines).WithOne(x => x.PurchaseOrder)
                .HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SupplierPayment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.PaymentMethod).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.Property(x => x.Note).HasMaxLength(160);
            entity.HasIndex(x => new { x.PurchaseOrderId, x.OccurredAtUtc });
            entity.HasOne<PurchaseOrder>().WithMany()
                .HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CashRegisterSession>().WithMany()
                .HasForeignKey(x => x.CashRegisterSessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PurchaseLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Quantity).HasPrecision(18, 6);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.LineTotal).HasPrecision(18, 2);
            entity.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<HeldSale>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TerminalId).HasMaxLength(80);
            entity.Property(x => x.Payload).HasColumnType("TEXT");
            entity.Property(x => x.DiningTable).HasMaxLength(40);
            entity.Property(x => x.NamedOrder).HasMaxLength(80);
            entity.Property(x => x.KitchenNotes).HasMaxLength(160);
            entity.Property(x => x.NamedOrder).HasMaxLength(80);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerAccount>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TerminalId, x.UpdatedAtUtc });
        });
        modelBuilder.Entity<KitchenTicket>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.SaleId).IsUnique();
            entity.Property(x => x.DiningTable).HasMaxLength(40);
            entity.Property(x => x.NamedOrder).HasMaxLength(80);
            entity.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.KitchenTicketId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KitchenTicketLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductName).HasMaxLength(160);
            entity.Property(x => x.Notes).HasMaxLength(160);
            entity.Property(x => x.Quantity).HasPrecision(18, 6);
        });
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.Property(x => x.Action).HasMaxLength(80);
            entity.Property(x => x.EntityType).HasMaxLength(80);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAtUtc });
        });
        modelBuilder.Entity<AuditOutboxItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.IsDelivered, x.CreatedAtUtc });
            entity.HasIndex(x => x.AuditEntryId).IsUnique();
            entity.Property(x => x.Payload).HasColumnType("TEXT");
            entity.Property(x => x.LastError).HasMaxLength(1000);
        });
        modelBuilder.Entity<SaleCostSnapshot>(entity =>
        {
            entity.HasKey(x => x.SaleId);
            entity.Property(x => x.Cost).HasPrecision(18, 2);
            entity.HasOne<Sale>().WithOne().HasForeignKey<SaleCostSnapshot>(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SaleLineFinancialSnapshot>(entity =>
        {
            entity.HasKey(x => x.SaleLineId);
            entity.Property(x => x.UnitCost).HasPrecision(28, 10);
            entity.Property(x => x.CostOfGoods).HasPrecision(28, 10);
            entity.Property(x => x.GrossMargin).HasPrecision(28, 10);
            entity.Property(x => x.GrossMarginPercent).HasPrecision(18, 6);
            entity.HasOne<SaleLine>().WithOne()
                .HasForeignKey<SaleLineFinancialSnapshot>(x => x.SaleLineId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
