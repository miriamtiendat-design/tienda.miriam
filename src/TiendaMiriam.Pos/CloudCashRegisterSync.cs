using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Pos;

internal static class CloudApiClientFactory
{
    public static async Task<HttpClient> CreateAuthorizedClientAsync(
        bool administrator = false,
        CancellationToken cancellationToken = default)
    {
        var apiUrl = Environment.GetEnvironmentVariable("TIENDA_CLOUD_API_URL");
        var allowInsecureLoopback = string.Equals(
            Environment.GetEnvironmentVariable("TIENDA_ALLOW_INSECURE_LOCAL_API"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!CloudApiUrlPolicy.TryValidate(apiUrl, allowInsecureLoopback, out var apiUri)
            || apiUri is null)
        {
            throw new InvalidOperationException(
                "Configura TIENDA_CLOUD_API_URL con una URL HTTPS válida para consultar o sincronizar cierres.");
        }

        var client = new HttpClient { BaseAddress = apiUri, Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            var token = Environment.GetEnvironmentVariable(administrator
                ? "TIENDA_CLOUD_ADMIN_ACCESS_TOKEN"
                : "TIENDA_CLOUD_ACCESS_TOKEN");
            if (administrator && string.IsNullOrWhiteSpace(token))
            {
                token = Environment.GetEnvironmentVariable("TIENDA_CLOUD_ACCESS_TOKEN");
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                var username = Environment.GetEnvironmentVariable(administrator
                    ? "TIENDA_CLOUD_ADMIN_USERNAME"
                    : "TIENDA_CLOUD_USERNAME");
                var password = Environment.GetEnvironmentVariable(administrator
                    ? "TIENDA_CLOUD_ADMIN_PASSWORD"
                    : "TIENDA_CLOUD_PASSWORD");
                if (administrator && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
                {
                    username = Environment.GetEnvironmentVariable("TIENDA_CLOUD_USERNAME");
                    password = Environment.GetEnvironmentVariable("TIENDA_CLOUD_PASSWORD");
                }
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException(
                        administrator
                            ? "Configura una credencial cloud de Administrador para consultar los cierres."
                            : "Configura TIENDA_CLOUD_ACCESS_TOKEN o TIENDA_CLOUD_USERNAME y TIENDA_CLOUD_PASSWORD.");
                }

                using var response = await client.PostAsJsonAsync(
                    "/api/auth/login", new LoginRequest(username, password), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"No se pudo autenticar contra la nube (HTTP {(int)response.StatusCode}).");
                }
                var login = await response.Content.ReadFromJsonAsync<LoginResponse>(
                    cancellationToken: cancellationToken);
                token = login?.AccessToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("La API cloud devolvió una sesión de autenticación inválida.");
                }
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal static class CloudCashRegisterSync
{
    public static async Task<int> SyncPendingAsync(
        HttpClient client,
        TiendaDbContext db,
        CancellationToken cancellationToken = default)
    {
        var pending = await db.CashRegisterSessions
            .Where(session => session.SyncPending && session.ClosedAtUtc != null)
            .OrderBy(session => session.ClosedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var session in pending)
        {
            if (session.ClosedBy is null || session.CountedCash is null
                || session.ExpectedCash is null || session.Variance is null)
            {
                throw new InvalidOperationException(
                    $"El cierre de caja {session.Id} no contiene el arqueo completo y no se puede sincronizar.");
            }

            var request = new CashRegisterSessionSyncRequest(
                session.Id,
                session.TerminalId,
                session.OpenedBy,
                new DateTimeOffset(session.OpenedAtUtc, TimeSpan.Zero),
                session.OpeningCash,
                new DateTimeOffset(session.ClosedAtUtc!.Value, TimeSpan.Zero),
                session.ClosedBy,
                session.CountedCash.Value,
                session.ExpectedCash.Value,
                session.Variance.Value,
                session.ClosingNote);
            using var response = await client.PostAsJsonAsync(
                "/api/sync/cash-register-sessions", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var details = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"No se sincronizó el cierre {session.Id} (HTTP {(int)response.StatusCode}): {details}");
            }

            session.SyncPending = false;
            await db.SaveChangesAsync(cancellationToken);
        }
        return pending.Count;
    }
}
