using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public interface ICommerceService
{
    Task VoidSaleAsync(Guid saleId, string reason, string actor, CancellationToken cancellationToken);
    Task<CustomerResponse> CreateCustomerAsync(
        CreateCustomerRequest request, CancellationToken cancellationToken, bool enqueueForSync = false);
    Task<CustomerResponse> UpdateCustomerAsync(
        Guid customerId, CreateCustomerRequest request, string actor, CancellationToken cancellationToken,
        bool enqueueForSync = false);
    Task<CustomerResponse> AddWalletFundsAsync(Guid customerId, decimal amount, string description, string actor, PaymentMethod paymentMethod, CancellationToken cancellationToken);
    Task<CustomerResponse> RecordCreditPaymentAsync(
        Guid customerId, decimal amount, string description, string actor, PaymentMethod paymentMethod,
        CancellationToken cancellationToken, Guid? cashRegisterSessionId = null, bool enqueueForSync = false);
    Task RecordExpenseAsync(CreateExpenseRequest request, string actor, CancellationToken cancellationToken);
    Task<SupplierResponse> CreateSupplierAsync(CreateSupplierRequest request, CancellationToken cancellationToken);
    Task<SupplierResponse> UpdateSupplierAsync(Guid supplierId, CreateSupplierRequest request, string actor, CancellationToken cancellationToken);
    Task<PurchaseResponse> ReceivePurchaseAsync(ReceivePurchaseRequest request, string actor, CancellationToken cancellationToken);
    Task<SupplierPurchasePaymentResponse> RecordSupplierPurchasePaymentAsync(
        SupplierPurchasePaymentRequest request, string actor, CancellationToken cancellationToken);
    Task<HeldSaleResponse> SaveHeldSaleAsync(HoldSaleRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<HeldSaleResponse>> GetHeldSalesAsync(string terminalId, CancellationToken cancellationToken);
    Task DeleteHeldSaleAsync(
        Guid id, string terminalId, CancellationToken cancellationToken, int? expectedVersion = null);
    Task<StockCountResponse> CreateStockCountAsync(
        CreateStockCountRequest request, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<StockLotResponse>> GetStockLotsAsync(
        bool includeDepleted, CancellationToken cancellationToken);
    Task<CustomPaymentTypeResponse> CreateCustomPaymentTypeAsync(
        CreateCustomPaymentTypeRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomPaymentTypeResponse>> GetCustomPaymentTypesAsync(CancellationToken cancellationToken);
    Task<PriceListResponse> CreatePriceListAsync(CreatePriceListRequest request, string actor, CancellationToken cancellationToken);
    Task SetProductListPriceAsync(
        Guid priceListId, SetProductListPriceRequest request, string actor, CancellationToken cancellationToken);
    Task<PromotionResponse> CreatePromotionAsync(
        CreatePromotionRequest request, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<PromotionResponse>> GetPromotionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<KitchenTicketResponse>> GetKitchenQueueAsync(CancellationToken cancellationToken);
    Task UpdateKitchenTicketAsync(Guid id, KitchenTicketState state, string actor, CancellationToken cancellationToken);
    Task UpdateProductPriceAsync(Guid id, decimal price, string actor, CancellationToken cancellationToken);
    Task<Product> CreateProductAsync(CreateProductRequest request, string actor, CancellationToken cancellationToken);
    Task<Product> UpdateProductAsync(Guid id, CreateProductRequest request, string actor, CancellationToken cancellationToken);
    Task UpdateRecipeAsync(Guid productId, UpdateRecipeRequest request, string actor, CancellationToken cancellationToken);
    Task<StockItem> CreateStockItemAsync(CreateStockItemRequest request, string actor, CancellationToken cancellationToken);
    Task<StockItem> UpdateStockItemAsync(
        Guid id, UpdateStockItemRequest request, string actor, CancellationToken cancellationToken);
    Task AdjustStockAsync(Guid stockItemId, decimal delta, string reason, string actor, CancellationToken cancellationToken);
    Task SetProductPhotoAsync(Guid id, ProductPhotoUpload upload, CancellationToken cancellationToken);
    Task DeleteProductPhotoAsync(Guid id, CancellationToken cancellationToken);
    Task<ReportSummaryResponse> GetReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
