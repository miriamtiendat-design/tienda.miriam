using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public interface ISaleService
{
    Task<SaleResult> CreateAsync(CreateSaleRequest request, bool enqueueForSync, CancellationToken cancellationToken);
}
