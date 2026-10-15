using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Infrastructure;

namespace TiendaMiriam.Api;

internal sealed class AuditRetentionBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditRetentionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TiendaDbContext>();
                var deleted = await AuditRetentionService.PruneExpiredAsync(db, stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Audit retention removed {EntryCount} expired cloud entries.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (DbUpdateException exception)
            {
                logger.LogError(exception, "Cloud audit retention failed while updating the database.");
            }
            catch (DbException exception)
            {
                logger.LogError(exception, "Cloud audit retention could not connect to the database.");
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError(exception, "Cloud audit retention could not access the database service.");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
