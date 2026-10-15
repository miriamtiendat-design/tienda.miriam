namespace TiendaMiriam.Infrastructure;

public static class AuditExecutionContext
{
    private static readonly AsyncLocal<AuditIdentity?> CurrentIdentity = new();

    public static string Actor => CurrentIdentity.Value?.Actor ?? "sistema";
    public static string TerminalId => CurrentIdentity.Value?.TerminalId ?? Environment.MachineName;
    public static string? Observation => CurrentIdentity.Value?.Observation;

    public static void Set(string actor, string? terminalId = null)
    {
        CurrentIdentity.Value = new AuditIdentity(
            string.IsNullOrWhiteSpace(actor) ? "sistema" : actor.Trim(),
            string.IsNullOrWhiteSpace(terminalId) ? Environment.MachineName : terminalId.Trim(),
            CurrentIdentity.Value?.Observation);
    }

    public static void SetObservation(string? observation)
    {
        if (CurrentIdentity.Value is { } identity)
        {
            CurrentIdentity.Value = identity with
            {
                Observation = string.IsNullOrWhiteSpace(observation) ? null : observation.Trim()
            };
        }
    }

    public static void ClearObservation() => SetObservation(null);

    private sealed record AuditIdentity(string Actor, string TerminalId, string? Observation);
}
