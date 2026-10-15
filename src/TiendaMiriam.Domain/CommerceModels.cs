namespace TiendaMiriam.Domain;

public enum AppRole
{
    Administrator,
    Cashier,
    Kitchen
}

public sealed class AppUser
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public AppRole Role { get; set; }
    public Guid AuthVersion { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
}

public sealed class CashRegisterSession
{
    public Guid Id { get; set; }
    public required string TerminalId { get; set; }
    public required string OpenedBy { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public decimal OpeningCash { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? Variance { get; set; }
    public string? ClosingNote { get; set; }
    public int Version { get; set; }
    public bool SyncPending { get; set; }
}

public enum CustomerLedgerKind
{
    WalletTopUp,
    WalletPurchase,
    CreditSale,
    CreditPayment,
    Reversal
}

public enum KitchenTicketState
{
    Queued,
    Preparing,
    Ready,
    Served,
    Cancelled
}

public enum ProductDepartment
{
    Retail = 0,
    Grocery = Retail,
    Cleaning = Retail,
    Other = Retail,
    PreparedDirect = 1,
    PreparedFood = PreparedDirect,
    Service = 2,
    MobileRecharge = Service,
    RawIngredient = 3,
    FrozenBatch = 4
}

public sealed class ProductDetails
{
    public Guid ProductId { get; set; }
    public ProductDepartment Department { get; set; }
    public string? PrintStation { get; set; }
    public decimal ServiceUnitCost { get; set; }
}

public sealed class ProductPhoto
{
    public Guid ProductId { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class BusinessAsset
{
    public required string Key { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CustomerAccount
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? TaxId { get; set; }
    public string? Notes { get; set; }
    public decimal WalletBalance { get; set; }
    public decimal CreditBalance { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class CustomerLedgerEntry
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? SaleId { get; set; }
    public CustomerLedgerKind Kind { get; set; }
    public decimal WalletDelta { get; set; }
    public decimal CreditDelta { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public Guid? CashRegisterSessionId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public required string Actor { get; set; }
    public required string Description { get; set; }
}

public enum CustomerSyncChangeKind
{
    ProfileUpsert,
    CreditPayment
}

public sealed record CustomerSyncChange(
    Guid EventId,
    Guid CustomerId,
    CustomerSyncChangeKind Kind,
    CustomerProfileSync? Profile = null,
    CustomerCreditPaymentSync? CreditPayment = null);

public sealed record CustomerProfileSync(
    string Name,
    string? Phone,
    string? Address,
    string? TaxId,
    string? Notes,
    decimal CreditLimit);

public sealed record CustomerCreditPaymentSync(
    Guid LedgerEntryId,
    decimal Amount,
    string Description,
    string Actor,
    PaymentMethod PaymentMethod,
    DateTimeOffset OccurredAt,
    Guid? CashRegisterSessionId = null);

public sealed class Expense
{
    public Guid Id { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public Guid? CashRegisterSessionId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public required string Actor { get; set; }
}

public sealed class Supplier
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? TaxId { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? ContactName { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SupplierStockItem
{
    public Guid SupplierId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal LastUnitCost { get; set; }
    public DateTime LastPurchasedAtUtc { get; set; }
}

public sealed class PurchaseOrder
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? CashRegisterSessionId { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Credit;
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public required string InvoiceReference { get; set; }
    public required string Actor { get; set; }
    public List<PurchaseLine> Lines { get; set; } = [];
}

public sealed class SupplierPayment
{
    public Guid Id { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid CashRegisterSessionId { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public required string Actor { get; set; }
    public string? Note { get; set; }
}

public sealed class PurchaseLine
{
    public Guid Id { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
    public Guid StockItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class HeldSale
{
    public Guid Id { get; set; }
    public required string TerminalId { get; set; }
    public required string Payload { get; set; }
    public string? DiningTable { get; set; }
    public string? NamedOrder { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Counter;
    public Guid? PriceListId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? KitchenNotes { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int Version { get; set; }
    public bool SyncPending { get; set; }
    public int ExpectedVersion { get; set; }
    public bool ClosePending { get; set; }
    public bool CheckoutRequested { get; set; }
    public bool DeletePending { get; set; }
}

public sealed class KitchenTicket
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public KitchenTicketState State { get; set; } = KitchenTicketState.Queued;
    public string? DiningTable { get; set; }
    public string? NamedOrder { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Counter;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<KitchenTicketLine> Lines { get; set; } = [];
}

public sealed class KitchenTicketLine
{
    public Guid Id { get; set; }
    public Guid KitchenTicketId { get; set; }
    public required string ProductName { get; set; }
    public decimal Quantity { get; set; }
    public string? Notes { get; set; }
}

public sealed class AuditEntry
{
    public Guid Id { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public required string Reason { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string? TerminalId { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? Observation { get; set; }
}

public sealed class AuditOutboxItem
{
    public Guid Id { get; set; }
    public Guid AuditEntryId { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool IsDelivered { get; set; }
}

public sealed class SaleCostSnapshot
{
    public Guid SaleId { get; set; }
    public decimal Cost { get; set; }
}

public sealed class SaleLineFinancialSnapshot
{
    public Guid SaleLineId { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? CostOfGoods { get; set; }
    public decimal? GrossMargin { get; set; }
    public decimal? GrossMarginPercent { get; set; }
}

public sealed record CreateCustomerRequest(
    string Name,
    string? Phone,
    decimal CreditLimit,
    string? Address = null,
    string? TaxId = null,
    string? Notes = null);
public sealed record CustomerMoneyRequest(
    decimal Amount,
    string Description,
    PaymentMethod PaymentMethod = PaymentMethod.Cash,
    Guid? CashRegisterSessionId = null);
public sealed record CreateExpenseRequest(
    string Description, decimal Amount, DateTimeOffset OccurredAt, PaymentMethod PaymentMethod = PaymentMethod.Cash);
/// <summary>Datos de contacto y referencia para crear o actualizar un proveedor.</summary>
/// <summary>Datos de un proveedor y de los artículos que ofrece al negocio.</summary>
public sealed record CreateSupplierRequest(
    string Name, string? TaxId, string? Phone, string? Address = null, string? ContactName = null,
    IReadOnlyList<SupplierStockItemInput>? Items = null);
/// <summary>Artículo asociado a un proveedor y su costo unitario acordado.</summary>
public sealed record SupplierStockItemInput(Guid StockItemId, decimal UnitCost);
public sealed record PurchaseLineRequest(
    Guid StockItemId, decimal Quantity, decimal UnitCost, string? LotCode = null,
    DateTimeOffset? ExpiresAt = null);
public sealed record ReceivePurchaseRequest(
    Guid SupplierId,
    string InvoiceReference,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<PurchaseLineRequest> Lines,
    PaymentMethod PaymentMethod = PaymentMethod.Credit,
    Guid? CashRegisterSessionId = null,
    DateTimeOffset? DueAt = null);
/// <summary>Registers a partial or final payment against a supplier purchase.</summary>
public sealed record SupplierPurchasePaymentRequest(
    Guid PurchaseOrderId,
    decimal Amount,
    PaymentMethod PaymentMethod,
    Guid CashRegisterSessionId,
    string? Note = null);
/// <summary>Supplier debt balance after recording a payment.</summary>
public sealed record SupplierPurchasePaymentResponse(
    Guid PurchaseOrderId,
    decimal PurchaseTotal,
    decimal AmountPaid,
    decimal OutstandingBalance,
    decimal PaymentAmount,
    DateTimeOffset PaidAt);
public sealed record VoidSaleRequest(string Reason);
/// <summary>Payload for saving an uncompleted order at a terminal.</summary>
public sealed record HoldSaleRequest(
    Guid Id, string TerminalId, string Payload, string? DiningTable = null, string? KitchenNotes = null,
    string? NamedOrder = null, FulfillmentMode FulfillmentMode = FulfillmentMode.Counter,
    Guid? PriceListId = null, Guid? CustomerId = null, int ExpectedVersion = 0,
    DateTimeOffset? StartedAt = null, bool CheckoutRequested = false);
public sealed record UpdateKitchenTicketRequest(KitchenTicketState State);
public sealed record UpdateProductPriceRequest(decimal Price);
public sealed record ProductPhotoUpload(string ContentType, string Base64Data);
public sealed record ReportSummaryResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    decimal Revenue,
    decimal CostOfGoods,
    decimal Expenses,
    decimal? GrossProfit,
    int CompletedSales,
    int SalesWithoutCostSnapshot,
    decimal PurchaseInvestment,
    decimal? NetProfit);
public sealed record PaymentMethodReportPoint(string Name, decimal Amount);
/// <summary>Aggregated sales trends and leading products for an administrator dashboard.</summary>
public sealed record SalesAnalyticsResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<MonthlySalesPoint> MonthlySales,
    IReadOnlyList<TopProductSalesPoint> TopProducts,
    IReadOnlyList<HourlySalesPoint> SalesByHour,
    IReadOnlyList<DailySalesPoint> WeeklySales,
    IReadOnlyList<RecentHourlySalesPoint> RecentHourlySales);
/// <summary>Revenue and count of completed sales for a business-local calendar month.</summary>
public sealed record MonthlySalesPoint(string Month, decimal Revenue, int SalesCount);
/// <summary>Quantity and revenue for a product in the requested range.</summary>
public sealed record TopProductSalesPoint(string ProductName, decimal Quantity, decimal Revenue);
/// <summary>Revenue and count of completed sales by business-local hour of day.</summary>
public sealed record HourlySalesPoint(int Hour, decimal Revenue, int SalesCount);
/// <summary>Revenue and count of completed sales by business-local date.</summary>
public sealed record DailySalesPoint(string Date, decimal Revenue, int SalesCount);
/// <summary>Revenue and count of completed sales in one of the latest 24 business-local hours.</summary>
public sealed record RecentHourlySalesPoint(DateTimeOffset Hour, decimal Revenue, int SalesCount);
public sealed record CustomerResponse(
    Guid Id,
    string Name,
    string? Phone,
    decimal WalletBalance,
    decimal CreditBalance,
    decimal CreditLimit,
    string? Address = null,
    string? TaxId = null,
    string? Notes = null);
public sealed record CustomerLedgerResponse(
    Guid Id,
    Guid? SaleId,
    CustomerLedgerKind Kind,
    decimal WalletDelta,
    decimal CreditDelta,
    PaymentMethod PaymentMethod,
    DateTimeOffset OccurredAt,
    string Actor,
    string Description);
/// <summary>Represents a locally or remotely held order.</summary>
public sealed record HeldSaleResponse(
    Guid Id, string TerminalId, string Payload, DateTimeOffset UpdatedAt, string? DiningTable, string? KitchenNotes,
    string? NamedOrder = null, FulfillmentMode FulfillmentMode = FulfillmentMode.Counter,
    Guid? PriceListId = null, Guid? CustomerId = null, int Version = 0,
    DateTimeOffset? StartedAt = null, bool CheckoutRequested = false);
/// <summary>Comanda and preparation state returned by the kitchen API.</summary>
public sealed record KitchenTicketResponse(
    Guid Id, Guid SaleId, KitchenTicketState State, DateTimeOffset CreatedAt, string? DiningTable,
    IReadOnlyList<KitchenTicketLineResponse> Lines, string? NamedOrder = null,
    FulfillmentMode FulfillmentMode = FulfillmentMode.Counter);
/// <summary>Menu item and special preparation instructions within a kitchen ticket.</summary>
public sealed record KitchenTicketLineResponse(string ProductName, decimal Quantity, string? Notes);
/// <summary>Información de proveedor expuesta por la API de administración.</summary>
public sealed record SupplierResponse(
    Guid Id, string Name, string? TaxId, string? Phone, string? Address = null, string? ContactName = null);
public sealed record PurchaseResponse(Guid Id, decimal Total, DateTimeOffset ReceivedAt);
public sealed record CatalogProductResponse(
    Guid Id,
    string Sku,
    string Name,
    decimal Price,
    bool IsActive,
    Guid? StockItemId,
    bool IsRecipe,
    ProductDepartment Department,
    string? Barcode = null,
    string? PrintStation = null);
public sealed record CatalogRecipeComponentResponse(Guid ProductId, Guid StockItemId, decimal Quantity);
public sealed record CatalogStockItemResponse(
    Guid Id, string Name, string Unit, decimal OnHand, decimal ReorderAt, decimal UnitCost);
public sealed record CatalogSnapshotResponse(
    IReadOnlyList<CatalogProductResponse> Products,
    IReadOnlyList<CatalogRecipeComponentResponse> Recipes,
    IReadOnlyList<CatalogStockItemResponse> StockItems,
    IReadOnlyList<CatalogStockLotResponse>? Lots = null,
    IReadOnlyList<PriceListResponse>? PriceLists = null,
    IReadOnlyList<ProductListPriceResponse>? ListPrices = null,
    IReadOnlyList<PromotionResponse>? Promotions = null,
    IReadOnlyList<PromotionBundleResponse>? PromotionBundles = null);
/// <summary>Stock lot metadata included in offline catalog synchronization.</summary>
public sealed record CatalogStockLotResponse(
    Guid Id, Guid StockItemId, string LotCode, decimal ReceivedQuantity,
    decimal RemainingQuantity, decimal UnitCost, DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpiresAt);
/// <summary>An active named list of product prices, all denominated in BOB.</summary>
public sealed record PriceListResponse(Guid Id, string Name, bool IsActive);
/// <summary>A product-specific price override in a named price list, denominated in BOB.</summary>
public sealed record ProductListPriceResponse(Guid PriceListId, Guid ProductId, decimal Price);
/// <summary>An automatic product promotion valid within the supplied UTC interval.</summary>
public sealed record PromotionResponse(
    Guid Id, Guid ProductId, string Name, PromotionKind Kind, decimal Value,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsActive);
/// <summary>Request to create an administrator-managed price list.</summary>
public sealed record CreatePriceListRequest(string Name);
/// <summary>Request to set a product's price within a named price list in BOB.</summary>
public sealed record SetProductListPriceRequest(Guid ProductId, decimal Price);
/// <summary>Request to create a time-bounded product promotion.</summary>
public sealed record CreatePromotionRequest(
    Guid ProductId, string Name, PromotionKind Kind, decimal Value,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record PromotionBundleComponentInput(Guid ProductId, decimal Quantity);
public sealed record CreatePromotionBundleRequest(
    string Name,
    decimal Price,
    IReadOnlyList<PromotionBundleComponentInput> Components,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    bool IsActive = true,
    Guid? Id = null,
    bool IsDeleted = false,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? DeletedAt = null);
public sealed record PromotionBundleComponentResponse(Guid ProductId, string ProductName, decimal Quantity);
public sealed record PromotionBundleResponse(
    Guid Id,
    string Name,
    decimal Price,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool IsActive,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    Guid ImageProductId,
    IReadOnlyList<PromotionBundleComponentResponse> Components);
public sealed record PromotionBundleSalesReport(
    Guid PromotionBundleId,
    string Name,
    int TimesSold,
    decimal Revenue,
    decimal DiscountGiven,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt);
public sealed record SyncSaleStatusRequest(IReadOnlyList<Guid>? SaleIds);
public sealed record CreateProductRequest(
    string Sku,
    string Name,
    decimal Price,
    bool IsRecipe,
    Guid? StockItemId,
    ProductDepartment Department,
    IReadOnlyList<RecipeComponentInput>? Recipe,
    string? Barcode = null,
    string? PrintStation = null,
    decimal ServiceUnitCost = 0m);
/// <summary>
/// Insumo y cantidad consumida por porción, expresada en la unidad base configurada para el insumo.
/// </summary>
public sealed record RecipeComponentInput(Guid StockItemId, decimal Quantity);
public sealed record UpdateRecipeRequest(IReadOnlyList<RecipeComponentInput>? Components);
public sealed record CreateStockItemRequest(string Name, string Unit, decimal OpeningStock, decimal ReorderAt, decimal UnitCost);
public sealed record UpdateStockItemRequest(
    string Name, string Unit, decimal OnHand, decimal ReorderAt, decimal UnitCost, string Reason);
/// <summary>Raw stock consumed by a production batch, in the stock item's base unit.</summary>
public sealed record ProductionIngredientInput(Guid StockItemId, decimal QuantityConsumed);
/// <summary>Request to record one locally or remotely synchronized finished-goods batch.</summary>
public sealed record RecordProductionRequest(
    Guid BatchId,
    Guid ProductId,
    decimal QuantityProduced,
    IReadOnlyList<ProductionIngredientInput> Ingredients);
/// <summary>Production result with its calculated finished-unit cost and recording time.</summary>
public sealed record ProductionBatchResponse(
    Guid BatchId, Guid ProductId, string ProductName, decimal QuantityProduced,
    decimal UnitCost, DateTimeOffset OccurredAt);
/// <summary>Current available amount and per-unit consumption for one production ingredient.</summary>
public sealed record ProductionCapacityIngredientResponse(
    string Name, decimal OnHand, string Unit, decimal QuantityPerFinishedUnit, decimal PossibleFinishedUnits);
/// <summary>Estimated additional output using the most recently recorded production batch.</summary>
public sealed record ProductionCapacityResponse(
    Guid ProductId, string ProductName, string FinishedUnit, decimal FinishedOnHand,
    decimal? MaximumAdditionalQuantity, DateTimeOffset? BasedOnProductionAt,
    IReadOnlyList<ProductionCapacityIngredientResponse> Ingredients);
public sealed record StockAdjustmentRequest(decimal Delta, string Reason);
/// <summary>Receipt of a dated lot in the store's base currency (BOB).</summary>
public sealed record ReceiveStockLotRequest(
    Guid StockItemId, string LotCode, decimal Quantity, decimal UnitCost,
    DateTimeOffset ReceivedAt, DateTimeOffset? ExpiresAt);
/// <summary>One physical count value for a stock item.</summary>
public sealed record StockCountLineRequest(Guid StockItemId, decimal CountedQuantity);
/// <summary>Request for an audited physical inventory count.</summary>
public sealed record CreateStockCountRequest(string Reason, IReadOnlyList<StockCountLineRequest> Lines);
/// <summary>A stock item lot and its remaining quantity and expiry.</summary>
public sealed record StockLotResponse(
    Guid Id, Guid StockItemId, string LotCode, decimal ReceivedQuantity,
    decimal RemainingQuantity, decimal UnitCost, DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpiresAt);
/// <summary>Audited physical inventory count result.</summary>
public sealed record StockCountResponse(Guid Id, int ItemsCounted, decimal AbsoluteVariance);
/// <summary>Request to create a custom payment method.</summary>
public sealed record CreateCustomPaymentTypeRequest(string Name);
/// <summary>Custom payment method available to a cashier.</summary>
public sealed record CustomPaymentTypeResponse(Guid Id, string Name, bool IsActive);
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt);
/// <summary>Immutable payload for synchronizing a completed cash-register session.</summary>
public sealed record CashRegisterSessionSyncRequest(
    Guid Id,
    string TerminalId,
    string OpenedBy,
    DateTimeOffset OpenedAtUtc,
    decimal OpeningCash,
    DateTimeOffset ClosedAtUtc,
    string ClosedBy,
    decimal CountedCash,
    decimal ExpectedCash,
    decimal Variance,
    string? ClosingNote);
/// <summary>One closed cash-register session from the consolidated cloud history.</summary>
public sealed record CashRegisterClosureResponse(
    Guid Id,
    string TerminalId,
    string OpenedBy,
    DateTimeOffset OpenedAtUtc,
    decimal OpeningCash,
    DateTimeOffset ClosedAtUtc,
    string ClosedBy,
    decimal CountedCash,
    decimal ExpectedCash,
    decimal Variance,
    string? ClosingNote);
/// <summary>A page of closed sessions returned by the cloud history endpoint.</summary>
public sealed record CashRegisterClosureListResponse(
    IReadOnlyList<CashRegisterClosureResponse> Sessions,
    bool HasMore);
public sealed record UpdateAuditRetentionRequest(int RetentionDays);
public sealed record CreateUserRequest(string Username, string Password, AppRole Role);
public sealed record UpdateUserStatusRequest(bool IsActive);
public sealed record UpdateUserPasswordRequest(string Password);
public sealed record UserResponse(Guid Id, string Username, AppRole Role, bool IsActive, DateTimeOffset CreatedAt);
