namespace TiendaMiriam.Domain;

public enum PaymentMethod
{
    Cash,
    Transfer,
    CustomerWallet,
    Credit,
    Custom
}

public enum SaleState
{
    Completed,
    OnHold,
    Voided
}

public enum FulfillmentMode
{
    Counter,
    DineIn,
    Takeaway
}

public enum PromotionKind
{
    PercentOff,
    FixedPrice
}

public sealed class Product
{
    public Guid Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? StockItemId { get; set; }
    public bool IsRecipe { get; set; }
}

public sealed class PriceList
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProductListPrice
{
    public Guid PriceListId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Price { get; set; }
}

public sealed class Promotion
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public PromotionKind Kind { get; set; }
    public decimal Value { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class PromotionBundle
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public DateTime? StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public List<PromotionBundleComponent> Components { get; set; } = [];
}

public sealed class PromotionBundleComponent
{
    public Guid PromotionBundleId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class PromotionBundleSaleSnapshot
{
    public Guid SaleLineId { get; set; }
    public decimal RegularAmount { get; set; }
    public decimal DiscountAmount { get; set; }
}

public sealed class StockItem
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Unit { get; set; }
    public decimal OnHand { get; set; }
    public decimal ReorderAt { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class RecipeComponent
{
    public Guid ProductId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class Sale
{
    public Guid Id { get; set; }
    public required string TerminalId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public decimal Total { get; set; }
    public string? DiningTable { get; set; }
    public string? NamedOrder { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Counter;
    public Guid? PriceListId { get; set; }
    public Guid? CashRegisterSessionId { get; set; }
    public Guid? HeldSaleId { get; set; }
    public SaleState State { get; set; } = SaleState.Completed;
    public bool IsSynced { get; set; }
    public List<SaleLine> Lines { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public sealed class SaleLine
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    public required string ProductName { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? Notes { get; set; }
    public Guid? AppliedPromotionId { get; set; }
    public Guid? PromotionBundleId { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public PaymentMethod Method { get; set; }
    public Guid? PaymentTypeId { get; set; }
    public string? PaymentTypeName { get; set; }
    public decimal Amount { get; set; }
    public decimal Change { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class StockMovement
{
    public Guid Id { get; set; }
    public Guid StockItemId { get; set; }
    public Guid SaleId { get; set; }
    public Guid? ProductionBatchId { get; set; }
    public Guid? StockLotId { get; set; }
    public decimal QuantityDelta { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class StockProductionBatch
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid FinishedStockItemId { get; set; }
    public decimal QuantityProduced { get; set; }
    public decimal TotalCost { get; set; }
    public required string Actor { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public bool SyncPending { get; set; } = true;
    public List<StockProductionLine> Lines { get; set; } = [];
}

public sealed class StockProductionLine
{
    public Guid Id { get; set; }
    public Guid ProductionBatchId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal QuantityConsumed { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
}

public sealed class StockLot
{
    public Guid Id { get; set; }
    public Guid StockItemId { get; set; }
    public required string LotCode { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public sealed class StockCount
{
    public Guid Id { get; set; }
    public required string Actor { get; set; }
    public required string Reason { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public List<StockCountLine> Lines { get; set; } = [];
}

public sealed class StockCountLine
{
    public Guid Id { get; set; }
    public Guid StockCountId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
}

public sealed class CustomPaymentType
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SyncOutboxItem
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool IsDelivered { get; set; }
}

public sealed class CustomerChangeOutboxItem
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public CustomerSyncChangeKind Kind { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool IsDelivered { get; set; }
}

public sealed class PromotionBundleOutboxItem
{
    public Guid Id { get; set; }
    public Guid PromotionBundleId { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool IsDelivered { get; set; }
}

public sealed class TerminalStatus
{
    public required string TerminalId { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public int PendingSales { get; set; }
}

/// <summary>One product line requested for a sale.</summary>
public sealed record SaleLineRequest(
    Guid ProductId,
    decimal Quantity,
    decimal UnitPriceAtSale,
    string? RecipeFingerprint = null,
    Guid? AppliedPromotionId = null,
    string? Notes = null,
    Guid? PromotionBundleId = null);
/// <summary>A payment component; cash Amount is tendered and other methods are applied.</summary>
public sealed record SalePaymentRequest(PaymentMethod Method, decimal Amount, Guid? PaymentTypeId = null);
public sealed record TerminalStatusUpdate(string TerminalId, int PendingSales);

/// <summary>Sale transaction accepted by the local POS and synchronization API.</summary>
public sealed record CreateSaleRequest(
    Guid SaleId,
    string TerminalId,
    IReadOnlyList<SaleLineRequest>? Lines,
    PaymentMethod PaymentMethod,
    decimal AmountTendered,
    DateTimeOffset OccurredAtUtc,
    Guid? CustomerId = null,
    string? DiningTable = null,
    string? KitchenNotes = null,
    string? NamedOrder = null,
    FulfillmentMode FulfillmentMode = FulfillmentMode.Counter,
    IReadOnlyList<SalePaymentRequest>? Payments = null,
    Guid? PriceListId = null,
    Guid? CashRegisterSessionId = null,
    Guid? HeldSaleId = null,
    int HeldSaleVersion = 0);

public sealed record SaleResult(Guid SaleId, decimal Total, decimal Change, bool AlreadyProcessed);

public sealed record MobileOverview(
    decimal SalesToday,
    int SalesCountToday,
    int ProductsLowOnStock,
    int PendingOutboxItems,
    int OnlineTerminals);
