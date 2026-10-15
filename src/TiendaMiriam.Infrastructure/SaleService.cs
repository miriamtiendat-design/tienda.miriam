using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class SaleService(TiendaDbContext db) : ISaleService
{
    public async Task<SaleResult> CreateAsync(
        CreateSaleRequest request,
        bool enqueueForSync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lines = ValidateRequest(request);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var trackedBefore = db.ChangeTracker.Entries().ToDictionary(
            entry => entry.Entity,
            entry => (
                State: entry.State,
                CurrentValues: entry.CurrentValues.Clone(),
                OriginalValues: entry.OriginalValues.Clone()),
            ReferenceEqualityComparer.Instance);
        try
        {
            var previous = await db.Sales.AsNoTracking()
                .Include(sale => sale.Lines)
                .Include(sale => sale.Payments)
                .SingleOrDefaultAsync(sale => sale.Id == request.SaleId, cancellationToken);
            if (previous is not null)
            {
                var previousLines = previous.Lines
                    .Select(line => (line.ProductId, line.Quantity, line.UnitPrice, line.AppliedPromotionId,
                        line.PromotionBundleId,
                        Notes: NormalizeNote(line.Notes)))
                    .OrderBy(line => line.ProductId)
                    .ThenBy(line => line.Quantity)
                    .ToArray();
                var requestedLines = lines
                    .Select(line => (line.ProductId, line.Quantity, line.UnitPriceAtSale, line.AppliedPromotionId,
                        line.PromotionBundleId,
                        Notes: NormalizeNote(line.Notes)))
                    .OrderBy(line => line.ProductId)
                    .ThenBy(line => line.Quantity)
                    .ToArray();
                var samePayment = previous.Payments.Count == 1
                    && previous.Payments[0].Method == request.PaymentMethod
                    && previous.Payments[0].Amount == request.AmountTendered
                    || request.Payments is { Count: > 0 } && previous.Payments
                        .OrderBy(payment => payment.Method).ThenBy(payment => payment.PaymentTypeId)
                        .Select(payment => (payment.Method, payment.Amount, payment.PaymentTypeId))
                        .SequenceEqual(request.Payments
                            .OrderBy(payment => payment.Method).ThenBy(payment => payment.PaymentTypeId)
                            .Select(payment => (payment.Method, payment.Amount, payment.PaymentTypeId)));
                var previousTicket = await db.KitchenTickets.AsNoTracking()
                    .Include(ticket => ticket.Lines)
                    .SingleOrDefaultAsync(ticket => ticket.SaleId == request.SaleId, cancellationToken);
                var requestTable = string.IsNullOrWhiteSpace(request.DiningTable) ? null : request.DiningTable.Trim();
                var requestOrder = string.IsNullOrWhiteSpace(request.NamedOrder) ? null : request.NamedOrder.Trim();
                var previousBundleIds = previous.Lines.Where(line => line.PromotionBundleId != null)
                    .Select(line => line.PromotionBundleId!.Value).ToArray();
                var previousBundles = await db.PromotionBundles.AsNoTracking()
                    .Include(bundle => bundle.Components)
                    .Where(bundle => previousBundleIds.Contains(bundle.Id))
                    .ToDictionaryAsync(bundle => bundle.Id, cancellationToken);
                var previousProductIds = previous.Lines.Select(line => line.ProductId)
                    .Concat(previousBundles.Values.SelectMany(bundle => bundle.Components)
                        .Select(component => component.ProductId))
                    .Distinct()
                    .ToArray();
                var previousProducts = await db.Products.AsNoTracking()
                    .Where(product => previousProductIds.Contains(product.Id))
                    .ToDictionaryAsync(product => product.Id, cancellationToken);
                var requestedKitchenLines = lines
                    .SelectMany(line => line.PromotionBundleId is Guid bundleId
                        && previousBundles.TryGetValue(bundleId, out var previousBundle)
                        ? previousBundle.Components
                            .Where(component => previousProducts[component.ProductId].IsRecipe)
                            .Select(component => (
                                ProductName: previousProducts[component.ProductId].Name,
                                Quantity: component.Quantity * line.Quantity,
                                Notes: NormalizeNote(line.Notes) ?? NormalizeNote(request.KitchenNotes)))
                        : previousProducts.TryGetValue(line.ProductId, out var product)
                            && (product.IsRecipe || !string.IsNullOrWhiteSpace(line.Notes))
                                ? [(ProductName: product.Name, Quantity: line.Quantity,
                                    Notes: NormalizeNote(line.Notes) ?? NormalizeNote(request.KitchenNotes))]
                                : [])
                    .OrderBy(line => line.ProductName)
                    .ThenBy(line => line.Quantity)
                    .ToArray();
                var previousKitchenLines = previousTicket?.Lines
                    .Select(line => (line.ProductName, line.Quantity, Notes: NormalizeNote(line.Notes)))
                    .OrderBy(line => line.ProductName)
                    .ThenBy(line => line.Quantity)
                    .ToArray() ?? [];
                if (previous.TerminalId != request.TerminalId.Trim()
                    || previous.CashRegisterSessionId != request.CashRegisterSessionId
                    || previous.OccurredAtUtc != request.OccurredAtUtc.UtcDateTime
                    || !previousLines.SequenceEqual(requestedLines)
                    || !samePayment
                    || previous.DiningTable != requestTable
                    || previous.NamedOrder != requestOrder
                    || previous.FulfillmentMode != request.FulfillmentMode
                    || previous.PriceListId != request.PriceListId
                    || previous.HeldSaleId != request.HeldSaleId
                    || previousTicket is not null
                        && !previousKitchenLines.SequenceEqual(requestedKitchenLines)
                    || await db.CustomerLedger.AnyAsync(entry => entry.SaleId == request.SaleId
                        && entry.CustomerId != request.CustomerId, cancellationToken))
                {
                    throw new InvalidOperationException("El identificador de venta ya existe con contenido distinto.");
                }
                return new SaleResult(previous.Id, previous.Total, previous.Payments[0].Change, true);
            }

            var bundleIds = lines.Where(line => line.PromotionBundleId is not null)
                .Select(line => line.PromotionBundleId!.Value).Distinct().ToArray();
            var bundles = await db.PromotionBundles
                .Include(bundle => bundle.Components)
                .Where(bundle => bundleIds.Contains(bundle.Id))
                .ToDictionaryAsync(bundle => bundle.Id, cancellationToken);
            if (bundles.Count != bundleIds.Length)
            {
                throw new InvalidOperationException("Una promoción ya no existe en el catálogo local.");
            }
            foreach (var line in lines.Where(line => line.PromotionBundleId is not null))
            {
                var bundle = bundles[line.PromotionBundleId!.Value];
                var occurredAt = request.OccurredAtUtc.UtcDateTime;
                var existedAtSale = (!bundle.IsDeleted && bundle.IsActive
                        || bundle.DeletedAtUtc > occurredAt
                        || !bundle.IsActive && bundle.UpdatedAtUtc > occurredAt)
                    && (bundle.StartsAtUtc is null || bundle.StartsAtUtc <= occurredAt)
                    && (bundle.EndsAtUtc is null || bundle.EndsAtUtc > occurredAt);
                if (!existedAtSale || bundle.Components.Count == 0
                    || line.Quantity != decimal.Truncate(line.Quantity)
                    || bundle.Components.All(component => component.ProductId != line.ProductId)
                    || bundle.Price != line.UnitPriceAtSale)
                {
                    throw new InvalidOperationException(
                        "La promoción no coincide con el catálogo vigente cuando se realizó la venta.");
                }
            }
            var productIds = lines.Select(line => line.ProductId)
                .Concat(bundles.Values.SelectMany(bundle => bundle.Components).Select(component => component.ProductId))
                .Distinct().ToArray();
            CashRegisterSession? cashSession = null;
            if (request.CashRegisterSessionId is Guid cashSessionId)
            {
                cashSession = await db.CashRegisterSessions.SingleOrDefaultAsync(
                    item => item.Id == cashSessionId
                        && item.TerminalId == request.TerminalId.Trim()
                        && item.ClosedAtUtc == null,
                    cancellationToken);
                if (cashSession is null)
                {
                    throw new InvalidOperationException("La caja no tiene una sesión activa para registrar esta venta.");
                }
            }
            HeldSale? heldOrder = null;
            if (request.HeldSaleId is Guid heldSaleId)
            {
                heldOrder = await db.HeldSales.SingleOrDefaultAsync(
                    item => item.Id == heldSaleId && item.Version == request.HeldSaleVersion,
                    cancellationToken);
                if (heldOrder is null
                    || !string.Equals(heldOrder.DiningTable, request.DiningTable, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("La orden de mesa ya cambió o se cerró en otra caja. Recarga la mesa antes de cobrar.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.DiningTable)
                && await db.HeldSales.AnyAsync(
                    item => item.DiningTable == request.DiningTable.Trim(),
                    cancellationToken))
            {
                throw new InvalidOperationException("La mesa tiene una orden abierta. Recupérala antes de iniciar otra venta para esa mesa.");
            }
            var products = await db.Products
                .Where(product => productIds.Contains(product.Id) && product.IsActive)
                .ToDictionaryAsync(product => product.Id, cancellationToken);
            if (products.Count != productIds.Length)
            {
                throw new InvalidOperationException("Uno o más productos no existen o están inactivos.");
            }
            if (products.Values.Any(product => product.Price < 0))
            {
                throw new InvalidOperationException("No se puede cobrar un producto con precio negativo.");
            }
            var productDetails = await db.ProductDetails
                .Where(item => productIds.Contains(item.ProductId))
                .ToDictionaryAsync(item => item.ProductId, cancellationToken);

            if (request.PriceListId is Guid requestedPriceListId
                && !await db.PriceLists.AnyAsync(item => item.Id == requestedPriceListId && item.IsActive, cancellationToken))
            {
                throw new InvalidOperationException("La lista de precios no existe o está inactiva.");
            }
            var listPriceEntries = request.PriceListId is Guid listId
                ? await db.ProductListPrices
                    .Where(item => item.PriceListId == listId && productIds.Contains(item.ProductId))
                    .ToDictionaryAsync(item => item.ProductId, item => item.Price, cancellationToken)
                : new Dictionary<Guid, decimal>();
            var promotions = await db.Promotions
                .Where(item => productIds.Contains(item.ProductId) && item.IsActive
                    && item.StartsAtUtc <= request.OccurredAtUtc.UtcDateTime
                    && item.EndsAtUtc > request.OccurredAtUtc.UtcDateTime)
                .ToListAsync(cancellationToken);
            var promotionsByProduct = promotions.GroupBy(item => item.ProductId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            if (lines.Any(line =>
                {
                    if (line.PromotionBundleId is not null)
                    {
                        return false;
                    }
                    var product = products[line.ProductId];
                    var basePrice = listPriceEntries.TryGetValue(product.Id, out var preferredPrice)
                        ? preferredPrice
                        : (decimal?)null;
                    var quote = ProductPricingCalculator.GetQuote(
                        product.Price,
                        basePrice,
                        promotionsByProduct.GetValueOrDefault(product.Id) ?? [],
                        request.OccurredAtUtc.UtcDateTime);
                    return quote.Price != line.UnitPriceAtSale || quote.PromotionId != line.AppliedPromotionId;
                }))
            {
                throw new InvalidOperationException(
                    "El precio promocional local no coincide con el catálogo del servidor; la venta queda pendiente de conciliación.");
            }

            var recipeComponents = await db.RecipeComponents
                .Where(component => productIds.Contains(component.ProductId))
                .ToListAsync(cancellationToken);
            var recipeByProduct = recipeComponents
                .GroupBy(component => component.ProductId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var normalizedLines = new List<SaleLineRequest>(lines.Count);
            foreach (var line in lines)
            {
                var product = products[line.ProductId];
                if (line.PromotionBundleId is Guid bundleId)
                {
                    var bundleFingerprint = ComputeBundleFingerprint(
                        bundles[bundleId].Components, products, recipeByProduct);
                    if (enqueueForSync)
                    {
                        normalizedLines.Add(line with { RecipeFingerprint = bundleFingerprint });
                    }
                    else if (string.IsNullOrWhiteSpace(line.RecipeFingerprint)
                        || !CryptographicOperations.FixedTimeEquals(
                            Encoding.ASCII.GetBytes(bundleFingerprint),
                            Encoding.ASCII.GetBytes(line.RecipeFingerprint)))
                    {
                        throw new InvalidOperationException(
                            $"Los productos del combo '{bundles[bundleId].Name}' cambiaron desde que esta caja quedó offline; la venta queda pendiente de conciliación.");
                    }
                    else
                    {
                        normalizedLines.Add(line);
                    }
                    continue;
                }
                if (!product.IsRecipe)
                {
                    normalizedLines.Add(line with { RecipeFingerprint = null });
                    continue;
                }
                var fingerprint = ComputeRecipeFingerprint(recipeByProduct.GetValueOrDefault(product.Id) ?? []);
                if (enqueueForSync)
                {
                    normalizedLines.Add(line with { RecipeFingerprint = fingerprint });
                }
                else if (string.IsNullOrWhiteSpace(line.RecipeFingerprint)
                    || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(fingerprint),
                        Encoding.ASCII.GetBytes(line.RecipeFingerprint)))
                {
                    throw new InvalidOperationException(
                        $"La receta de '{product.Name}' cambió desde que esta caja quedó offline; la venta queda pendiente de conciliación.");
                }
                else
                {
                    normalizedLines.Add(line);
                }
            }
            lines = normalizedLines;
            request = request with { Lines = lines };

            var sale = new Sale
            {
                Id = request.SaleId,
                TerminalId = request.TerminalId.Trim(),
                OccurredAtUtc = request.OccurredAtUtc.UtcDateTime,
                DiningTable = string.IsNullOrWhiteSpace(request.DiningTable) ? null : request.DiningTable.Trim(),
                NamedOrder = string.IsNullOrWhiteSpace(request.NamedOrder) ? null : request.NamedOrder.Trim(),
                FulfillmentMode = request.FulfillmentMode,
                PriceListId = request.PriceListId,
                CashRegisterSessionId = request.CashRegisterSessionId,
                HeldSaleId = request.HeldSaleId,
                State = SaleState.Completed
            };

            foreach (var line in lines)
            {
                var product = products[line.ProductId];
                var lineTotal = decimal.Round(line.UnitPriceAtSale * line.Quantity, 2, MidpointRounding.AwayFromZero);
                sale.Lines.Add(new SaleLine
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    ProductName = line.PromotionBundleId is Guid bundleId
                        ? bundles[bundleId].Name
                        : product.Name,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPriceAtSale,
                    LineTotal = lineTotal,
                    Notes = NormalizeNote(line.Notes),
                    AppliedPromotionId = line.AppliedPromotionId,
                    PromotionBundleId = line.PromotionBundleId
                });
                sale.Total += lineTotal;
            }

            sale.Total = decimal.Round(sale.Total, 2, MidpointRounding.AwayFromZero);
            var payments = request.Payments ?? [new SalePaymentRequest(request.PaymentMethod, request.AmountTendered)];
            if (payments.Count is 0 or > 5
                || payments.Any(payment => payment.Amount < 0
                    || payment.Amount == 0 && payment.Method != PaymentMethod.Credit
                    || decimal.Round(payment.Amount, 2) != payment.Amount))
            {
                throw new InvalidOperationException("La venta debe incluir de 1 a 5 pagos positivos con dos decimales.");
            }
            if (payments.Count > 1 && payments.Any(payment =>
                    payment.Method is PaymentMethod.CustomerWallet or PaymentMethod.Credit))
            {
                throw new InvalidOperationException("Monedero y fiado deben cubrir la venta completa en un solo método.");
            }
            if (request.Payments is null && request.PaymentMethod == PaymentMethod.Custom)
            {
                throw new InvalidOperationException("Un pago personalizado debe identificar su tipo de pago.");
            }
            if (payments.Any(payment => payment.Method == PaymentMethod.Custom && payment.PaymentTypeId is null)
                || payments.Any(payment => payment.Method != PaymentMethod.Custom && payment.PaymentTypeId is not null))
            {
                throw new InvalidOperationException("El tipo personalizado sólo corresponde a pagos personalizados.");
            }
            if (request.Payments is null && request.PaymentMethod is (PaymentMethod.Transfer or PaymentMethod.CustomerWallet)
                && request.AmountTendered != sale.Total)
            {
                throw new InvalidOperationException("El importe debe coincidir exactamente con el total.");
            }
            if (payments.Any(payment => payment.Method == PaymentMethod.Credit) && payments.Sum(payment => payment.Amount) != 0)
            {
                throw new InvalidOperationException("Una venta fiada no puede registrar efectivo recibido.");
            }
            if (request.Payments is null && request.PaymentMethod == PaymentMethod.Cash && request.AmountTendered < sale.Total)
            {
                throw new InvalidOperationException("El importe recibido no cubre el total de la venta.");
            }
            if (payments.Any(payment => payment.Method is PaymentMethod.CustomerWallet or PaymentMethod.Credit)
                && request.CustomerId is null)
            {
                throw new InvalidOperationException("El monedero y el fiado requieren identificar al cliente.");
            }
            var customer = request.CustomerId is Guid customerId
                ? await db.Customers.SingleOrDefaultAsync(
                    item => item.Id == customerId && item.IsActive,
                    cancellationToken)
                : null;
            if (request.CustomerId is not null && customer is null)
            {
                throw new InvalidOperationException("El cliente no existe o está inactivo.");
            }
            if (payments.Any(payment => payment.Method == PaymentMethod.CustomerWallet)
                && customer!.WalletBalance < sale.Total)
            {
                throw new InvalidOperationException("El saldo a favor del cliente es insuficiente.");
            }
            if (payments.Any(payment => payment.Method == PaymentMethod.Credit)
                && customer!.CreditBalance + sale.Total > customer.CreditLimit)
            {
                throw new InvalidOperationException("La venta supera el límite de crédito del cliente.");
            }

            var stockDeltas = new Dictionary<Guid, decimal>();
            foreach (var line in lines)
            {
                var consumedProducts = line.PromotionBundleId is Guid bundleId
                    ? bundles[bundleId].Components.Select(component => (Product: products[component.ProductId],
                        Quantity: component.Quantity * line.Quantity)).ToArray()
                    : [(Product: products[line.ProductId], Quantity: line.Quantity)];
                foreach (var consumedProduct in consumedProducts)
                {
                    var product = consumedProduct.Product;
                    if (product.IsRecipe)
                    {
                        var components = recipeComponents.Where(component => component.ProductId == product.Id).ToArray();
                        if (components.Length == 0)
                        {
                            throw new InvalidOperationException($"El producto '{product.Name}' no tiene receta.");
                        }
                        foreach (var component in components)
                        {
                            stockDeltas[component.StockItemId] =
                                stockDeltas.GetValueOrDefault(component.StockItemId)
                                + component.Quantity * consumedProduct.Quantity;
                        }
                    }
                    else if (product.StockItemId is Guid stockItemId
                        && productDetails.GetValueOrDefault(product.Id)?.Department
                            != ProductDepartment.Service)
                    {
                        stockDeltas[stockItemId] = stockDeltas.GetValueOrDefault(stockItemId) + consumedProduct.Quantity;
                    }
                }
            }

            var stockItems = await db.StockItems
                .Where(item => stockDeltas.Keys.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            if (stockItems.Count != stockDeltas.Count)
            {
                throw new InvalidOperationException("Falta configurar uno o más insumos de inventario.");
            }

            var customPaymentIds = payments.Where(payment => payment.Method == PaymentMethod.Custom)
                .Select(payment => payment.PaymentTypeId!.Value).Distinct().ToArray();
            var customPaymentTypes = await db.CustomPaymentTypes
                .Where(item => customPaymentIds.Contains(item.Id) && item.IsActive)
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            if (customPaymentTypes.Count != customPaymentIds.Length)
            {
                throw new InvalidOperationException("Uno o más tipos de pago personalizados no existen o están inactivos.");
            }

            var fixedApplied = payments.Count == 1 && payments[0].Method == PaymentMethod.Credit
                ? sale.Total
                : payments.Where(payment => payment.Method != PaymentMethod.Cash).Sum(payment => payment.Amount);
            if (fixedApplied > sale.Total)
            {
                throw new InvalidOperationException("Los pagos sin efectivo superan el total de la venta.");
            }
            var remainingToPay = sale.Total - fixedApplied;
            var change = 0m;
            var paymentRecords = new List<Payment>();
            foreach (var payment in payments)
            {
                var paymentChange = 0m;
                if (payment.Method == PaymentMethod.Cash)
                {
                    var applied = Math.Min(payment.Amount, remainingToPay);
                    remainingToPay -= applied;
                    paymentChange = payment.Amount - applied;
                    change += paymentChange;
                }
                paymentRecords.Add(new Payment
                {
                    Id = Guid.NewGuid(),
                    Method = payment.Method,
                    PaymentTypeId = payment.PaymentTypeId,
                    PaymentTypeName = payment.PaymentTypeId is Guid typeId
                        ? customPaymentTypes[typeId].Name
                        : null,
                    Amount = payment.Amount,
                    Change = paymentChange,
                    OccurredAtUtc = sale.OccurredAtUtc
                });
            }
            if (remainingToPay != 0)
            {
                throw new InvalidOperationException("La suma de pagos no cubre exactamente el total de la venta.");
            }

            var lineCostTotal = 0m;
            foreach (var saleLine in sale.Lines)
            {
                decimal? unitCost;
                decimal? costOfGoodsForLine;
                decimal? regularAmount = null;
                if (saleLine.PromotionBundleId is Guid bundleId)
                {
                    var bundle = bundles[bundleId];
                    decimal totalCostPerBundle = 0m;
                    var hasCompleteCost = true;
                    foreach (var component in bundle.Components)
                    {
                        var componentProduct = products[component.ProductId];
                        var componentUnitCost = ProductFinancialCalculator.GetUnitCost(
                            componentProduct, stockItems, recipeByProduct,
                            productDetails.GetValueOrDefault(componentProduct.Id) is
                                { Department: ProductDepartment.Service } componentDetails
                                ? componentDetails.ServiceUnitCost : null);
                        if (componentUnitCost is null)
                        {
                            hasCompleteCost = false;
                            break;
                        }
                        totalCostPerBundle += componentUnitCost.Value * component.Quantity;
                    }
                    unitCost = hasCompleteCost ? totalCostPerBundle : null;
                    regularAmount = bundle.Components.Sum(component =>
                        products[component.ProductId].Price * component.Quantity * saleLine.Quantity);
                    db.PromotionBundleSaleSnapshots.Add(new PromotionBundleSaleSnapshot
                    {
                        SaleLineId = saleLine.Id,
                        RegularAmount = decimal.Round(regularAmount.Value, 2, MidpointRounding.AwayFromZero),
                        DiscountAmount = decimal.Round(Math.Max(0m, regularAmount.Value - saleLine.LineTotal), 2,
                            MidpointRounding.AwayFromZero)
                    });
                }
                else
                {
                    unitCost = ProductFinancialCalculator.GetUnitCost(
                        products[saleLine.ProductId], stockItems, recipeByProduct,
                        productDetails.GetValueOrDefault(saleLine.ProductId) is
                            { Department: ProductDepartment.Service } serviceDetails
                            ? serviceDetails.ServiceUnitCost : null);
                }
                costOfGoodsForLine = unitCost is null ? null : unitCost.Value * saleLine.Quantity;

                decimal? grossMargin = unitCost is null
                    ? null
                    : saleLine.LineTotal - costOfGoodsForLine!.Value;
                db.SaleLineFinancialSnapshots.Add(new SaleLineFinancialSnapshot
                {
                    SaleLineId = saleLine.Id,
                    UnitCost = unitCost,
                    CostOfGoods = costOfGoodsForLine,
                    GrossMargin = grossMargin,
                    GrossMarginPercent = ProductFinancialCalculator.GetGrossMarginPercent(unitCost, saleLine.UnitPrice)
                });
                if (costOfGoodsForLine is decimal knownCost)
                {
                    lineCostTotal += knownCost;
                }
            }

            var costOfGoods = lineCostTotal;
            foreach (var (stockItemId, quantity) in stockDeltas)
            {
                var stockItem = stockItems[stockItemId];
                if (stockItem.OnHand < quantity)
                {
                    throw new InvalidOperationException($"Stock insuficiente de '{stockItem.Name}'.");
                }

                var lots = await db.StockLots
                    .Where(lot => lot.StockItemId == stockItemId && lot.RemainingQuantity > 0)
                    .OrderBy(lot => lot.ExpiresAtUtc == null)
                    .ThenBy(lot => lot.ExpiresAtUtc)
                    .ThenBy(lot => lot.ReceivedAtUtc)
                    .ToListAsync(cancellationToken);
                var trackedLotBalance = lots.Sum(lot => lot.RemainingQuantity);
                var untrackedBalance = Math.Max(0m, stockItem.OnHand - trackedLotBalance);
                var availableLotBalance = lots
                    .Where(lot => lot.ExpiresAtUtc is null || lot.ExpiresAtUtc > sale.OccurredAtUtc)
                    .Sum(lot => lot.RemainingQuantity);
                var remaining = quantity;
                foreach (var lot in lots.Where(lot => lot.ExpiresAtUtc is null || lot.ExpiresAtUtc > sale.OccurredAtUtc))
                {
                    if (remaining == 0)
                    {
                        break;
                    }
                    var consumed = Math.Min(lot.RemainingQuantity, remaining);
                    lot.RemainingQuantity -= consumed;
                    remaining -= consumed;
                    db.StockMovements.Add(new StockMovement
                    {
                        Id = Guid.NewGuid(),
                        StockItemId = stockItemId,
                        StockLotId = lot.Id,
                        SaleId = sale.Id,
                        QuantityDelta = -consumed,
                        OccurredAtUtc = sale.OccurredAtUtc,
                        Reason = "Venta"
                    });
                }
                if (remaining > untrackedBalance || availableLotBalance + untrackedBalance < quantity)
                {
                    throw new InvalidOperationException($"Stock vencido o insuficiente de '{stockItem.Name}'.");
                }
                if (remaining > 0)
                {
                    db.StockMovements.Add(new StockMovement
                    {
                        Id = Guid.NewGuid(),
                        StockItemId = stockItemId,
                        SaleId = sale.Id,
                        QuantityDelta = -remaining,
                        OccurredAtUtc = sale.OccurredAtUtc,
                        Reason = "Venta"
                    });
                }
                stockItem.OnHand -= quantity;
            }

            sale.Payments.AddRange(paymentRecords);
            if (heldOrder is not null)
            {
                if (enqueueForSync && heldOrder.DiningTable is not null)
                {
                    heldOrder.ClosePending = true;
                }
                else
                {
                    db.HeldSales.Remove(heldOrder);
                }
            }
            if (cashSession is not null)
            {
                cashSession.Version++;
            }
            db.Sales.Add(sale);
            db.SaleCostSnapshots.Add(new SaleCostSnapshot
            {
                SaleId = sale.Id,
                Cost = decimal.Round(costOfGoods, 2, MidpointRounding.AwayFromZero)
            });
            if (customer is not null && payments.Count == 1
                && payments[0].Method is (PaymentMethod.CustomerWallet or PaymentMethod.Credit))
            {
                var walletDelta = payments[0].Method == PaymentMethod.CustomerWallet ? -sale.Total : 0m;
                var creditDelta = payments[0].Method == PaymentMethod.Credit ? sale.Total : 0m;
                customer.WalletBalance += walletDelta;
                customer.CreditBalance += creditDelta;
                db.CustomerLedger.Add(new CustomerLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customer.Id,
                    SaleId = sale.Id,
                    Kind = payments[0].Method == PaymentMethod.Credit
                        ? CustomerLedgerKind.CreditSale
                        : CustomerLedgerKind.WalletPurchase,
                    WalletDelta = walletDelta,
                    CreditDelta = creditDelta,
                    OccurredAtUtc = sale.OccurredAtUtc,
                    Actor = sale.TerminalId,
                    Description = $"Venta {sale.Id:N}"
                });
            }
            var kitchenLines = lines
                .SelectMany(line => line.PromotionBundleId is Guid bundleId
                    ? bundles[bundleId].Components
                        .Where(component => products[component.ProductId].IsRecipe)
                        .Select(component => (ProductName: products[component.ProductId].Name,
                            Quantity: component.Quantity * line.Quantity, line.Notes))
                    : products[line.ProductId].IsRecipe || !string.IsNullOrWhiteSpace(line.Notes)
                        ? [(ProductName: products[line.ProductId].Name, Quantity: line.Quantity, line.Notes)]
                        : [])
                .Select(line => new KitchenTicketLine
                {
                    Id = Guid.NewGuid(),
                    ProductName = line.ProductName,
                    Quantity = line.Quantity,
                    Notes = NormalizeNote(line.Notes) ?? NormalizeNote(request.KitchenNotes)
                })
                .ToList();
            if (kitchenLines.Count != 0)
            {
                var ticket = new KitchenTicket
                {
                    Id = Guid.NewGuid(),
                    SaleId = sale.Id,
                    DiningTable = sale.DiningTable,
                    NamedOrder = sale.NamedOrder,
                    FulfillmentMode = sale.FulfillmentMode,
                    CreatedAtUtc = sale.OccurredAtUtc,
                    UpdatedAtUtc = sale.OccurredAtUtc,
                    Lines = kitchenLines
                };
                db.KitchenTickets.Add(ticket);
            }
            db.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.NewGuid(),
                Actor = sale.TerminalId,
                Action = "SALE_CREATED",
                EntityType = "Sale",
                EntityId = sale.Id,
                Reason = "Venta confirmada",
                OccurredAtUtc = sale.OccurredAtUtc
            });

            if (enqueueForSync)
            {
                db.SyncOutbox.Add(new SyncOutboxItem
                {
                    Id = Guid.NewGuid(),
                    SaleId = sale.Id,
                    Payload = JsonSerializer.Serialize(request with
                    {
                        CashRegisterSessionId = null,
                        HeldSaleId = request.DiningTable is null ? null : request.HeldSaleId
                    }),
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new InvalidOperationException(
                    "El inventario o la sesión de caja cambió durante el cobro; vuelve a consultar e intenta nuevamente.",
                    exception);
            }
            await transaction.CommitAsync(cancellationToken);
            return new SaleResult(sale.Id, sale.Total, change, false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            foreach (var entry in db.ChangeTracker.Entries().ToArray())
            {
                if (!trackedBefore.TryGetValue(entry.Entity, out var snapshot))
                {
                    entry.State = EntityState.Detached;
                    continue;
                }
                entry.CurrentValues.SetValues(snapshot.CurrentValues);
                entry.OriginalValues.SetValues(snapshot.OriginalValues);
                entry.State = snapshot.State;
            }
            throw;
        }
    }

    private static IReadOnlyList<SaleLineRequest> ValidateRequest(CreateSaleRequest request)
    {
        if (request.SaleId == Guid.Empty || string.IsNullOrWhiteSpace(request.TerminalId))
        {
            throw new InvalidOperationException("La venta requiere un identificador y una caja.");
        }
        if (request.DiningTable?.Trim().Length > 40 || request.KitchenNotes?.Trim().Length > 160
            || request.NamedOrder?.Trim().Length > 80 || !Enum.IsDefined(request.FulfillmentMode))
        {
            throw new InvalidOperationException("La mesa admite hasta 40 caracteres y la nota de cocina hasta 160.");
        }
        var lines = request.Lines;
        if (lines is null || lines.Count == 0
            || lines.Any(line => line is null || line.ProductId == Guid.Empty || line.Quantity <= 0
                || decimal.Round(line.Quantity, 6) != line.Quantity
                || line.UnitPriceAtSale < 0 || decimal.Round(line.UnitPriceAtSale, 4) != line.UnitPriceAtSale
                || line.Notes?.Trim().Length > 160))
        {
            throw new InvalidOperationException(
                "La venta debe contener productos con cantidades positivas y precios no negativos válidos.");
        }
        if (request.AmountTendered < 0 || decimal.Round(request.AmountTendered, 2) != request.AmountTendered)
        {
            throw new InvalidOperationException("El importe recibido no puede ser negativo y debe tener máximo dos decimales.");
        }
        return lines;
    }

    private static string? NormalizeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static string ComputeRecipeFingerprint(IReadOnlyCollection<RecipeComponent> components)
    {
        var canonical = string.Join('\n', components
            .OrderBy(component => component.StockItemId)
            .Select(component =>
                $"{component.StockItemId:N}:{component.Quantity.ToString("0.######", CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeBundleFingerprint(
        IReadOnlyCollection<PromotionBundleComponent> components,
        IReadOnlyDictionary<Guid, Product> products,
        IReadOnlyDictionary<Guid, RecipeComponent[]> recipes)
    {
        var canonical = string.Join('\n', components
            .OrderBy(component => component.ProductId)
            .Select(component =>
            {
                var product = products[component.ProductId];
                var recipe = recipes.GetValueOrDefault(product.Id) ?? [];
                var recipeValue = product.IsRecipe
                    ? ComputeRecipeFingerprint(recipe)
                    : product.StockItemId?.ToString("N") ?? "NO_STOCK";
                return $"{product.Id:N}:{component.Quantity.ToString("0.######", CultureInfo.InvariantCulture)}:{recipeValue}";
            }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
