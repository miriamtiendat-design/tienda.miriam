using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class PromotionBundleService(TiendaDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PromotionBundleResponse> UpsertAsync(
        CreatePromotionBundleRequest request,
        string actor,
        bool enqueueForSync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120
            || request.Price < 0 || decimal.Round(request.Price, 2) != request.Price
            || request.Components is null || request.Components.Count is < 1 or > 50
            || request.Components.Any(component => component.ProductId == Guid.Empty
                || component.Quantity <= 0 || decimal.Round(component.Quantity, 6) != component.Quantity)
            || request.Components.Select(component => component.ProductId).Distinct().Count()
                != request.Components.Count
            || request.StartsAt is not null && request.EndsAt is not null
                && request.StartsAt >= request.EndsAt)
        {
            throw new InvalidOperationException("Revisa el nombre, precio, fechas y componentes de la promoción.");
        }

        var componentIds = request.Components.Select(component => component.ProductId).ToArray();
        var products = await db.Products
            .Where(product => componentIds.Contains(product.Id) && (request.IsDeleted || product.IsActive))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        if (products.Count != componentIds.Length)
        {
            throw new KeyNotFoundException("Uno o más productos del combo no existen o están inactivos.");
        }

        var bundleId = request.Id ?? Guid.NewGuid();
        var bundle = await db.PromotionBundles
            .Include(item => item.Components)
            .SingleOrDefaultAsync(item => item.Id == bundleId, cancellationToken);
        var now = DateTime.UtcNow;
        if (bundle is null)
        {
            bundle = new PromotionBundle
            {
                Id = bundleId,
                Name = name,
                Price = request.Price,
                CreatedAtUtc = request.CreatedAt?.UtcDateTime ?? now,
                UpdatedAtUtc = now
            };
            db.PromotionBundles.Add(bundle);
        }
        else if (bundle.IsDeleted && !request.IsDeleted)
        {
            throw new InvalidOperationException("Una promoción eliminada no puede reactivarse; crea una nueva.");
        }
        else
        {
            var existingComponents = bundle.Components
                .OrderBy(component => component.ProductId)
                .Select(component => (component.ProductId, component.Quantity))
                .ToArray();
            var requestedComponents = request.Components
                .OrderBy(component => component.ProductId)
                .Select(component => (component.ProductId, component.Quantity))
                .ToArray();
            if (bundle.Name != name || bundle.Price != request.Price
                || bundle.StartsAtUtc != request.StartsAt?.UtcDateTime
                || bundle.EndsAtUtc != request.EndsAt?.UtcDateTime
                || !existingComponents.SequenceEqual(requestedComponents))
            {
                throw new InvalidOperationException(
                    "Los combos guardados son históricos e inmutables; crea otro combo para cambiar sus productos o precio.");
            }
            bundle.Name = name;
            bundle.UpdatedAtUtc = now;
        }

        bundle.StartsAtUtc = request.StartsAt?.UtcDateTime;
        bundle.EndsAtUtc = request.EndsAt?.UtcDateTime;
        bundle.IsActive = request.IsDeleted ? false : request.IsActive;
        bundle.IsDeleted = request.IsDeleted;
        bundle.DeletedAtUtc = request.IsDeleted
            ? request.DeletedAt?.UtcDateTime ?? bundle.DeletedAtUtc ?? now
            : null;
        if (bundle.Components.Count == 0)
        {
            bundle.Components.AddRange(request.Components.Select(component => new PromotionBundleComponent
            {
                PromotionBundleId = bundle.Id,
                ProductId = component.ProductId,
                Quantity = component.Quantity
            }));
        }

        var canonicalRequest = request with
        {
            Name = bundle.Name,
            Id = bundle.Id,
            IsActive = bundle.IsActive,
            IsDeleted = bundle.IsDeleted,
            CreatedAt = new DateTimeOffset(bundle.CreatedAtUtc, TimeSpan.Zero),
            DeletedAt = bundle.DeletedAtUtc is null
                ? null
                : new DateTimeOffset(bundle.DeletedAtUtc.Value, TimeSpan.Zero)
        };
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = request.IsDeleted ? "PROMOTION_BUNDLE_DELETED" : "PROMOTION_BUNDLE_SAVED",
            EntityType = "PromotionBundle",
            EntityId = bundle.Id,
            Reason = request.IsDeleted ? "Combo desactivado lógicamente" : "Combo guardado",
            OccurredAtUtc = now
        });
        if (enqueueForSync)
        {
            db.PromotionBundleOutbox.Add(new PromotionBundleOutboxItem
            {
                Id = Guid.NewGuid(),
                PromotionBundleId = bundle.Id,
                Payload = JsonSerializer.Serialize(canonicalRequest, JsonOptions),
                CreatedAtUtc = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(bundle, products);
    }

    public async Task<PromotionBundleResponse> DeleteAsync(
        Guid bundleId,
        string actor,
        bool enqueueForSync,
        CancellationToken cancellationToken)
    {
        var bundle = await db.PromotionBundles.AsNoTracking()
            .Include(item => item.Components)
            .SingleOrDefaultAsync(item => item.Id == bundleId, cancellationToken)
            ?? throw new KeyNotFoundException("No se encontró el combo.");
        var request = new CreatePromotionBundleRequest(
            bundle.Name,
            bundle.Price,
            bundle.Components.Select(component =>
                new PromotionBundleComponentInput(component.ProductId, component.Quantity)).ToArray(),
            bundle.StartsAtUtc is null ? null : new DateTimeOffset(bundle.StartsAtUtc.Value, TimeSpan.Zero),
            bundle.EndsAtUtc is null ? null : new DateTimeOffset(bundle.EndsAtUtc.Value, TimeSpan.Zero),
            false,
            bundle.Id,
            true,
            new DateTimeOffset(bundle.CreatedAtUtc, TimeSpan.Zero),
            new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));
        return await UpsertAsync(request, actor, enqueueForSync, cancellationToken);
    }

    public async Task<PromotionBundleResponse> SetActiveAsync(
        Guid bundleId,
        bool isActive,
        string actor,
        bool enqueueForSync,
        CancellationToken cancellationToken)
    {
        var bundle = await db.PromotionBundles.AsNoTracking()
            .Include(item => item.Components)
            .SingleOrDefaultAsync(item => item.Id == bundleId, cancellationToken)
            ?? throw new KeyNotFoundException("No se encontró el combo.");
        if (bundle.IsDeleted && isActive)
        {
            throw new InvalidOperationException("Una promoción eliminada no se puede reactivar; crea una nueva.");
        }
        var request = new CreatePromotionBundleRequest(
            bundle.Name,
            bundle.Price,
            bundle.Components.Select(component =>
                new PromotionBundleComponentInput(component.ProductId, component.Quantity)).ToArray(),
            bundle.StartsAtUtc is null ? null : new DateTimeOffset(bundle.StartsAtUtc.Value, TimeSpan.Zero),
            bundle.EndsAtUtc is null ? null : new DateTimeOffset(bundle.EndsAtUtc.Value, TimeSpan.Zero),
            isActive,
            bundle.Id,
            bundle.IsDeleted,
            new DateTimeOffset(bundle.CreatedAtUtc, TimeSpan.Zero),
            bundle.DeletedAtUtc is null ? null : new DateTimeOffset(bundle.DeletedAtUtc.Value, TimeSpan.Zero));
        return await UpsertAsync(request, actor, enqueueForSync, cancellationToken);
    }

    public async Task<IReadOnlyList<PromotionBundleResponse>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        var bundles = await db.PromotionBundles.AsNoTracking()
            .Include(bundle => bundle.Components)
            .OrderByDescending(bundle => bundle.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var productIds = bundles.SelectMany(bundle => bundle.Components)
            .Select(component => component.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        return bundles.Select(bundle => ToResponse(bundle, products)).ToArray();
    }

    public async Task<IReadOnlyList<PromotionBundleSalesReport>> GetSalesReportAsync(
        DateTimeOffset? rangeStart,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from line in db.SaleLines.AsNoTracking()
            join sale in db.Sales.AsNoTracking() on line.SaleId equals sale.Id
            join snapshot in db.PromotionBundleSaleSnapshots.AsNoTracking()
                on line.Id equals snapshot.SaleLineId
            where line.PromotionBundleId != null
                && sale.State == SaleState.Completed
                && (rangeStart == null || sale.OccurredAtUtc >= rangeStart.Value.UtcDateTime)
                && (to == null || sale.OccurredAtUtc < to.Value.UtcDateTime)
            select new { BundleId = line.PromotionBundleId!.Value, line.Quantity, line.LineTotal,
                snapshot.DiscountAmount })
            .ToListAsync(cancellationToken);
        var bundles = await db.PromotionBundles.AsNoTracking().ToDictionaryAsync(bundle => bundle.Id, cancellationToken);
        return rows.GroupBy(row => row.BundleId)
            .Where(group => bundles.ContainsKey(group.Key))
            .Select(group =>
            {
                var bundle = bundles[group.Key];
                return new PromotionBundleSalesReport(
                    bundle.Id,
                    bundle.Name,
                    (int)group.Sum(row => row.Quantity),
                    group.Sum(row => row.LineTotal),
                    group.Sum(row => row.DiscountAmount),
                    new DateTimeOffset(bundle.CreatedAtUtc, TimeSpan.Zero),
                    bundle.DeletedAtUtc is null ? null : new DateTimeOffset(bundle.DeletedAtUtc.Value, TimeSpan.Zero));
            })
            .OrderByDescending(item => item.TimesSold)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private static PromotionBundleResponse ToResponse(
        PromotionBundle bundle,
        IReadOnlyDictionary<Guid, Product> products) =>
        new(
            bundle.Id,
            bundle.Name,
            bundle.Price,
            bundle.StartsAtUtc is null ? null : new DateTimeOffset(bundle.StartsAtUtc.Value, TimeSpan.Zero),
            bundle.EndsAtUtc is null ? null : new DateTimeOffset(bundle.EndsAtUtc.Value, TimeSpan.Zero),
            bundle.IsActive,
            bundle.IsDeleted,
            new DateTimeOffset(bundle.CreatedAtUtc, TimeSpan.Zero),
            bundle.DeletedAtUtc is null ? null : new DateTimeOffset(bundle.DeletedAtUtc.Value, TimeSpan.Zero),
            bundle.Components.FirstOrDefault()?.ProductId ?? Guid.Empty,
            bundle.Components.Select(component => new PromotionBundleComponentResponse(
                component.ProductId,
                products.GetValueOrDefault(component.ProductId)?.Name ?? "Producto eliminado",
                component.Quantity)).ToArray());
}
