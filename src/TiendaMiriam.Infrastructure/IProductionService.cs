using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public interface IProductionService
{
    Task<ProductionBatchResponse> RecordProductionAsync(
        RecordProductionRequest request, string actor, CancellationToken cancellationToken);
    Task<ProductionCapacityResponse> GetProductionCapacityAsync(
        Guid productId, CancellationToken cancellationToken);
}
