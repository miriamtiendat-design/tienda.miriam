using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TiendaMiriam.Infrastructure;

public static class DatabaseRegistration
{
    public static IServiceCollection AddTiendaDatabase(
        this IServiceCollection services,
        string connectionString,
        bool usePostgres)
    {
        services.AddDbContext<TiendaDbContext>(options =>
        {
            if (usePostgres)
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }
        });
        services.AddScoped<ISaleService, SaleService>();
        services.AddScoped<ICommerceService, CommerceService>();
        services.AddScoped<IProductionService, ProductionService>();
        return services;
    }
}
