using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class PaymentReportService(TiendaDbContext db)
{
    public async Task<IReadOnlyList<PaymentMethodReportPoint>> LoadAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (from >= to)
        {
            throw new InvalidOperationException("El rango debe ser creciente.");
        }

        var fromUtc = from.UtcDateTime;
        var toUtc = to.UtcDateTime;
        var saleIds = await db.Sales.AsNoTracking()
            .Where(sale => sale.State == SaleState.Completed
                && sale.OccurredAtUtc >= fromUtc && sale.OccurredAtUtc < toUtc)
            .Select(sale => sale.Id)
            .ToArrayAsync(cancellationToken);
        if (saleIds.Length == 0)
        {
            return [];
        }

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => saleIds.Contains(payment.SaleId))
            .Select(payment => new { payment.Method, payment.Amount, payment.Change })
            .ToListAsync(cancellationToken);
        var creditSales = await db.CustomerLedger.AsNoTracking()
            .Where(entry => entry.SaleId != null
                && saleIds.Contains(entry.SaleId.Value)
                && entry.Kind == CustomerLedgerKind.CreditSale)
            .SumAsync(entry => (decimal?)entry.CreditDelta, cancellationToken) ?? 0m;

        return Enum.GetValues<PaymentMethod>()
            .Select(method => new PaymentMethodReportPoint(
                method switch
                {
                    PaymentMethod.Cash => "Efectivo",
                    PaymentMethod.Transfer => "QR / transferencia",
                    PaymentMethod.Credit => "Fiado",
                    PaymentMethod.CustomerWallet => "Monedero",
                    _ => "Otros"
                },
                method switch
                {
                    PaymentMethod.Cash => payments
                        .Where(payment => payment.Method == method)
                        .Sum(payment => Math.Max(0m, payment.Amount - payment.Change)),
                    PaymentMethod.Credit => creditSales,
                    _ => payments
                        .Where(payment => payment.Method == method)
                        .Sum(payment => payment.Amount)
                }))
            .Where(point => point.Amount > 0m)
            .ToArray();
    }
}
