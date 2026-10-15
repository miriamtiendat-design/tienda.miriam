using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class CommerceService(TiendaDbContext db) : ICommerceService
{
    public async Task VoidSaleAsync(Guid saleId, string reason, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
        {
            throw new InvalidOperationException("La anulación requiere un motivo de hasta 500 caracteres.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sale = await db.Sales.SingleOrDefaultAsync(item => item.Id == saleId, cancellationToken)
            ?? throw new KeyNotFoundException("La venta no existe.");
        if (sale.State != SaleState.Completed)
        {
            throw new InvalidOperationException("Sólo se pueden anular ventas completadas.");
        }
        var ticket = await db.KitchenTickets.SingleOrDefaultAsync(item => item.SaleId == saleId, cancellationToken);
        if (ticket is not null && ticket.State != KitchenTicketState.Queued)
        {
            throw new InvalidOperationException("No se puede anular: cocina ya comenzó a preparar el pedido.");
        }

        var saleMovements = await db.StockMovements
            .Where(item => item.SaleId == saleId)
            .ToListAsync(cancellationToken);
        var movements = saleMovements
            .GroupBy(item => item.StockItemId)
            .Select(group => new { StockItemId = group.Key, Quantity = -group.Sum(item => item.QuantityDelta) })
            .ToList();
        var lotDeltas = saleMovements.Where(item => item.StockLotId is not null)
            .GroupBy(item => item.StockLotId!.Value)
            .ToDictionary(group => group.Key, group => -group.Sum(item => item.QuantityDelta));
        var lots = await db.StockLots.Where(item => lotDeltas.Keys.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (lots.Count != lotDeltas.Count)
        {
            throw new InvalidOperationException("Falta un lote afectado por la venta; no se puede revertir el inventario con seguridad.");
        }
        foreach (var (lotId, quantity) in lotDeltas)
        {
            lots[lotId].RemainingQuantity += quantity;
        }
        var stockIds = movements.Select(item => item.StockItemId).ToArray();
        var stock = await db.StockItems.Where(item => stockIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var movement in movements)
        {
            if (!stock.TryGetValue(movement.StockItemId, out var item))
            {
                throw new InvalidOperationException("No se encontró un insumo asociado a la venta.");
            }
            item.OnHand += movement.Quantity;
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                StockItemId = item.Id,
                SaleId = sale.Id,
                QuantityDelta = movement.Quantity,
                OccurredAtUtc = DateTime.UtcNow,
                Reason = "Anulación"
            });
        }
        foreach (var (lotId, quantity) in lotDeltas)
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                StockItemId = lots[lotId].StockItemId,
                StockLotId = lotId,
                SaleId = sale.Id,
                QuantityDelta = quantity,
                OccurredAtUtc = DateTime.UtcNow,
                Reason = "Anulación"
            });
        }

        var accountEntries = await db.CustomerLedger.Where(entry => entry.SaleId == saleId).ToListAsync(cancellationToken);
        foreach (var entry in accountEntries)
        {
            var customer = await db.Customers.SingleAsync(item => item.Id == entry.CustomerId, cancellationToken);
            customer.WalletBalance -= entry.WalletDelta;
            customer.CreditBalance -= entry.CreditDelta;
            db.CustomerLedger.Add(new CustomerLedgerEntry
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                SaleId = sale.Id,
                Kind = CustomerLedgerKind.Reversal,
                WalletDelta = -entry.WalletDelta,
                CreditDelta = -entry.CreditDelta,
                OccurredAtUtc = DateTime.UtcNow,
                Actor = actor,
                Description = $"Reversión de venta: {reason.Trim()}"
            });
        }

        sale.State = SaleState.Voided;
        if (ticket is not null)
        {
            ticket.State = KitchenTicketState.Cancelled;
            ticket.UpdatedAtUtc = DateTime.UtcNow;
        }
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = "SALE_VOIDED",
            EntityType = "Sale",
            EntityId = sale.Id,
            Reason = reason.Trim(),
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CustomerResponse> CreateCustomerAsync(
        CreateCustomerRequest request, CancellationToken cancellationToken, bool enqueueForSync = false)
    {
        ValidateCustomerRequest(request);
        var customer = new CustomerAccount
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Phone = NormalizeOptional(request.Phone, 32),
            Address = NormalizeOptional(request.Address, 240),
            TaxId = NormalizeOptional(request.TaxId, 40),
            Notes = NormalizeOptional(request.Notes, 500),
            CreditLimit = request.CreditLimit
        };
        db.Customers.Add(customer);
        if (enqueueForSync)
        {
            AddCustomerProfileChange(customer);
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    public async Task<CustomerResponse> UpdateCustomerAsync(
        Guid customerId,
        CreateCustomerRequest request,
        string actor,
        CancellationToken cancellationToken,
        bool enqueueForSync = false)
    {
        ValidateCustomerRequest(request);
        if (customerId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 64)
        {
            throw new InvalidOperationException("El cliente y el usuario que registra el cambio son obligatorios.");
        }
        var customer = await db.Customers.SingleOrDefaultAsync(
            item => item.Id == customerId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El cliente no existe o está inactivo.");
        customer.Name = request.Name.Trim();
        customer.Phone = NormalizeOptional(request.Phone, 32);
        customer.Address = NormalizeOptional(request.Address, 240);
        customer.TaxId = NormalizeOptional(request.TaxId, 40);
        customer.Notes = NormalizeOptional(request.Notes, 500);
        customer.CreditLimit = request.CreditLimit;
        if (enqueueForSync)
        {
            AddCustomerProfileChange(customer);
        }
        db.AuditEntries.Add(NewAudit(actor.Trim(), "CUSTOMER_UPDATED", nameof(CustomerAccount),
            customer.Id, $"Ficha actualizada: {customer.Name}"));
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    public async Task<CustomerResponse> AddWalletFundsAsync(
        Guid customerId, decimal amount, string description, string actor, PaymentMethod paymentMethod, CancellationToken cancellationToken)
    {
        ValidateMoneyEntry(amount, description);
        ValidateCashMovementMethod(paymentMethod);
        var customer = await db.Customers.SingleOrDefaultAsync(
            item => item.Id == customerId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El cliente no existe o está inactivo.");
        customer.WalletBalance += amount;
        var entry = NewLedger(customer, CustomerLedgerKind.WalletTopUp, amount, 0, description, actor);
        entry.PaymentMethod = paymentMethod;
        db.CustomerLedger.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    public async Task<CustomerResponse> RecordCreditPaymentAsync(
        Guid customerId,
        decimal amount,
        string description,
        string actor,
        PaymentMethod paymentMethod,
        CancellationToken cancellationToken,
        Guid? cashRegisterSessionId = null,
        bool enqueueForSync = false)
    {
        ValidateMoneyEntry(amount, description);
        ValidateCashMovementMethod(paymentMethod);
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 64)
        {
            throw new InvalidOperationException("El usuario que registra el abono no es válido.");
        }
        if (enqueueForSync && cashRegisterSessionId is null)
        {
            throw new InvalidOperationException("El abono local requiere una sesión de caja abierta.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (cashRegisterSessionId is Guid sessionId
            && !await db.CashRegisterSessions.AnyAsync(
                item => item.Id == sessionId && item.ClosedAtUtc == null,
                cancellationToken))
        {
            throw new InvalidOperationException("No hay una sesión de caja abierta para registrar el abono.");
        }
        var customer = await db.Customers.SingleOrDefaultAsync(
            item => item.Id == customerId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El cliente no existe o está inactivo.");
        if (amount > customer.CreditBalance)
        {
            throw new InvalidOperationException("El abono supera el saldo fiado.");
        }
        customer.CreditBalance -= amount;
        var entry = NewLedger(customer, CustomerLedgerKind.CreditPayment, 0, -amount, description, actor);
        entry.PaymentMethod = paymentMethod;
        entry.CashRegisterSessionId = cashRegisterSessionId;
        db.CustomerLedger.Add(entry);
        if (enqueueForSync)
        {
            AddCreditPaymentChange(customer, entry, amount, paymentMethod);
        }
        db.AuditEntries.Add(NewAudit(actor.Trim(), "CUSTOMER_CREDIT_PAYMENT", nameof(CustomerLedgerEntry),
            entry.Id, $"{customer.Name}: abono de {amount:0.00} BOB por {paymentMethod}."));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(customer);
    }

    private static void ValidateCustomerRequest(CreateCustomerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160
            || request.CreditLimit < 0 || decimal.Round(request.CreditLimit, 2) != request.CreditLimit
            || request.Phone?.Trim().Length > 32 || request.Address?.Trim().Length > 240
            || request.TaxId?.Trim().Length > 40 || request.Notes?.Trim().Length > 500)
        {
            throw new InvalidOperationException("Revisa el nombre, el límite de crédito y los datos del cliente.");
        }
    }

    private void AddCustomerProfileChange(CustomerAccount customer)
    {
        var change = new CustomerSyncChange(
            Guid.NewGuid(),
            customer.Id,
            CustomerSyncChangeKind.ProfileUpsert,
            new CustomerProfileSync(customer.Name, customer.Phone, customer.Address, customer.TaxId,
                customer.Notes, customer.CreditLimit));
        db.CustomerChangeOutbox.Add(new CustomerChangeOutboxItem
        {
            Id = change.EventId,
            CustomerId = customer.Id,
            Kind = change.Kind,
            Payload = System.Text.Json.JsonSerializer.Serialize(change),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private void AddCreditPaymentChange(
        CustomerAccount customer,
        CustomerLedgerEntry entry,
        decimal amount,
        PaymentMethod paymentMethod)
    {
        var change = new CustomerSyncChange(
            Guid.NewGuid(),
            customer.Id,
            CustomerSyncChangeKind.CreditPayment,
            CreditPayment: new CustomerCreditPaymentSync(
                entry.Id,
                amount,
                entry.Description,
                entry.Actor,
                paymentMethod,
                new DateTimeOffset(entry.OccurredAtUtc, TimeSpan.Zero),
                entry.CashRegisterSessionId));
        db.CustomerChangeOutbox.Add(new CustomerChangeOutboxItem
        {
            Id = change.EventId,
            CustomerId = customer.Id,
            Kind = change.Kind,
            Payload = System.Text.Json.JsonSerializer.Serialize(change),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public async Task RecordExpenseAsync(
        CreateExpenseRequest request, string actor, CancellationToken cancellationToken)
    {
        ValidateMoneyEntry(request.Amount, request.Description);
        ValidateCashMovementMethod(request.PaymentMethod);
        db.Expenses.Add(new Expense
        {
            Id = Guid.NewGuid(),
            Description = request.Description.Trim(),
            Amount = request.Amount,
            PaymentMethod = request.PaymentMethod,
            OccurredAtUtc = request.OccurredAt.UtcDateTime,
            Actor = actor
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateCashMovementMethod(PaymentMethod method)
    {
        if (method is not (PaymentMethod.Cash or PaymentMethod.Transfer))
        {
            throw new InvalidOperationException("Para estos movimientos, el medio de pago debe ser Efectivo o QR/transferencia.");
        }
    }

    public async Task<SupplierResponse> CreateSupplierAsync(
        CreateSupplierRequest request, CancellationToken cancellationToken)
    {
        ValidateSupplierRequest(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            TaxId = NormalizeOptional(request.TaxId, 40),
            Phone = NormalizeOptional(request.Phone, 32),
            Address = NormalizeOptional(request.Address, 240),
            ContactName = NormalizeOptional(request.ContactName, 120)
        };
        db.Suppliers.Add(supplier);
        await SynchronizeSupplierItemsAsync(supplier.Id, request.Items, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToSupplierResponse(supplier);
    }

    public async Task<SupplierResponse> UpdateSupplierAsync(
        Guid supplierId, CreateSupplierRequest request, string actor, CancellationToken cancellationToken)
    {
        ValidateSupplierRequest(request);
        var supplier = await db.Suppliers.SingleOrDefaultAsync(
            item => item.Id == supplierId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El proveedor no existe o está inactivo.");
        supplier.Name = request.Name.Trim();
        supplier.TaxId = NormalizeOptional(request.TaxId, 40);
        supplier.Phone = NormalizeOptional(request.Phone, 32);
        supplier.Address = NormalizeOptional(request.Address, 240);
        supplier.ContactName = NormalizeOptional(request.ContactName, 120);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SynchronizeSupplierItemsAsync(supplier.Id, request.Items, cancellationToken);
        db.AuditEntries.Add(NewAudit(actor, "SUPPLIER_UPDATED", nameof(Supplier), supplier.Id,
            $"Datos actualizados: {supplier.Name}"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToSupplierResponse(supplier);
    }

    private async Task SynchronizeSupplierItemsAsync(
        Guid supplierId, IReadOnlyList<SupplierStockItemInput>? requestedItems, CancellationToken cancellationToken)
    {
        if (requestedItems is null)
        {
            return;
        }
        if (requestedItems.Count > 500
            || requestedItems.Any(item => item.StockItemId == Guid.Empty || item.UnitCost < 0
                || decimal.Round(item.UnitCost, 4) != item.UnitCost)
            || requestedItems.Select(item => item.StockItemId).Distinct().Count() != requestedItems.Count)
        {
            throw new InvalidOperationException("La lista de artículos del proveedor contiene datos inválidos.");
        }
        var requestedIds = requestedItems.Select(item => item.StockItemId).ToArray();
        if (requestedIds.Length > 0
            && await db.StockItems.CountAsync(item => requestedIds.Contains(item.Id), cancellationToken) != requestedIds.Length)
        {
            throw new KeyNotFoundException("Un artículo asociado al proveedor ya no existe en el inventario.");
        }
        var existing = await db.SupplierStockItems
            .Where(item => item.SupplierId == supplierId)
            .ToDictionaryAsync(item => item.StockItemId, cancellationToken);
        foreach (var item in existing.Values.Where(item => !requestedIds.Contains(item.StockItemId)))
        {
            db.SupplierStockItems.Remove(item);
        }
        var updatedAt = DateTime.UtcNow;
        foreach (var requested in requestedItems)
        {
            if (existing.TryGetValue(requested.StockItemId, out var linked))
            {
                linked.LastUnitCost = requested.UnitCost;
                linked.LastPurchasedAtUtc = updatedAt;
            }
            else
            {
                db.SupplierStockItems.Add(new SupplierStockItem
                {
                    SupplierId = supplierId,
                    StockItemId = requested.StockItemId,
                    LastUnitCost = requested.UnitCost,
                    LastPurchasedAtUtc = updatedAt
                });
            }
        }
    }

    public async Task<PurchaseResponse> ReceivePurchaseAsync(
        ReceivePurchaseRequest request, string actor, CancellationToken cancellationToken)
    {
        if (request.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Transfer
            && request.CashRegisterSessionId is null)
        {
            throw new InvalidOperationException(
                "Debes abrir una caja antes de registrar una compra en efectivo.");
        }

        if (request.SupplierId == Guid.Empty || string.IsNullOrWhiteSpace(request.InvoiceReference)
            || request.InvoiceReference.Trim().Length > 100 || request.Lines is null || request.Lines.Count == 0
            || !Enum.IsDefined(request.PaymentMethod)
            || request.PaymentMethod == PaymentMethod.Credit && request.CashRegisterSessionId is not null
            || request.PaymentMethod != PaymentMethod.Credit && request.DueAt is not null
            || request.DueAt is DateTimeOffset dueAt && dueAt.Date < request.ReceivedAt.Date
            || request.Lines.Count > 500
            || request.Lines.Any(line => line.StockItemId == Guid.Empty || line.Quantity <= 0
                || decimal.Round(line.Quantity, 6) != line.Quantity || line.UnitCost < 0
                || decimal.Round(line.UnitCost, 4) != line.UnitCost
                || line.LotCode?.Trim().Length > 80
                || line.ExpiresAt is DateTimeOffset expiresAt && expiresAt < request.ReceivedAt))
        {
            throw new InvalidOperationException("La recepción de compra contiene datos inválidos.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        CashRegisterSession? cashSession = null;
        if (request.CashRegisterSessionId is Guid cashRegisterSessionId)
        {
            cashSession = await db.CashRegisterSessions.SingleOrDefaultAsync(
                session => session.Id == cashRegisterSessionId && session.ClosedAtUtc == null,
                cancellationToken);
            if (cashSession is null)
            {
                throw new InvalidOperationException("La sesión de caja indicada no existe o ya está cerrada.");
            }
        }
        if (!await db.Suppliers.AnyAsync(item => item.Id == request.SupplierId && item.IsActive, cancellationToken))
        {
            throw new KeyNotFoundException("El proveedor no existe o está inactivo.");
        }
        var stockIds = request.Lines.Select(line => line.StockItemId).Distinct().ToArray();
        var stock = await db.StockItems.Where(item => stockIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (stock.Count != stockIds.Length)
        {
            throw new KeyNotFoundException("Uno o más insumos de la compra no existen.");
        }
        var supplierItems = await db.SupplierStockItems
            .Where(item => item.SupplierId == request.SupplierId && stockIds.Contains(item.StockItemId))
            .ToDictionaryAsync(item => item.StockItemId, cancellationToken);

        var purchase = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            SupplierId = request.SupplierId,
            CashRegisterSessionId = cashSession?.Id,
            PaymentMethod = request.PaymentMethod,
            ReceivedAtUtc = request.ReceivedAt.UtcDateTime,
            DueAtUtc = request.DueAt?.UtcDateTime,
            InvoiceReference = request.InvoiceReference.Trim(),
            Actor = actor
        };
        foreach (var line in request.Lines)
        {
            var item = stock[line.StockItemId];
            var lineTotal = decimal.Round(line.Quantity * line.UnitCost, 2, MidpointRounding.AwayFromZero);
            var oldValue = item.OnHand * item.UnitCost;
            var newQuantity = item.OnHand + line.Quantity;
            item.UnitCost = newQuantity == 0 ? 0 : decimal.Round(
                (oldValue + line.Quantity * line.UnitCost) / newQuantity, 4, MidpointRounding.AwayFromZero);
            item.OnHand = newQuantity;
            var lot = new StockLot
            {
                Id = Guid.NewGuid(),
                StockItemId = item.Id,
                LotCode = string.IsNullOrWhiteSpace(line.LotCode)
                    ? $"{request.InvoiceReference.Trim()}-{purchase.Lines.Count + 1}"
                    : line.LotCode.Trim(),
                ReceivedQuantity = line.Quantity,
                RemainingQuantity = line.Quantity,
                UnitCost = line.UnitCost,
                ReceivedAtUtc = request.ReceivedAt.UtcDateTime,
                ExpiresAtUtc = line.ExpiresAt?.UtcDateTime
            };
            db.StockLots.Add(lot);
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                StockItemId = item.Id,
                SaleId = Guid.Empty,
                StockLotId = lot.Id,
                QuantityDelta = line.Quantity,
                OccurredAtUtc = request.ReceivedAt.UtcDateTime,
                Reason = "Compra recibida"
            });
            if (!supplierItems.TryGetValue(item.Id, out var supplierItem))
            {
                supplierItem = new SupplierStockItem
                {
                    SupplierId = request.SupplierId,
                    StockItemId = item.Id,
                    LastUnitCost = line.UnitCost,
                    LastPurchasedAtUtc = request.ReceivedAt.UtcDateTime
                };
                supplierItems.Add(item.Id, supplierItem);
                db.SupplierStockItems.Add(supplierItem);
            }
            else
            {
                supplierItem.LastUnitCost = line.UnitCost;
                supplierItem.LastPurchasedAtUtc = request.ReceivedAt.UtcDateTime;
            }
            purchase.Total += lineTotal;
            purchase.Lines.Add(new PurchaseLine
            {
                Id = Guid.NewGuid(),
                PurchaseOrderId = purchase.Id,
                PurchaseOrder = purchase,
                StockItemId = item.Id,
                Quantity = line.Quantity,
                UnitCost = line.UnitCost,
                LineTotal = lineTotal
            });
        }

        purchase.Total = decimal.Round(purchase.Total, 2, MidpointRounding.AwayFromZero);
        purchase.AmountPaid = request.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Transfer
            ? purchase.Total
            : 0m;
        db.PurchaseOrders.Add(purchase);
        if (cashSession is not null)
        {
            db.Expenses.Add(new Expense
            {
                Id = Guid.NewGuid(),
                Description = $"Compra a proveedor: {purchase.InvoiceReference}",
                Amount = purchase.Total,
                PaymentMethod = request.PaymentMethod,
                CashRegisterSessionId = cashSession.Id,
                PurchaseOrderId = purchase.Id,
                OccurredAtUtc = DateTime.UtcNow,
                Actor = actor
            });
        }
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = "PURCHASE_RECEIVED",
            EntityType = "PurchaseOrder",
            EntityId = purchase.Id,
            Reason = purchase.InvoiceReference,
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PurchaseResponse(purchase.Id, purchase.Total, new DateTimeOffset(purchase.ReceivedAtUtc, TimeSpan.Zero));
    }

    public async Task<SupplierPurchasePaymentResponse> RecordSupplierPurchasePaymentAsync(
        SupplierPurchasePaymentRequest request, string actor, CancellationToken cancellationToken)
    {
        if (request.PurchaseOrderId == Guid.Empty || request.CashRegisterSessionId == Guid.Empty
            || request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount
            || request.PaymentMethod is not (PaymentMethod.Cash or PaymentMethod.Transfer)
            || request.Note?.Trim().Length > 160
            || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 64)
        {
            throw new InvalidOperationException("Revisa el monto, el medio de pago y la sesión de caja.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var purchase = await db.PurchaseOrders.SingleOrDefaultAsync(
            item => item.Id == request.PurchaseOrderId, cancellationToken)
            ?? throw new KeyNotFoundException("La compra a proveedor no existe.");
        if (purchase.PaymentMethod != PaymentMethod.Credit)
        {
            throw new InvalidOperationException("Sólo se pueden abonar compras registradas a crédito.");
        }
        var outstanding = decimal.Round(purchase.Total - purchase.AmountPaid, 2, MidpointRounding.AwayFromZero);
        if (outstanding <= 0)
        {
            throw new InvalidOperationException("Esta compra ya está pagada por completo.");
        }
        if (request.Amount > outstanding)
        {
            throw new InvalidOperationException($"El abono supera el saldo pendiente de {outstanding:0.00} Bs.");
        }

        var cashSession = await db.CashRegisterSessions.SingleOrDefaultAsync(
            session => session.Id == request.CashRegisterSessionId && session.ClosedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("La sesión de caja indicada no existe o ya está cerrada.");
        var now = DateTime.UtcNow;
        purchase.AmountPaid = decimal.Round(purchase.AmountPaid + request.Amount, 2, MidpointRounding.AwayFromZero);
        db.SupplierPayments.Add(new SupplierPayment
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = purchase.Id,
            CashRegisterSessionId = cashSession.Id,
            Amount = request.Amount,
            PaymentMethod = request.PaymentMethod,
            OccurredAtUtc = now,
            Actor = actor.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        });
        db.Expenses.Add(new Expense
        {
            Id = Guid.NewGuid(),
            Description = $"Abono compra proveedor {purchase.InvoiceReference}"
                + (string.IsNullOrWhiteSpace(request.Note) ? string.Empty : $": {request.Note.Trim()}"),
            Amount = request.Amount,
            PaymentMethod = request.PaymentMethod,
            CashRegisterSessionId = cashSession.Id,
            OccurredAtUtc = now,
            Actor = actor.Trim()
        });
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor.Trim(),
            Action = "SUPPLIER_PURCHASE_PAYMENT",
            EntityType = nameof(PurchaseOrder),
            EntityId = purchase.Id,
            Reason = $"Abono {request.Amount:0.00} Bs ({request.PaymentMethod}); saldo restante "
                + $"{purchase.Total - purchase.AmountPaid:0.00} Bs",
            OccurredAtUtc = now
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException(
                "El saldo de esta deuda cambió mientras se registraba el pago. Actualiza la pantalla e inténtalo de nuevo.",
                exception);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SupplierPurchasePaymentResponse(
            purchase.Id,
            purchase.Total,
            purchase.AmountPaid,
            decimal.Round(purchase.Total - purchase.AmountPaid, 2, MidpointRounding.AwayFromZero),
            request.Amount,
            new DateTimeOffset(now, TimeSpan.Zero));
    }

    private static void ValidateSupplierRequest(CreateSupplierRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160
            || string.IsNullOrWhiteSpace(request.Phone)
            || request.TaxId?.Trim().Length > 40
            || request.Phone?.Trim().Length > 32
            || string.IsNullOrWhiteSpace(request.Address)
            || request.Address?.Trim().Length > 240
            || string.IsNullOrWhiteSpace(request.ContactName)
            || request.ContactName?.Trim().Length > 120)
        {
            throw new InvalidOperationException("Nombre, teléfono, dirección y contacto son obligatorios; revisa también sus límites.");
        }
    }

    private static SupplierResponse ToSupplierResponse(Supplier supplier) =>
        new(supplier.Id, supplier.Name, supplier.TaxId, supplier.Phone, supplier.Address, supplier.ContactName);

    public async Task<HeldSaleResponse> SaveHeldSaleAsync(HoldSaleRequest request, CancellationToken cancellationToken)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.TerminalId)
            || request.TerminalId.Length > 80 || string.IsNullOrWhiteSpace(request.Payload)
            || request.Payload.Length > 32_768
            || request.DiningTable?.Trim().Length > 40 || request.KitchenNotes?.Trim().Length > 160
            || request.NamedOrder?.Trim().Length > 80 || !Enum.IsDefined(request.FulfillmentMode)
            || request.ExpectedVersion < 0
            || request.CheckoutRequested && string.IsNullOrWhiteSpace(request.DiningTable))
        {
            throw new InvalidOperationException("La venta en espera contiene datos inválidos.");
        }
        using var document = System.Text.Json.JsonDocument.Parse(request.Payload);
        if (request.CustomerId is Guid customerId
            && !await db.Customers.AnyAsync(item => item.Id == customerId, cancellationToken))
        {
            throw new KeyNotFoundException("El cliente de la venta en espera no existe.");
        }
        var diningTable = string.IsNullOrWhiteSpace(request.DiningTable) ? null : request.DiningTable.Trim();
        var heldByTable = diningTable is null
            ? null
            : await db.HeldSales.SingleOrDefaultAsync(item => item.DiningTable == diningTable
                && !item.ClosePending && !item.DeletePending, cancellationToken);
        if (heldByTable is not null && heldByTable.Id != request.Id)
        {
            throw new InvalidOperationException("La mesa ya tiene una orden compartida. Actualiza el plano y recupera esa orden antes de editarla.");
        }
        var held = await db.HeldSales.SingleOrDefaultAsync(item => item.Id == request.Id, cancellationToken);
        if (held is not null && request.ExpectedVersion != held.Version)
        {
            throw new InvalidOperationException("La orden de mesa cambió en otra caja. Actualiza la vista y vuelve a cargar los datos.");
        }
        if (held is null && request.ExpectedVersion != 0)
        {
            throw new InvalidOperationException("La orden compartida ya no existe. Actualiza la vista de mesas.");
        }
        if (held is null)
        {
            held = new HeldSale
            {
                Id = request.Id,
                TerminalId = request.TerminalId,
                Payload = request.Payload,
                DiningTable = diningTable,
                NamedOrder = request.NamedOrder?.Trim(),
                FulfillmentMode = request.FulfillmentMode,
                PriceListId = request.PriceListId,
                CustomerId = request.CustomerId,
                KitchenNotes = request.KitchenNotes?.Trim(),
                StartedAtUtc = request.StartedAt?.UtcDateTime ?? DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1,
                CheckoutRequested = request.CheckoutRequested
            };
            db.HeldSales.Add(held);
        }
        else
        {
            held.TerminalId = request.TerminalId.Trim();
            held.Payload = request.Payload;
            held.DiningTable = diningTable;
            held.NamedOrder = request.NamedOrder?.Trim();
            held.FulfillmentMode = request.FulfillmentMode;
            held.PriceListId = request.PriceListId;
            held.CustomerId = request.CustomerId;
            held.KitchenNotes = request.KitchenNotes?.Trim();
            held.StartedAtUtc ??= request.StartedAt?.UtcDateTime;
            held.UpdatedAtUtc = DateTime.UtcNow;
            held.CheckoutRequested = request.CheckoutRequested;
            held.Version++;
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("La orden de mesa cambió en otra caja. Recarga la orden antes de guardarla.", exception);
        }
        catch (DbUpdateException exception) when (db.Database.IsSqlite() || db.Database.IsNpgsql())
        {
            throw new InvalidOperationException("La mesa ya fue tomada o actualizada en otra caja. Recarga su estado.", exception);
        }
        return ToHeldSaleResponse(held);
    }

    public async Task<IReadOnlyList<HeldSaleResponse>> GetHeldSalesAsync(string terminalId, CancellationToken cancellationToken) =>
        await db.HeldSales.AsNoTracking()
            .Where(item => !item.ClosePending && !item.DeletePending
                && (item.TerminalId == terminalId || item.DiningTable != null))
            .OrderBy(item => item.UpdatedAtUtc)
            .Select(item => new HeldSaleResponse(
                item.Id, item.TerminalId, item.Payload, new DateTimeOffset(item.UpdatedAtUtc, TimeSpan.Zero),
                item.DiningTable, item.KitchenNotes, item.NamedOrder, item.FulfillmentMode, item.PriceListId,
                item.CustomerId, item.Version,
                item.StartedAtUtc == null ? null : new DateTimeOffset(item.StartedAtUtc.Value, TimeSpan.Zero),
                item.CheckoutRequested))
            .ToListAsync(cancellationToken);

    public async Task DeleteHeldSaleAsync(
        Guid id, string terminalId, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        var held = await db.HeldSales.SingleOrDefaultAsync(
            item => item.Id == id && item.TerminalId == terminalId, cancellationToken)
            ?? throw new KeyNotFoundException("La venta en espera no existe en esta caja.");
        if (expectedVersion is not null && expectedVersion != held.Version)
        {
            throw new InvalidOperationException("La orden cambió en otra caja. No se eliminó para evitar perder cambios.");
        }
        db.HeldSales.Remove(held);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KitchenTicketResponse>> GetKitchenQueueAsync(CancellationToken cancellationToken) =>
        await db.KitchenTickets.AsNoTracking()
            .Where(ticket => ticket.State != KitchenTicketState.Served && ticket.State != KitchenTicketState.Cancelled)
            .OrderBy(ticket => ticket.CreatedAtUtc)
            .Select(ticket => new KitchenTicketResponse(
                ticket.Id,
                ticket.SaleId,
                ticket.State,
                new DateTimeOffset(ticket.CreatedAtUtc, TimeSpan.Zero),
                ticket.DiningTable,
                ticket.Lines.Select(line => new KitchenTicketLineResponse(line.ProductName, line.Quantity, line.Notes)).ToList(),
                ticket.NamedOrder,
                ticket.FulfillmentMode))
            .ToListAsync(cancellationToken);

    public async Task UpdateKitchenTicketAsync(
        Guid id, KitchenTicketState state, string actor, CancellationToken cancellationToken)
    {
        var ticket = await db.KitchenTickets.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("La comanda no existe.");
        if (ticket.State == KitchenTicketState.Cancelled || ticket.State == KitchenTicketState.Served
            || !IsAllowedKitchenTransition(ticket.State, state))
        {
            throw new InvalidOperationException("La transición de estado de la comanda no está permitida.");
        }
        ticket.State = state;
        ticket.UpdatedAtUtc = DateTime.UtcNow;
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = "KITCHEN_STATE_CHANGED",
            EntityType = "KitchenTicket",
            EntityId = ticket.Id,
            Reason = state.ToString(),
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateProductPriceAsync(
        Guid id, decimal price, string actor, CancellationToken cancellationToken)
    {
        if (price < 0 || decimal.Round(price, 4) != price)
        {
            throw new InvalidOperationException("El precio no puede ser negativo y admite máximo cuatro decimales.");
        }
        var product = await db.Products.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe.");
        var previousPrice = product.Price;
        product.Price = price;
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = "PRODUCT_PRICE_CHANGED",
            EntityType = "Product",
            EntityId = product.Id,
            Reason = $"{previousPrice:0.####} -> {price:0.####}",
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Product> CreateProductAsync(
        CreateProductRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Sku) || request.Sku.Trim().Length > 64
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160
            || request.Price < 0 || decimal.Round(request.Price, 4) != request.Price
            || request.ServiceUnitCost < 0 || decimal.Round(request.ServiceUnitCost, 4) != request.ServiceUnitCost
            || !Enum.IsDefined(request.Department) || request.Barcode?.Trim().Length > 64
            || request.PrintStation?.Trim().Length > 80)
        {
            throw new InvalidOperationException("Los datos del producto no son válidos.");
        }
        var components = request.Recipe ?? [];
        if (request.IsRecipe && components.Count == 0
            || !request.IsRecipe && components.Count != 0
            || request.IsRecipe && request.StockItemId is not null)
        {
            throw new InvalidOperationException("Un producto elaborado requiere receta; un artículo simple no debe declarar componentes.");
        }
        ValidateDepartmentModel(request.IsRecipe, request.StockItemId, request.Department);
        ValidateRecipeComponents(components);
        if (!request.IsRecipe && request.StockItemId is Guid stockId
            && !await db.StockItems.AnyAsync(item => item.Id == stockId, cancellationToken))
        {
            throw new KeyNotFoundException("El insumo asociado al artículo no existe.");
        }
        var recipeStockIds = components.Select(item => item.StockItemId).Distinct().ToArray();
        if (recipeStockIds.Length != 0
            && await db.StockItems.CountAsync(item => recipeStockIds.Contains(item.Id), cancellationToken) != recipeStockIds.Length)
        {
            throw new KeyNotFoundException("Una receta referencia un insumo que no existe.");
        }
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = request.Sku.Trim(),
            Name = request.Name.Trim(),
            Barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim(),
            Price = request.Price,
            IsRecipe = request.IsRecipe,
            StockItemId = request.StockItemId,
            IsActive = true
        };
        db.Products.Add(product);
        db.ProductDetails.Add(new ProductDetails
        {
            ProductId = product.Id,
            Department = request.Department,
            PrintStation = string.IsNullOrWhiteSpace(request.PrintStation) ? null : request.PrintStation.Trim(),
            ServiceUnitCost = request.ServiceUnitCost
        });
        foreach (var component in components)
        {
            db.RecipeComponents.Add(new RecipeComponent
            {
                ProductId = product.Id,
                StockItemId = component.StockItemId,
                Quantity = component.Quantity
            });
        }
        db.AuditEntries.Add(NewAudit(actor, "PRODUCT_CREATED", "Product", product.Id, product.Name));
        await db.SaveChangesAsync(cancellationToken);
        return product;
    }

    public async Task<Product> UpdateProductAsync(
        Guid id, CreateProductRequest request, string actor, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.Sku) || request.Sku.Trim().Length > 64
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160
            || request.Price < 0 || decimal.Round(request.Price, 4) != request.Price
            || request.ServiceUnitCost < 0 || decimal.Round(request.ServiceUnitCost, 4) != request.ServiceUnitCost
            || !Enum.IsDefined(request.Department) || request.Barcode?.Trim().Length > 64
            || request.PrintStation?.Trim().Length > 80)
        {
            throw new InvalidOperationException("Los datos del producto no son válidos.");
        }
        var components = request.Recipe ?? [];
        if (request.IsRecipe && components.Count == 0
            || !request.IsRecipe && components.Count != 0
            || request.IsRecipe && request.StockItemId is not null)
        {
            throw new InvalidOperationException("Un producto elaborado requiere receta; un artículo simple no debe declarar componentes.");
        }
        ValidateDepartmentModel(request.IsRecipe, request.StockItemId, request.Department);
        ValidateRecipeComponents(components);
        if (!request.IsRecipe && request.StockItemId is Guid linkedStockId
            && !await db.StockItems.AnyAsync(item => item.Id == linkedStockId, cancellationToken))
        {
            throw new KeyNotFoundException("El insumo asociado al artículo no existe.");
        }
        var recipeStockIds = components.Select(item => item.StockItemId).Distinct().ToArray();
        if (recipeStockIds.Length != 0
            && await db.StockItems.CountAsync(item => recipeStockIds.Contains(item.Id), cancellationToken) != recipeStockIds.Length)
        {
            throw new KeyNotFoundException("Una receta referencia un insumo que no existe.");
        }
        var product = await db.Products.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe.");
        var normalizedSku = request.Sku.Trim();
        var normalizedBarcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        if (await db.Products.AnyAsync(item => item.Id != id && item.Sku == normalizedSku, cancellationToken)
            || normalizedBarcode is not null
                && await db.Products.AnyAsync(item => item.Id != id && item.Barcode == normalizedBarcode, cancellationToken))
        {
            throw new InvalidOperationException("El código SKU o de barras ya está asignado a otro producto.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        product.Sku = normalizedSku;
        product.Name = request.Name.Trim();
        product.Barcode = normalizedBarcode;
        product.Price = request.Price;
        product.IsRecipe = request.IsRecipe;
        product.StockItemId = request.IsRecipe ? null : request.StockItemId;
        var details = await db.ProductDetails.SingleOrDefaultAsync(item => item.ProductId == id, cancellationToken);
        if (details is null)
        {
            db.ProductDetails.Add(new ProductDetails
            {
                ProductId = id,
                Department = request.Department,
                PrintStation = NormalizeOptional(request.PrintStation, 80),
                ServiceUnitCost = request.ServiceUnitCost
            });
        }
        else
        {
            details.Department = request.Department;
            details.PrintStation = NormalizeOptional(request.PrintStation, 80);
            details.ServiceUnitCost = request.ServiceUnitCost;
        }

        var existingComponents = await db.RecipeComponents.Where(item => item.ProductId == id).ToListAsync(cancellationToken);
        db.RecipeComponents.RemoveRange(existingComponents);
        foreach (var component in components)
        {
            db.RecipeComponents.Add(new RecipeComponent
            {
                ProductId = id,
                StockItemId = component.StockItemId,
                Quantity = component.Quantity
            });
        }
        db.AuditEntries.Add(NewAudit(actor, "PRODUCT_UPDATED", nameof(Product), id,
            $"Datos y receta actualizados: {product.Name}"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return product;
    }

    public async Task UpdateProductDepartmentAsync(
        Guid productId,
        ProductDepartment department,
        string actor,
        CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty || !Enum.IsDefined(department))
        {
            throw new InvalidOperationException("Selecciona un producto y una categoría válida.");
        }
        var product = await db.Products.SingleOrDefaultAsync(item => item.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe.");
        ValidateDepartmentModel(product.IsRecipe, product.StockItemId, department);
        var details = await db.ProductDetails.SingleOrDefaultAsync(
            item => item.ProductId == productId, cancellationToken)
            ?? throw new InvalidOperationException("El producto no tiene categoría configurada.");
        if (details.Department == department)
        {
            return;
        }

        var previous = details.Department;
        details.Department = department;
        db.AuditEntries.Add(NewAudit(actor, "PRODUCT_DEPARTMENT_CHANGED", nameof(Product), productId,
            $"{previous} -> {department}"));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateDepartmentModel(
        bool isRecipe,
        Guid? stockItemId,
        ProductDepartment department)
    {
        var valid = department switch
        {
            ProductDepartment.RawIngredient or ProductDepartment.Retail or ProductDepartment.FrozenBatch =>
                !isRecipe && stockItemId is not null,
            ProductDepartment.PreparedDirect => isRecipe && stockItemId is null,
            ProductDepartment.Service => !isRecipe && stockItemId is null,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException(department switch
            {
                ProductDepartment.RawIngredient =>
                    "Un insumo vendible debe ser un producto simple enlazado a una existencia.",
                ProductDepartment.PreparedDirect =>
                    "Un preparado del día necesita una receta directa y no puede enlazarse a stock terminado.",
                ProductDepartment.FrozenBatch =>
                    "Un producto congelado por lote debe ser simple, no tener receta de venta y estar enlazado a su stock terminado.",
                ProductDepartment.Retail =>
                    "Un artículo de tienda debe ser simple y tener una existencia enlazada.",
                ProductDepartment.Service =>
                    "Un servicio no debe tener receta ni existencia física enlazada.",
                _ => "La categoría del producto no es válida."
            });
        }
    }

    public async Task UpdateRecipeAsync(
        Guid productId, UpdateRecipeRequest request, string actor, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(item => item.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe.");
        if (!product.IsRecipe)
        {
            throw new InvalidOperationException("Sólo se pueden editar recetas de productos elaborados.");
        }
        var components = request.Components;
        if (components is null || components.Count == 0 || components.Count > 200)
        {
            throw new InvalidOperationException("La receta debe contener entre 1 y 200 insumos.");
        }
        ValidateRecipeComponents(components);
        var stockIds = components.Select(item => item.StockItemId).Distinct().ToArray();
        if (await db.StockItems.CountAsync(item => stockIds.Contains(item.Id), cancellationToken) != stockIds.Length)
        {
            throw new KeyNotFoundException("Una receta referencia un insumo que no existe.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.RecipeComponents.Where(item => item.ProductId == productId).ToListAsync(cancellationToken);
        var requested = components.ToDictionary(item => item.StockItemId);
        foreach (var existing in current)
        {
            if (requested.Remove(existing.StockItemId, out var updated))
            {
                existing.Quantity = updated.Quantity;
            }
            else
            {
                db.RecipeComponents.Remove(existing);
            }
        }
        foreach (var component in requested)
        {
            db.RecipeComponents.Add(new RecipeComponent
            {
                ProductId = productId,
                StockItemId = component.Key,
                Quantity = component.Value.Quantity
            });
        }
        db.AuditEntries.Add(NewAudit(actor, "RECIPE_UPDATED", "Product", productId,
            string.Join(", ", components.Select(item => $"{item.StockItemId:N}:{item.Quantity:0.######}"))));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StockItem> CreateStockItemAsync(
        CreateStockItemRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160
            || string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Trim().Length > 24
            || request.OpeningStock < 0 || decimal.Round(request.OpeningStock, 6) != request.OpeningStock
            || request.ReorderAt < 0 || decimal.Round(request.ReorderAt, 6) != request.ReorderAt
            || request.UnitCost < 0 || decimal.Round(request.UnitCost, 4) != request.UnitCost)
        {
            throw new InvalidOperationException("Los datos del insumo no son válidos.");
        }
        var stock = new StockItem
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Unit = request.Unit.Trim(),
            OnHand = request.OpeningStock,
            ReorderAt = request.ReorderAt,
            UnitCost = request.UnitCost
        };
        db.StockItems.Add(stock);
        if (stock.OnHand > 0)
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                StockItemId = stock.Id,
                SaleId = Guid.Empty,
                QuantityDelta = stock.OnHand,
                OccurredAtUtc = DateTime.UtcNow,
                Reason = "Stock inicial"
            });
        }
        db.AuditEntries.Add(NewAudit(actor, "STOCK_ITEM_CREATED", "StockItem", stock.Id,
            $"Stock inicial: {stock.OnHand:0.######} {stock.Unit}"));
        await db.SaveChangesAsync(cancellationToken);
        return stock;
    }

    public async Task<StockItem> UpdateStockItemAsync(
        Guid id, UpdateStockItemRequest request, string actor, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160
            || string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Trim().Length > 24
            || request.OnHand < 0 || decimal.Round(request.OnHand, 6) != request.OnHand
            || request.ReorderAt < 0 || decimal.Round(request.ReorderAt, 6) != request.ReorderAt
            || request.UnitCost < 0 || decimal.Round(request.UnitCost, 4) != request.UnitCost
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
        {
            throw new InvalidOperationException("Los datos del insumo y el motivo del ajuste no son válidos.");
        }
        var stock = await db.StockItems.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("El insumo no existe.");
        if (!string.Equals(stock.Unit.Trim(), request.Unit.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "La unidad base no se puede cambiar después de crear el insumo, porque define el significado del stock, las compras y las recetas. Crea otro insumo si necesitas una unidad base distinta.");
        }
        var delta = request.OnHand - stock.OnHand;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var occurredAt = DateTime.UtcNow;
        if (delta > 0)
        {
            var lot = new StockLot
            {
                Id = Guid.NewGuid(),
                StockItemId = stock.Id,
                LotCode = $"EDIT-{Guid.NewGuid():N}"[..24],
                ReceivedQuantity = delta,
                RemainingQuantity = delta,
                UnitCost = request.UnitCost,
                ReceivedAtUtc = occurredAt
            };
            db.StockLots.Add(lot);
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                StockItemId = stock.Id,
                StockLotId = lot.Id,
                SaleId = Guid.Empty,
                QuantityDelta = delta,
                OccurredAtUtc = occurredAt,
                Reason = "Ajuste: " + request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 70)]
            });
        }
        else if (delta < 0)
        {
            var reduceBy = -delta;
            var lots = await db.StockLots
                .Where(lot => lot.StockItemId == stock.Id && lot.RemainingQuantity > 0)
                .OrderBy(lot => lot.ExpiresAtUtc == null)
                .ThenBy(lot => lot.ExpiresAtUtc)
                .ThenBy(lot => lot.ReceivedAtUtc)
                .ToListAsync(cancellationToken);
            foreach (var lot in lots)
            {
                if (reduceBy == 0)
                {
                    break;
                }
                var removed = Math.Min(reduceBy, lot.RemainingQuantity);
                lot.RemainingQuantity -= removed;
                reduceBy -= removed;
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    StockItemId = stock.Id,
                    StockLotId = lot.Id,
                    SaleId = Guid.Empty,
                    QuantityDelta = -removed,
                    OccurredAtUtc = occurredAt,
                    Reason = "Ajuste: " + request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 70)]
                });
            }
            if (reduceBy > 0)
            {
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    StockItemId = stock.Id,
                    SaleId = Guid.Empty,
                    QuantityDelta = -reduceBy,
                    OccurredAtUtc = occurredAt,
                    Reason = "Ajuste: " + request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 70)]
                });
            }
        }
        var previousDescription = $"{stock.Name}, {stock.Unit}, costo {stock.UnitCost:0.####}";
        stock.Name = request.Name.Trim();
        stock.Unit = request.Unit.Trim();
        stock.OnHand = request.OnHand;
        stock.ReorderAt = request.ReorderAt;
        stock.UnitCost = request.UnitCost;
        db.AuditEntries.Add(NewAudit(actor, "STOCK_ITEM_UPDATED", nameof(StockItem), id,
            $"{previousDescription} -> {stock.Name}, {stock.Unit}, costo {stock.UnitCost:0.####}; {request.Reason.Trim()}"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stock;
    }

    public async Task AdjustStockAsync(
        Guid stockItemId, decimal delta, string reason, string actor, CancellationToken cancellationToken)
    {
        if (delta == 0 || decimal.Round(delta, 6) != delta
            || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
        {
            throw new InvalidOperationException("La cantidad y el motivo del ajuste son obligatorios.");
        }
        var stock = await db.StockItems.SingleOrDefaultAsync(item => item.Id == stockItemId, cancellationToken)
            ?? throw new KeyNotFoundException("El insumo no existe.");
        if (stock.OnHand + delta < 0)
        {
            throw new InvalidOperationException("El ajuste no puede dejar existencias negativas.");
        }
        var occurredAt = DateTime.UtcNow;
        if (delta > 0)
        {
            var lot = new StockLot
            {
                Id = Guid.NewGuid(),
                StockItemId = stock.Id,
                LotCode = $"ADJUST-{Guid.NewGuid():N}"[..24],
                ReceivedQuantity = delta,
                RemainingQuantity = delta,
                UnitCost = stock.UnitCost,
                ReceivedAtUtc = occurredAt
            };
            db.StockLots.Add(lot);
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                StockItemId = stock.Id,
                StockLotId = lot.Id,
                SaleId = Guid.Empty,
                QuantityDelta = delta,
                OccurredAtUtc = occurredAt,
                Reason = "Ajuste: " + reason.Trim()[..Math.Min(reason.Trim().Length, 70)]
            });
        }
        else
        {
            var reduceBy = -delta;
            var lots = await db.StockLots
                .Where(lot => lot.StockItemId == stock.Id && lot.RemainingQuantity > 0)
                .OrderBy(lot => lot.ExpiresAtUtc == null)
                .ThenBy(lot => lot.ExpiresAtUtc)
                .ThenBy(lot => lot.ReceivedAtUtc)
                .ToListAsync(cancellationToken);
            foreach (var lot in lots)
            {
                if (reduceBy == 0)
                {
                    break;
                }
                var removed = Math.Min(reduceBy, lot.RemainingQuantity);
                lot.RemainingQuantity -= removed;
                reduceBy -= removed;
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    StockItemId = stock.Id,
                    StockLotId = lot.Id,
                    SaleId = Guid.Empty,
                    QuantityDelta = -removed,
                    OccurredAtUtc = occurredAt,
                    Reason = "Ajuste: " + reason.Trim()[..Math.Min(reason.Trim().Length, 70)]
                });
            }
            if (reduceBy > 0)
            {
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    StockItemId = stock.Id,
                    SaleId = Guid.Empty,
                    QuantityDelta = -reduceBy,
                    OccurredAtUtc = occurredAt,
                    Reason = "Ajuste: " + reason.Trim()[..Math.Min(reason.Trim().Length, 70)]
                });
            }
        }
        stock.OnHand += delta;
        db.AuditEntries.Add(NewAudit(actor, "STOCK_ADJUSTED", "StockItem", stock.Id, reason.Trim()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("El stock cambió en paralelo; vuelve a consultar y repite el ajuste.", exception);
        }
    }

    public async Task SetProductPhotoAsync(
        Guid id, ProductPhotoUpload upload, CancellationToken cancellationToken)
    {
        var type = upload.ContentType.Trim().ToLowerInvariant();
        if (type is not ("image/jpeg" or "image/png" or "image/webp")
            || string.IsNullOrWhiteSpace(upload.Base64Data) || upload.Base64Data.Length > 7_000_000)
        {
            throw new InvalidOperationException("La imagen debe ser JPEG, PNG o WebP y no superar 5 MB.");
        }
        var productExists = await db.Products.AnyAsync(item => item.Id == id, cancellationToken);
        if (!productExists)
        {
            throw new KeyNotFoundException("El producto no existe.");
        }
        byte[] data;
        try
        {
            data = Convert.FromBase64String(upload.Base64Data);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("La imagen no contiene Base64 válido.", exception);
        }
        if (data.Length is 0 or > 5_242_880 || !MatchesImageSignature(data, type))
        {
            throw new InvalidOperationException("El contenido de imagen no coincide con el formato declarado o supera 5 MB.");
        }
        var photo = await db.ProductPhotos.SingleOrDefaultAsync(item => item.ProductId == id, cancellationToken);
        if (photo is null)
        {
            db.ProductPhotos.Add(new ProductPhoto
            {
                ProductId = id,
                ContentType = type,
                Data = data,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            photo.ContentType = type;
            photo.Data = data;
            photo.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteProductPhotoAsync(Guid id, CancellationToken cancellationToken)
    {
        var photo = await db.ProductPhotos.SingleOrDefaultAsync(item => item.ProductId == id, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no tiene foto.");
        db.ProductPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StockLotResponse>> GetStockLotsAsync(
        bool includeDepleted, CancellationToken cancellationToken) =>
        await db.StockLots.AsNoTracking()
            .Where(item => includeDepleted || item.RemainingQuantity > 0)
            .OrderBy(item => item.ExpiresAtUtc == null)
            .ThenBy(item => item.ExpiresAtUtc)
            .ThenBy(item => item.StockItemId)
            .Select(item => new StockLotResponse(
                item.Id,
                item.StockItemId,
                item.LotCode,
                item.ReceivedQuantity,
                item.RemainingQuantity,
                item.UnitCost,
                new DateTimeOffset(item.ReceivedAtUtc, TimeSpan.Zero),
                item.ExpiresAtUtc == null ? null : new DateTimeOffset(item.ExpiresAtUtc.Value, TimeSpan.Zero)))
            .ToListAsync(cancellationToken);

    public async Task<StockCountResponse> CreateStockCountAsync(
        CreateStockCountRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500
            || request.Lines is null || request.Lines.Count is 0 or > 500
            || request.Lines.Any(line => line.StockItemId == Guid.Empty || line.CountedQuantity < 0
                || decimal.Round(line.CountedQuantity, 6) != line.CountedQuantity)
            || request.Lines.Select(line => line.StockItemId).Distinct().Count() != request.Lines.Count)
        {
            throw new InvalidOperationException("El recuento requiere motivo y cantidades válidas, sin insumos duplicados.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = request.Lines.Select(line => line.StockItemId).ToArray();
        var stock = await db.StockItems.Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (stock.Count != ids.Length)
        {
            throw new KeyNotFoundException("El recuento contiene insumos inexistentes.");
        }

        var count = new StockCount
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Reason = request.Reason.Trim(),
            OccurredAtUtc = DateTime.UtcNow
        };
        var absoluteVariance = 0m;
        foreach (var line in request.Lines)
        {
            var item = stock[line.StockItemId];
            var expected = item.OnHand;
            var delta = line.CountedQuantity - expected;
            absoluteVariance += Math.Abs(delta);
            count.Lines.Add(new StockCountLine
            {
                Id = Guid.NewGuid(),
                StockItemId = item.Id,
                ExpectedQuantity = expected,
                CountedQuantity = line.CountedQuantity
            });
            if (delta < 0)
            {
                var reduceBy = -delta;
                var lots = await db.StockLots
                    .Where(lot => lot.StockItemId == item.Id && lot.RemainingQuantity > 0)
                    .OrderBy(lot => lot.ExpiresAtUtc == null)
                    .ThenBy(lot => lot.ExpiresAtUtc)
                    .ThenBy(lot => lot.ReceivedAtUtc)
                    .ToListAsync(cancellationToken);
                foreach (var lot in lots)
                {
                    if (reduceBy == 0)
                    {
                        break;
                    }
                    var lotDelta = Math.Min(reduceBy, lot.RemainingQuantity);
                    lot.RemainingQuantity -= lotDelta;
                    reduceBy -= lotDelta;
                    db.StockMovements.Add(new StockMovement
                    {
                        Id = Guid.NewGuid(),
                        StockItemId = item.Id,
                        StockLotId = lot.Id,
                        SaleId = Guid.Empty,
                        QuantityDelta = -lotDelta,
                        OccurredAtUtc = count.OccurredAtUtc,
                        Reason = "Recuento físico"
                    });
                }
            }
            else if (delta > 0)
            {
                var lot = new StockLot
                {
                    Id = Guid.NewGuid(),
                    StockItemId = item.Id,
                    LotCode = $"RECOUNT-{count.Id:N}"[..24],
                    ReceivedQuantity = delta,
                    RemainingQuantity = delta,
                    UnitCost = item.UnitCost,
                    ReceivedAtUtc = count.OccurredAtUtc
                };
                db.StockLots.Add(lot);
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    StockItemId = item.Id,
                    StockLotId = lot.Id,
                    SaleId = Guid.Empty,
                    QuantityDelta = delta,
                    OccurredAtUtc = count.OccurredAtUtc,
                    Reason = "Recuento físico"
                });
            }
            item.OnHand = line.CountedQuantity;
        }
        db.StockCounts.Add(count);
        db.AuditEntries.Add(NewAudit(actor, "STOCK_COUNTED", "StockCount", count.Id, count.Reason));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("El inventario cambió durante el recuento; vuelve a consultar el stock.", exception);
        }

        return new StockCountResponse(count.Id, count.Lines.Count, absoluteVariance);
    }

    public async Task<CustomPaymentTypeResponse> CreateCustomPaymentTypeAsync(
        CreateCustomPaymentTypeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
        {
            throw new InvalidOperationException("El medio de pago requiere un nombre de hasta 80 caracteres.");
        }
        var item = new CustomPaymentType
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            IsActive = true
        };
        db.CustomPaymentTypes.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return new CustomPaymentTypeResponse(item.Id, item.Name, item.IsActive);
    }

    public async Task<IReadOnlyList<CustomPaymentTypeResponse>> GetCustomPaymentTypesAsync(
        CancellationToken cancellationToken) =>
        await db.CustomPaymentTypes.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .Select(item => new CustomPaymentTypeResponse(item.Id, item.Name, item.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<PriceListResponse> CreatePriceListAsync(
        CreatePriceListRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
        {
            throw new InvalidOperationException("La lista de precios requiere un nombre de hasta 80 caracteres.");
        }
        var item = new PriceList
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            IsActive = true
        };
        db.PriceLists.Add(item);
        db.AuditEntries.Add(NewAudit(actor, "PRICE_LIST_CREATED", "PriceList", item.Id, item.Name));
        await db.SaveChangesAsync(cancellationToken);
        return new PriceListResponse(item.Id, item.Name, item.IsActive);
    }

    public async Task SetProductListPriceAsync(
        Guid priceListId,
        SetProductListPriceRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (request.ProductId == Guid.Empty || request.Price < 0 || decimal.Round(request.Price, 2) != request.Price)
        {
            throw new InvalidOperationException("El precio preferencial debe ser no negativo y tener dos decimales BOB.");
        }
        var priceList = await db.PriceLists.SingleOrDefaultAsync(
            item => item.Id == priceListId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("La lista de precios no existe o está inactiva.");
        var product = await db.Products.SingleOrDefaultAsync(
            item => item.Id == request.ProductId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe o está inactivo.");
        var entry = await db.ProductListPrices.SingleOrDefaultAsync(
            item => item.PriceListId == priceListId && item.ProductId == request.ProductId, cancellationToken);
        if (entry is null)
        {
            db.ProductListPrices.Add(new ProductListPrice
            {
                PriceListId = priceList.Id,
                ProductId = product.Id,
                Price = request.Price
            });
        }
        else
        {
            entry.Price = request.Price;
        }
        db.AuditEntries.Add(NewAudit(
            actor, "PRODUCT_LIST_PRICE_SET", "Product", product.Id, $"{priceList.Name}: {request.Price:0.00} BOB"));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PromotionResponse> CreatePromotionAsync(
        CreatePromotionRequest request, string actor, CancellationToken cancellationToken)
    {
        if (request.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Trim().Length > 100 || !Enum.IsDefined(request.Kind)
            || request.StartsAt >= request.EndsAt || request.Value < 0
            || decimal.Round(request.Value, 2) != request.Value
            || request.Kind == PromotionKind.PercentOff && request.Value is <= 0 or > 100)
        {
            throw new InvalidOperationException("La promoción contiene valores inválidos o un rango horario vacío.");
        }
        var product = await db.Products.SingleOrDefaultAsync(
            item => item.Id == request.ProductId && item.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("El producto no existe o está inactivo.");
        if (request.Kind == PromotionKind.FixedPrice && request.Value >= product.Price)
        {
            throw new InvalidOperationException("El precio promocional debe ser menor al precio regular.");
        }
        var item = new Promotion
        {
            Id = Guid.NewGuid(),
            ProductId = request.ProductId,
            Name = request.Name.Trim(),
            Kind = request.Kind,
            Value = request.Value,
            StartsAtUtc = request.StartsAt.UtcDateTime,
            EndsAtUtc = request.EndsAt.UtcDateTime,
            IsActive = true
        };
        db.Promotions.Add(item);
        db.AuditEntries.Add(NewAudit(actor, "PROMOTION_CREATED", "Promotion", item.Id,
            $"{product.Name}: {item.Name}, vigente {request.StartsAt:O} - {request.EndsAt:O}"));
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(item);
    }

    public async Task<IReadOnlyList<PromotionResponse>> GetPromotionsAsync(CancellationToken cancellationToken) =>
        await db.Promotions.AsNoTracking()
            .OrderBy(item => item.StartsAtUtc)
            .Select(item => new PromotionResponse(
                item.Id,
                item.ProductId,
                item.Name,
                item.Kind,
                item.Value,
                new DateTimeOffset(item.StartsAtUtc, TimeSpan.Zero),
                new DateTimeOffset(item.EndsAtUtc, TimeSpan.Zero),
                item.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<ReportSummaryResponse> GetReportAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (from >= to || to - from > TimeSpan.FromDays(366))
        {
            throw new InvalidOperationException("El rango debe ser creciente y no superar 366 días.");
        }
        var fromUtc = from.UtcDateTime;
        var toUtc = to.UtcDateTime;
        var sales = await db.Sales.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= fromUtc && item.OccurredAtUtc < toUtc
                && item.State == SaleState.Completed)
            .Select(item => new { item.Id, item.Total })
            .ToListAsync(cancellationToken);
        var saleIds = sales.Select(item => item.Id).ToArray();
        var costSnapshots = await db.SaleCostSnapshots.AsNoTracking()
            .Where(item => saleIds.Contains(item.SaleId))
            .Select(item => new { item.SaleId, item.Cost })
            .ToListAsync(cancellationToken);
        var saleLines = await db.SaleLines.AsNoTracking()
            .Where(item => saleIds.Contains(item.SaleId))
            .Select(item => new { item.Id, item.SaleId })
            .ToListAsync(cancellationToken);
        var lineIds = saleLines.Select(item => item.Id).ToArray();
        var costedLineIds = await db.SaleLineFinancialSnapshots.AsNoTracking()
            .Where(item => lineIds.Contains(item.SaleLineId) && item.UnitCost != null)
            .Select(item => item.SaleLineId)
            .ToListAsync(cancellationToken);
        var expenses = await db.Expenses.AsNoTracking()
            .Where(item => item.OccurredAtUtc >= fromUtc && item.OccurredAtUtc < toUtc)
            .Select(item => item.Amount)
            .ToListAsync(cancellationToken);
        var purchases = await db.PurchaseOrders.AsNoTracking()
            .Where(item => item.ReceivedAtUtc >= fromUtc && item.ReceivedAtUtc < toUtc)
            .Select(item => item.Total)
            .ToListAsync(cancellationToken);
        var revenue = sales.Sum(item => item.Total);
        var costedLineIdSet = costedLineIds.ToHashSet();
        var salesWithUncostedLines = saleLines
            .Where(line => !costedLineIdSet.Contains(line.Id))
            .Select(line => line.SaleId)
            .ToHashSet();
        var salesWithLines = saleLines.Select(line => line.SaleId).ToHashSet();
        var missingCosts = saleIds.Count(id =>
            !salesWithLines.Contains(id) || salesWithUncostedLines.Contains(id));
        var cost = costSnapshots.Sum(item => item.Cost);
        var expenseTotal = expenses.Sum();
        var grossProfit = missingCosts == 0 ? revenue - cost : (decimal?)null;
        var purchaseInvestment = purchases.Sum();
        return new ReportSummaryResponse(
            from,
            to,
            revenue,
            cost,
            expenseTotal,
            grossProfit,
            sales.Count,
            missingCosts,
            purchaseInvestment,
            grossProfit is decimal knownGrossProfit ? knownGrossProfit - expenseTotal : null);
    }

    private static CustomerLedgerEntry NewLedger(
        CustomerAccount customer,
        CustomerLedgerKind kind,
        decimal walletDelta,
        decimal creditDelta,
        string description,
        string actor) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customer.Id,
        Kind = kind,
        WalletDelta = walletDelta,
        CreditDelta = creditDelta,
        OccurredAtUtc = DateTime.UtcNow,
        Actor = actor,
        Description = description.Trim()
    };

    private static AuditEntry NewAudit(string actor, string action, string entityType, Guid entityId, string reason) => new()
    {
        Id = Guid.NewGuid(),
        Actor = actor,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        Reason = reason.Length > 500 ? reason[..500] : reason,
        OccurredAtUtc = DateTime.UtcNow
    };

    private static void ValidateRecipeComponents(IReadOnlyList<RecipeComponentInput> components)
    {
        if (components.Count > 200
            || components.Any(item => item.StockItemId == Guid.Empty || item.Quantity <= 0
                || decimal.Round(item.Quantity, 6) != item.Quantity)
            || components.Select(item => item.StockItemId).Distinct().Count() != components.Count)
        {
            throw new InvalidOperationException("Los insumos de receta deben ser únicos y tener cantidades positivas de hasta seis decimales.");
        }
    }

    private static CustomerResponse ToResponse(CustomerAccount customer) =>
        new(customer.Id, customer.Name, customer.Phone, customer.WalletBalance, customer.CreditBalance,
            customer.CreditLimit, customer.Address, customer.TaxId, customer.Notes);

    private static HeldSaleResponse ToHeldSaleResponse(HeldSale held) =>
        new(held.Id, held.TerminalId, held.Payload, new DateTimeOffset(held.UpdatedAtUtc, TimeSpan.Zero),
            held.DiningTable, held.KitchenNotes, held.NamedOrder, held.FulfillmentMode,
            held.PriceListId, held.CustomerId, held.Version,
            held.StartedAtUtc is null ? null : new DateTimeOffset(held.StartedAtUtc.Value, TimeSpan.Zero),
            held.CheckoutRequested);

    private static PromotionResponse ToResponse(Promotion promotion) =>
        new(promotion.Id, promotion.ProductId, promotion.Name, promotion.Kind, promotion.Value,
            new DateTimeOffset(promotion.StartsAtUtc, TimeSpan.Zero),
            new DateTimeOffset(promotion.EndsAtUtc, TimeSpan.Zero), promotion.IsActive);

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized?.Length > maxLength)
        {
            throw new InvalidOperationException("Un campo excede la longitud permitida.");
        }
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static void ValidateMoneyEntry(decimal amount, string description)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount || string.IsNullOrWhiteSpace(description)
            || description.Trim().Length > 300)
        {
            throw new InvalidOperationException("Importe o descripción de movimiento inválido.");
        }
    }

    private static bool IsAllowedKitchenTransition(KitchenTicketState current, KitchenTicketState next) =>
        (current, next) is (KitchenTicketState.Queued, KitchenTicketState.Preparing)
            or (KitchenTicketState.Preparing, KitchenTicketState.Ready)
            or (KitchenTicketState.Ready, KitchenTicketState.Served);

    private static bool MatchesImageSignature(byte[] data, string contentType) => contentType switch
    {
        "image/jpeg" => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
        "image/png" => data.Length >= 8 && data.AsSpan(0, 8)
            .SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/webp" => data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };
}
