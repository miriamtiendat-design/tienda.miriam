using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class CashRegisterService(TiendaDbContext db)
{
    public async Task<CashRegisterSession> OpenAsync(
        string terminalId,
        string openedBy,
        decimal openingCash,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(terminalId, openedBy);
        ValidateMoney(openingCash, "El fondo inicial");
        if (await db.CashRegisterSessions.AnyAsync(
            item => item.TerminalId == terminalId && item.ClosedAtUtc == null,
            cancellationToken))
        {
            throw new InvalidOperationException("Esta caja ya tiene una sesión abierta.");
        }

        var now = DateTime.UtcNow;
        var session = new CashRegisterSession
        {
            Id = Guid.NewGuid(),
            TerminalId = terminalId.Trim(),
            OpenedBy = openedBy.Trim(),
            OpenedAtUtc = now,
            OpeningCash = openingCash
        };
        db.CashRegisterSessions.Add(session);
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = openedBy.Trim(),
            Action = "CASH_SESSION_OPENED",
            EntityType = nameof(CashRegisterSession),
            EntityId = session.Id,
            Reason = $"Fondo inicial: {openingCash:0.00} BOB",
            OccurredAtUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException("No se pudo abrir la sesión de caja; actualiza el estado y vuelve a intentar.", exception);
        }
        return session;
    }

    public async Task<CashSessionSummary?> GetOpenSummaryAsync(
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.CashRegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TerminalId == terminalId && item.ClosedAtUtc == null,
                cancellationToken);
        return session is null ? null : await BuildSummaryAsync(session, cancellationToken);
    }

    public async Task<CashSessionSummary> CloseAsync(
        Guid sessionId,
        string terminalId,
        string closedBy,
        decimal countedCash,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(terminalId, closedBy);
        ValidateMoney(countedCash, "El efectivo contado");
        if (note?.Trim().Length > 300)
        {
            throw new InvalidOperationException("La observación no puede superar 300 caracteres.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.CashRegisterSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId && item.TerminalId == terminalId && item.ClosedAtUtc == null,
            cancellationToken) ?? throw new InvalidOperationException("La sesión no existe, pertenece a otra caja o ya fue cerrada.");

        var summary = await BuildSummaryAsync(session, cancellationToken);
        var now = DateTime.UtcNow;
        session.ClosedAtUtc = now;
        session.ClosedBy = closedBy.Trim();
        session.CountedCash = countedCash;
        session.ExpectedCash = summary.ExpectedCash;
        session.Variance = decimal.Round(countedCash - summary.ExpectedCash, 2, MidpointRounding.AwayFromZero);
        session.ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        session.Version++;
        session.SyncPending = true;
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = closedBy.Trim(),
            Action = "CASH_SESSION_CLOSED",
            EntityType = nameof(CashRegisterSession),
            EntityId = session.Id,
            Reason = $"Esperado {summary.ExpectedCash:0.00}; contado {countedCash:0.00}; diferencia {session.Variance:0.00} BOB"
                + (session.ClosingNote is null ? string.Empty : $"; {session.ClosingNote}"),
            OccurredAtUtc = now
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("La caja cambió mientras se hacía el arqueo. Actualiza el resumen y vuelve a cerrar.", exception);
        }
        await transaction.CommitAsync(cancellationToken);
        return summary with
        {
            ClosedAt = new DateTimeOffset(now, TimeSpan.Zero),
            CountedCash = countedCash,
            Variance = session.Variance,
            ClosedBy = session.ClosedBy
        };
    }

    public async Task<IReadOnlyList<CashSessionSummary>> GetRecentSessionsAsync(
        string terminalId,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var sessions = await db.CashRegisterSessions.AsNoTracking()
            .Where(item => item.TerminalId == terminalId)
            .OrderByDescending(item => item.OpenedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
        var results = new List<CashSessionSummary>(sessions.Count);
        foreach (var session in sessions)
        {
            results.Add(await BuildSummaryAsync(session, cancellationToken));
        }
        return results;
    }

    private async Task<CashSessionSummary> BuildSummaryAsync(
        CashRegisterSession session,
        CancellationToken cancellationToken)
    {
        var end = session.ClosedAtUtc ?? DateTime.UtcNow;
        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.OccurredAtUtc >= session.OpenedAtUtc
                && payment.OccurredAtUtc <= end
                && db.Sales.Any(sale => sale.Id == payment.SaleId
                    && sale.TerminalId == session.TerminalId
                    && sale.CashRegisterSessionId == session.Id
                    && sale.State == SaleState.Completed))
            .Select(payment => new { payment.Method, payment.Amount, payment.Change })
            .ToListAsync(cancellationToken);
        var externalLedger = await db.CustomerLedger.AsNoTracking()
            .Where(entry => entry.SaleId == null
                && (entry.CashRegisterSessionId == session.Id
                    || entry.CashRegisterSessionId == null
                        && entry.OccurredAtUtc >= session.OpenedAtUtc
                        && entry.OccurredAtUtc <= end)
                && (entry.Kind == CustomerLedgerKind.WalletTopUp
                    || entry.Kind == CustomerLedgerKind.CreditPayment))
            .Select(entry => new
            {
                entry.Kind,
                entry.PaymentMethod,
                entry.WalletDelta,
                entry.CreditDelta
            })
            .ToListAsync(cancellationToken);
        var expenses = await db.Expenses.AsNoTracking()
            .Where(expense => expense.CashRegisterSessionId == session.Id
                || expense.CashRegisterSessionId == null
                    && expense.OccurredAtUtc >= session.OpenedAtUtc
                    && expense.OccurredAtUtc <= end)
            .Select(expense => new { expense.Amount, expense.PaymentMethod })
            .ToListAsync(cancellationToken);
        var cashPayments = payments.Where(payment => payment.Method == PaymentMethod.Cash).ToArray();
        var cashReceived = cashPayments.Sum(payment => payment.Amount);
        var changePaid = cashPayments.Sum(payment => payment.Change);
        var qrReceived = payments.Where(payment => payment.Method == PaymentMethod.Transfer).Sum(payment => payment.Amount);
        var walletApplied = payments.Where(payment => payment.Method == PaymentMethod.CustomerWallet).Sum(payment => payment.Amount);
        var creditIssued = payments.Where(payment => payment.Method == PaymentMethod.Credit).Sum(payment => payment.Amount);
        var otherPayments = payments.Where(payment => payment.Method == PaymentMethod.Custom).Sum(payment => payment.Amount);
        var walletTopUpsCash = externalLedger
            .Where(entry => entry.Kind == CustomerLedgerKind.WalletTopUp && entry.PaymentMethod == PaymentMethod.Cash)
            .Sum(entry => entry.WalletDelta);
        var walletTopUpsQr = externalLedger
            .Where(entry => entry.Kind == CustomerLedgerKind.WalletTopUp && entry.PaymentMethod == PaymentMethod.Transfer)
            .Sum(entry => entry.WalletDelta);
        var creditPaymentsCash = externalLedger
            .Where(entry => entry.Kind == CustomerLedgerKind.CreditPayment && entry.PaymentMethod == PaymentMethod.Cash)
            .Sum(entry => -entry.CreditDelta);
        var creditPaymentsQr = externalLedger
            .Where(entry => entry.Kind == CustomerLedgerKind.CreditPayment && entry.PaymentMethod == PaymentMethod.Transfer)
            .Sum(entry => -entry.CreditDelta);
        var cashExpenses = expenses
            .Where(expense => expense.PaymentMethod == PaymentMethod.Cash)
            .Sum(expense => expense.Amount);
        var qrExpenses = expenses
            .Where(expense => expense.PaymentMethod == PaymentMethod.Transfer)
            .Sum(expense => expense.Amount);
        var expectedCash = decimal.Round(
            session.OpeningCash + cashReceived - changePaid
                + walletTopUpsCash + creditPaymentsCash - cashExpenses,
            2,
            MidpointRounding.AwayFromZero);
        return new CashSessionSummary(
            session.Id,
            session.TerminalId,
            session.OpenedBy,
            new DateTimeOffset(session.OpenedAtUtc, TimeSpan.Zero),
            session.OpeningCash,
            cashReceived,
            changePaid,
            qrReceived,
            walletApplied,
            creditIssued,
            otherPayments,
            walletTopUpsCash,
            walletTopUpsQr,
            creditPaymentsCash,
            creditPaymentsQr,
            cashExpenses,
            qrExpenses,
            expectedCash,
            session.ClosedAtUtc is DateTime closedAt ? new DateTimeOffset(closedAt, TimeSpan.Zero) : null,
            session.ClosedBy,
            session.CountedCash,
            session.Variance,
            session.ClosingNote);
    }

    private static void ValidateActor(string terminalId, string actor)
    {
        if (string.IsNullOrWhiteSpace(terminalId) || terminalId.Trim().Length > 80
            || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 64)
        {
            throw new InvalidOperationException("La caja o el usuario no son válidos.");
        }
    }

    private static void ValidateMoney(decimal amount, string label)
    {
        if (amount < 0 || decimal.Round(amount, 2) != amount)
        {
            throw new InvalidOperationException($"{label} debe ser no negativo y tener dos decimales como máximo.");
        }
    }
}

public sealed record CashSessionSummary(
    Guid Id,
    string TerminalId,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    decimal OpeningCash,
    decimal CashReceived,
    decimal ChangePaid,
    decimal QrReceived,
    decimal WalletApplied,
    decimal CreditIssued,
    decimal OtherPayments,
    decimal WalletTopUpsCash,
    decimal WalletTopUpsQr,
    decimal CreditPaymentsCash,
    decimal CreditPaymentsQr,
    decimal CashExpenses,
    decimal QrExpenses,
    decimal ExpectedCash,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    decimal? CountedCash,
    decimal? Variance,
    string? ClosingNote);
