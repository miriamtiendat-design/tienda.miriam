using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using TiendaMiriam.Api;
using TiendaMiriam.Domain;
using TiendaMiriam.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Cloud")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Cloud con la base PostgreSQL o SQLite.");
var usePostgres = !string.Equals(
    builder.Configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);
var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
    builder.Configuration["BusinessTimeZone"] ?? "America/La_Paz");
var signingKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Configure Jwt:SigningKey mediante un gestor de secretos.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey debe contener al menos 32 bytes.");
}

builder.Services.AddTiendaDatabase(connectionString, usePostgres);
builder.Services.AddHostedService<AuditRetentionBackgroundService>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IPasswordHasher<AppUser>>(
    new PasswordHasher<AppUser>(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 })));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var subject = context.Principal?.FindFirstValue("sub");
                var version = context.Principal?.FindFirstValue("ver");
                if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(version, out var authVersion))
                {
                    context.Fail("Invalid account token.");
                    return;
                }
                await using var scope = context.HttpContext.RequestServices.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TiendaDbContext>();
                var user = await db.Users.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == userId, context.HttpContext.RequestAborted);
                if (user is null || !user.IsActive || user.AuthVersion != authVersion
                    || context.Principal?.FindFirstValue("role") != user.Role.ToString())
                {
                    context.Fail("Account is inactive or its token has been revoked.");
                }
            }
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "TiendaMiriam",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "TiendaMiriam",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CashierOrAdmin", policy => policy.RequireRole("Cashier", "Administrator"));
    options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
    options.AddPolicy("KitchenOrAdmin", policy => policy.RequireRole("Kitchen", "Administrator"));
});

var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TiendaDbContext>();
    await db.Database.EnsureCreatedAsync();
    await CommerceSchemaBootstrapper.EnsureCommerceTablesAsync(db);
    await DemoCatalogSeeder.EnsureDemoClassificationCorrectedAsync(db);
    if (!await db.Users.AnyAsync())
    {
        var bootstrapPassword = builder.Configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(bootstrapPassword) || bootstrapPassword.Length < 14)
        {
            throw new InvalidOperationException(
                "La primera ejecución requiere Bootstrap:AdminPassword con al menos 14 caracteres. Guárdala como secreto y elimínala después de inicializar el dueño.");
        }
        var owner = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = AppRole.Administrator,
            AuthVersion = Guid.NewGuid(),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = string.Empty
        };
        owner.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>()
            .HashPassword(owner, bootstrapPassword);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
    }
    if (string.Equals(builder.Configuration["Demo:Seed"], "true", StringComparison.OrdinalIgnoreCase))
    {
        await DemoDataSeeder.EnsureSeededAsync(db);
        await DemoCatalogSeeder.EnsureSeededAsync(db);
        await DemoCatalogSeeder.EnsureLotsSeededAsync(db);
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/auth/login", async (
    LoginRequest? request,
    TiendaDbContext db,
    IPasswordHasher<AppUser> passwordHasher,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Username)
        || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "Usuario y contraseña son obligatorios." });
    }
    var username = request.Username.Trim().ToLowerInvariant();
    var user = await db.Users.SingleOrDefaultAsync(item => item.Username == username, cancellationToken);
    if (user is null || !user.IsActive
        || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password)
            == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }
    var now = DateTime.UtcNow;
    var expiresAt = now.AddHours(4);
    var issuer = configuration["Jwt:Issuer"] ?? "TiendaMiriam";
    var audience = configuration["Jwt:Audience"] ?? "TiendaMiriam";
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
        configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey no está configurada.")));
    var claims = new[]
    {
        new Claim("sub", user.Id.ToString()),
        new Claim("name", user.Username),
        new Claim("role", user.Role.ToString()),
        new Claim("ver", user.AuthVersion.ToString())
    };
    var signedToken = new JwtSecurityToken(
        issuer, audience, claims, now, expiresAt,
        new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new LoginResponse(
        new JwtSecurityTokenHandler().WriteToken(signedToken),
        new DateTimeOffset(expiresAt, TimeSpan.Zero)));
})
.AllowAnonymous()
.RequireRateLimiting("auth-login")
.WithSummary("Inicia una sesión segura por usuario y contraseña");

app.MapGet("/api/admin/users", async (
    TiendaDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.Users.AsNoTracking().OrderBy(user => user.Username)
        .Select(user => new UserResponse(
            user.Id, user.Username, user.Role, user.IsActive, new DateTimeOffset(user.CreatedAtUtc, TimeSpan.Zero)))
        .ToListAsync(cancellationToken)))
.RequireAuthorization("Administrator");

app.MapPost("/api/admin/users", async (
    CreateUserRequest request,
    TiendaDbContext db,
    IPasswordHasher<AppUser> passwordHasher,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Username)
        || !System.Text.RegularExpressions.Regex.IsMatch(request.Username, "^[a-zA-Z0-9._-]{3,64}$")
        || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length is < 14 or > 256
        || !Enum.IsDefined(request.Role))
    {
        return Results.BadRequest(new { error = "Usuario inválido, contraseña mínima de 14 caracteres o rol no permitido." });
    }
    var username = request.Username.Trim().ToLowerInvariant();
    if (await db.Users.AnyAsync(user => user.Username == username, cancellationToken))
    {
        return Results.Conflict(new { error = "El nombre de usuario ya está registrado." });
    }
    var user = new AppUser
    {
        Id = Guid.NewGuid(),
        Username = username,
        Role = request.Role,
        AuthVersion = Guid.NewGuid(),
        IsActive = true,
        CreatedAtUtc = DateTime.UtcNow,
        PasswordHash = string.Empty
    };
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    db.Users.Add(user);
    db.AuditEntries.Add(new AuditEntry
    {
        Id = Guid.NewGuid(),
        Actor = Actor(context),
        Action = "USER_CREATED",
        EntityType = "User",
        EntityId = user.Id,
        Reason = $"{user.Username}:{user.Role}",
        OccurredAtUtc = DateTime.UtcNow
    });
    try
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException exception)
    {
        return Results.Conflict(new { error = $"No se pudo registrar el usuario: {exception.GetBaseException().Message}" });
    }
    return Results.Created($"/api/admin/users/{user.Id}", new UserResponse(
        user.Id, user.Username, user.Role, user.IsActive, new DateTimeOffset(user.CreatedAtUtc, TimeSpan.Zero)));
})
.RequireAuthorization("Administrator");

app.MapPut("/api/admin/users/{id:guid}/password", async (
    Guid id,
    UpdateUserPasswordRequest request,
    TiendaDbContext db,
    IPasswordHasher<AppUser> passwordHasher,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length is < 14 or > 256)
    {
        return Results.BadRequest(new { error = "La contraseña debe contener entre 14 y 256 caracteres." });
    }
    var user = await db.Users.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (user is null)
    {
        return Results.NotFound();
    }
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    user.AuthVersion = Guid.NewGuid();
    db.AuditEntries.Add(new AuditEntry
    {
        Id = Guid.NewGuid(),
        Actor = Actor(context),
        Action = "USER_PASSWORD_RESET",
        EntityType = "User",
        EntityId = user.Id,
        Reason = "Restablecida por administrador",
        OccurredAtUtc = DateTime.UtcNow
    });
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.RequireAuthorization("Administrator");

app.MapPut("/api/admin/users/{id:guid}/status", async (
    Guid id,
    UpdateUserStatusRequest request,
    TiendaDbContext db,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (user is null)
    {
        return Results.NotFound();
    }
    if (!request.IsActive && user.IsActive && user.Role == AppRole.Administrator
        && await db.Users.CountAsync(item => item.Role == AppRole.Administrator && item.IsActive, cancellationToken) == 1)
    {
        return Results.Conflict(new { error = "Debe quedar al menos un administrador activo." });
    }
    user.IsActive = request.IsActive;
    user.AuthVersion = Guid.NewGuid();
    db.AuditEntries.Add(new AuditEntry
    {
        Id = Guid.NewGuid(),
        Actor = Actor(context),
        Action = request.IsActive ? "USER_ACTIVATED" : "USER_DEACTIVATED",
        EntityType = "User",
        EntityId = user.Id,
        Reason = user.Username,
        OccurredAtUtc = DateTime.UtcNow
    });
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.RequireAuthorization("Administrator");

app.MapGet("/api/catalog", async (TiendaDbContext db, CancellationToken cancellationToken) =>
{
    var productRows = await (
        from product in db.Products.AsNoTracking()
        join details in db.ProductDetails.AsNoTracking() on product.Id equals details.ProductId into productDetails
        from details in productDetails.DefaultIfEmpty()
        orderby product.Name
        select new CatalogProductResponse(
            product.Id, product.Sku, product.Name, product.Price, product.IsActive, product.StockItemId,
            product.IsRecipe, details == null ? ProductDepartment.Retail : details.Department,
            product.Barcode, details == null ? null : details.PrintStation))
        .ToListAsync(cancellationToken);
    var recipes = await db.RecipeComponents.AsNoTracking()
        .Select(component => new CatalogRecipeComponentResponse(
            component.ProductId, component.StockItemId, component.Quantity))
        .ToListAsync(cancellationToken);
    var stockItems = await db.StockItems.AsNoTracking()
        .Select(item => new CatalogStockItemResponse(
            item.Id, item.Name, item.Unit, item.OnHand, item.ReorderAt, item.UnitCost))
        .ToListAsync(cancellationToken);
    var lots = await db.StockLots.AsNoTracking()
        .Where(item => item.RemainingQuantity > 0)
        .Select(item => new CatalogStockLotResponse(
            item.Id, item.StockItemId, item.LotCode, item.ReceivedQuantity,
            item.RemainingQuantity, item.UnitCost,
            new DateTimeOffset(item.ReceivedAtUtc, TimeSpan.Zero),
            item.ExpiresAtUtc == null ? null : new DateTimeOffset(item.ExpiresAtUtc.Value, TimeSpan.Zero)))
        .ToListAsync(cancellationToken);
    var priceLists = await db.PriceLists.AsNoTracking()
        .Where(item => item.IsActive)
        .OrderBy(item => item.Name)
        .Select(item => new PriceListResponse(item.Id, item.Name, item.IsActive))
        .ToListAsync(cancellationToken);
    var listPrices = await db.ProductListPrices.AsNoTracking()
        .Where(entry => db.PriceLists.Any(list => list.Id == entry.PriceListId && list.IsActive))
        .Select(entry => new ProductListPriceResponse(entry.PriceListId, entry.ProductId, entry.Price))
        .ToListAsync(cancellationToken);
    var promotions = await db.Promotions.AsNoTracking()
        .OrderBy(item => item.StartsAtUtc)
        .Select(item => new PromotionResponse(
            item.Id, item.ProductId, item.Name, item.Kind, item.Value,
            new DateTimeOffset(item.StartsAtUtc, TimeSpan.Zero),
            new DateTimeOffset(item.EndsAtUtc, TimeSpan.Zero), item.IsActive))
        .ToListAsync(cancellationToken);
    var bundleRows = await db.PromotionBundles.AsNoTracking()
        .Include(bundle => bundle.Components)
        .OrderBy(bundle => bundle.Name)
        .ToListAsync(cancellationToken);
    var bundleProductIds = bundleRows.SelectMany(bundle => bundle.Components)
        .Select(component => component.ProductId).Distinct().ToArray();
    var bundleProducts = await db.Products.AsNoTracking()
        .Where(product => bundleProductIds.Contains(product.Id))
        .ToDictionaryAsync(product => product.Id, cancellationToken);
    var promotionBundles = bundleRows.Select(bundle => new PromotionBundleResponse(
            bundle.Id,
            bundle.Name,
            bundle.Price,
            bundle.StartsAtUtc == null ? null : new DateTimeOffset(bundle.StartsAtUtc.Value, TimeSpan.Zero),
            bundle.EndsAtUtc == null ? null : new DateTimeOffset(bundle.EndsAtUtc.Value, TimeSpan.Zero),
            bundle.IsActive,
            bundle.IsDeleted,
            new DateTimeOffset(bundle.CreatedAtUtc, TimeSpan.Zero),
            bundle.DeletedAtUtc == null ? null : new DateTimeOffset(bundle.DeletedAtUtc.Value, TimeSpan.Zero),
            bundle.Components.FirstOrDefault()?.ProductId ?? Guid.Empty,
            bundle.Components.Select(component => new PromotionBundleComponentResponse(
                component.ProductId,
                bundleProducts.GetValueOrDefault(component.ProductId)?.Name ?? "Producto",
                component.Quantity)).ToArray()))
        .ToArray();
    return Results.Ok(new CatalogSnapshotResponse(
        productRows, recipes, stockItems, lots, priceLists, listPrices, promotions, promotionBundles));
})
    .RequireAuthorization("CashierOrAdmin");

app.MapPost("/api/sync/sales", async (
    CreateSaleRequest? request,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "El cuerpo de la venta es obligatorio." });
        }
        return Results.Ok(await sales.CreateAsync(request, enqueueForSync: false, cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.RequireAuthorization("CashierOrAdmin");

app.MapPost("/api/sync/production", async (
    RecordProductionRequest? request,
    IProductionService production,
    ClaimsPrincipal principal,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { error = "El lote de producción es obligatorio." });
    }
    try
    {
        var actor = principal.FindFirstValue("name") ?? "cloud-sync";
        var result = await production.RecordProductionAsync(request, actor, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
    .RequireAuthorization("Administrator")
    .WithName("SyncProductionBatch")
    .WithSummary("Synchronize an offline production batch")
    .WithDescription("Idempotently applies a finished-goods production batch before dependent offline sales are synchronized.")
    .Produces<ProductionBatchResponse>()
    .Produces(StatusCodes.Status400BadRequest);

app.MapPost("/api/sync/cash-register-sessions", async (
    CashRegisterSessionSyncRequest? request,
    TiendaDbContext db,
    CancellationToken cancellationToken) =>
{
    if (request is null || request.Id == Guid.Empty
        || string.IsNullOrWhiteSpace(request.TerminalId) || request.TerminalId.Trim().Length > 80
        || string.IsNullOrWhiteSpace(request.OpenedBy) || request.OpenedBy.Trim().Length > 64
        || string.IsNullOrWhiteSpace(request.ClosedBy) || request.ClosedBy.Trim().Length > 64
        || request.OpenedAtUtc >= request.ClosedAtUtc
        || request.OpeningCash < 0 || request.CountedCash < 0
        || decimal.Round(request.OpeningCash, 2) != request.OpeningCash
        || decimal.Round(request.ExpectedCash, 2) != request.ExpectedCash
        || decimal.Round(request.CountedCash, 2) != request.CountedCash
        || decimal.Round(request.Variance, 2) != request.Variance
        || request.Variance != decimal.Round(
            request.CountedCash - request.ExpectedCash, 2, MidpointRounding.AwayFromZero)
        || request.ClosingNote?.Trim().Length > 300)
    {
        return Results.BadRequest(new { error = "Los datos del cierre de caja no son válidos." });
    }

    var existing = await db.CashRegisterSessions.SingleOrDefaultAsync(
        session => session.Id == request.Id, cancellationToken);
    if (existing is not null)
    {
        var matches = existing.TerminalId == request.TerminalId.Trim()
            && existing.OpenedBy == request.OpenedBy.Trim()
            && existing.OpenedAtUtc == request.OpenedAtUtc.UtcDateTime
            && existing.OpeningCash == request.OpeningCash
            && existing.ClosedAtUtc == request.ClosedAtUtc.UtcDateTime
            && existing.ClosedBy == request.ClosedBy.Trim()
            && existing.CountedCash == request.CountedCash
            && existing.ExpectedCash == request.ExpectedCash
            && existing.Variance == request.Variance
            && existing.ClosingNote == (string.IsNullOrWhiteSpace(request.ClosingNote)
                ? null
                : request.ClosingNote.Trim());
        return matches
            ? Results.NoContent()
            : Results.Conflict(new { error = "El identificador de cierre ya existe con otros datos." });
    }

    db.CashRegisterSessions.Add(new CashRegisterSession
    {
        Id = request.Id,
        TerminalId = request.TerminalId.Trim(),
        OpenedBy = request.OpenedBy.Trim(),
        OpenedAtUtc = request.OpenedAtUtc.UtcDateTime,
        OpeningCash = request.OpeningCash,
        ClosedAtUtc = request.ClosedAtUtc.UtcDateTime,
        ClosedBy = request.ClosedBy.Trim(),
        CountedCash = request.CountedCash,
        ExpectedCash = request.ExpectedCash,
        Variance = request.Variance,
        ClosingNote = string.IsNullOrWhiteSpace(request.ClosingNote) ? null : request.ClosingNote.Trim(),
        Version = 1,
        SyncPending = false
    });
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.RequireAuthorization("CashierOrAdmin")
.WithName("SyncCashRegisterSession")
.WithSummary("Sincroniza un cierre de caja terminado desde una terminal")
.WithDescription("La operación es idempotente por identificador de sesión. Sólo recibe cierres completos.")
.Produces(StatusCodes.Status204NoContent)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status409Conflict);

app.MapPost("/api/sync/sales/status", async (
    SyncSaleStatusRequest? request,
    TiendaDbContext db,
    CancellationToken cancellationToken) =>
{
    if (request?.SaleIds is null || request.SaleIds.Count is 0 or > 500
        || request.SaleIds.Any(id => id == Guid.Empty)
        || request.SaleIds.Distinct().Count() != request.SaleIds.Count)
    {
        return Results.BadRequest(new { error = "La lista de identificadores de venta no es válida." });
    }
    var ids = await db.Sales.AsNoTracking()
        .Where(sale => request.SaleIds.Contains(sale.Id))
        .Select(sale => sale.Id)
        .ToListAsync(cancellationToken);
    return Results.Ok(ids);
})
.RequireAuthorization("CashierOrAdmin");

app.MapPost("/api/sync/status", async (
    TerminalStatusUpdate? status,
    TiendaDbContext db,
    CancellationToken cancellationToken) =>
{
    if (status is null || string.IsNullOrWhiteSpace(status.TerminalId)
        || status.TerminalId.Length > 80 || status.PendingSales < 0)
    {
        return Results.BadRequest(new { error = "El estado de caja recibido no es válido." });
    }

    var terminal = await db.TerminalStatuses.SingleOrDefaultAsync(
        item => item.TerminalId == status.TerminalId,
        cancellationToken);
    if (terminal is null)
    {
        terminal = new TerminalStatus { TerminalId = status.TerminalId };
        db.TerminalStatuses.Add(terminal);
    }
    terminal.LastSeenAtUtc = DateTime.UtcNow;
    terminal.PendingSales = status.PendingSales;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.RequireAuthorization("CashierOrAdmin");

app.MapGet("/api/mobile/overview", async (TiendaDbContext db, CancellationToken cancellationToken) =>
{
    var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, businessTimeZone);
    var localMidnight = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
    var today = TimeZoneInfo.ConvertTimeToUtc(localMidnight, businessTimeZone);
    var tomorrow = TimeZoneInfo.ConvertTimeToUtc(localMidnight.AddDays(1), businessTimeZone);
    var terminalCutoff = DateTime.UtcNow.AddSeconds(-90);
    var todaySales = await db.Sales.AsNoTracking()
        .Where(sale => sale.OccurredAtUtc >= today && sale.OccurredAtUtc < tomorrow && sale.State == SaleState.Completed)
        .Select(sale => sale.Total)
        .ToListAsync(cancellationToken);
    var lowStock = await db.StockItems.AsNoTracking()
        .CountAsync(item => item.OnHand <= item.ReorderAt, cancellationToken);
    var terminals = await db.TerminalStatuses.AsNoTracking()
        .Where(item => item.LastSeenAtUtc >= terminalCutoff)
        .Select(item => item.PendingSales)
        .ToListAsync(cancellationToken);
    var overview = new MobileOverview(
        todaySales.Sum(),
        todaySales.Count,
        lowStock,
        terminals.Sum(),
        terminals.Count);
    return Results.Ok(overview);
})
.RequireAuthorization("Administrator");

app.MapGet("/api/admin/analytics", async (
    DateTimeOffset? from,
    DateTimeOffset? to,
    TiendaDbContext db,
    CancellationToken cancellationToken) =>
{
    if (from.HasValue != to.HasValue)
    {
        return Results.BadRequest(new { error = "Indica ambos límites from y to, o ninguno." });
    }

    var rangeEnd = to ?? DateTimeOffset.UtcNow;
    var rangeStart = from ?? rangeEnd.AddDays(-365);
    if (rangeStart >= rangeEnd || rangeEnd - rangeStart > TimeSpan.FromDays(366))
    {
        return Results.BadRequest(new { error = "El rango debe ser válido y no superar 366 días." });
    }

    var sales = await db.Sales.AsNoTracking()
        .Where(sale => sale.State == SaleState.Completed
            && sale.OccurredAtUtc >= rangeStart.UtcDateTime
            && sale.OccurredAtUtc < rangeEnd.UtcDateTime)
        .Select(sale => new
        {
            sale.OccurredAtUtc,
            sale.Total,
            Lines = sale.Lines.Select(line => new
            {
                line.ProductId,
                line.ProductName,
                line.Quantity,
                line.LineTotal
            }).ToList()
        })
        .ToListAsync(cancellationToken);

    var localStart = TimeZoneInfo.ConvertTime(rangeStart, businessTimeZone);
    var localEnd = TimeZoneInfo.ConvertTime(rangeEnd.AddTicks(-1), businessTimeZone);
    var localizedSales = sales
        .Select(sale => new
        {
            LocalDate = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(sale.OccurredAtUtc, DateTimeKind.Utc), businessTimeZone),
            sale.Total
        })
        .ToArray();
    var monthlyTotals = localizedSales
        .GroupBy(sale => sale.LocalDate.ToString("yyyy-MM"))
        .ToDictionary(group => group.Key, group => (Revenue: group.Sum(sale => sale.Total), Count: group.Count()));
    var monthlySales = new List<MonthlySalesPoint>();
    var month = new DateTime(localStart.Year, localStart.Month, 1);
    var finalMonth = new DateTime(localEnd.Year, localEnd.Month, 1);
    while (month <= finalMonth)
    {
        var key = month.ToString("yyyy-MM");
        var total = monthlyTotals.GetValueOrDefault(key);
        monthlySales.Add(new MonthlySalesPoint(key, total.Revenue, total.Count));
        month = month.AddMonths(1);
    }

    var hourlyTotals = localizedSales
        .GroupBy(sale => sale.LocalDate.Hour)
        .ToDictionary(group => group.Key, group => (Revenue: group.Sum(sale => sale.Total), Count: group.Count()));
    var hourlySales = Enumerable.Range(0, 24)
        .Select(hour =>
        {
            var total = hourlyTotals.GetValueOrDefault(hour);
            return new HourlySalesPoint(hour, total.Revenue, total.Count);
        })
        .ToArray();
    var topProducts = sales
        .SelectMany(sale => sale.Lines)
        .GroupBy(line => line.ProductId)
        .Select(group => new TopProductSalesPoint(
            group.First().ProductName,
            group.Sum(line => line.Quantity),
            group.Sum(line => line.LineTotal)))
        .OrderByDescending(item => item.Quantity)
        .ThenByDescending(item => item.Revenue)
        .Take(10)
        .ToArray();
    var nowUtc = DateTime.UtcNow;
    var currentLocalDate = TimeZoneInfo.ConvertTime(
        new DateTimeOffset(nowUtc, TimeSpan.Zero), businessTimeZone).Date;
    var weeklyStartLocal = DateTime.SpecifyKind(currentLocalDate.AddDays(-6), DateTimeKind.Unspecified);
    var weeklyStartUtc = TimeZoneInfo.ConvertTimeToUtc(weeklyStartLocal, businessTimeZone);
    var currentHourUtc = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
    var hourlyStartUtc = currentHourUtc.AddHours(-23);
    var supplementaryStartUtc = weeklyStartUtc < hourlyStartUtc ? weeklyStartUtc : hourlyStartUtc;
    var recentSales = await db.Sales.AsNoTracking()
        .Where(sale => sale.State == SaleState.Completed
            && sale.OccurredAtUtc >= supplementaryStartUtc
            && sale.OccurredAtUtc < nowUtc)
        .Select(sale => new { sale.OccurredAtUtc, sale.Total })
        .ToListAsync(cancellationToken);
    var recentSalesByDay = recentSales
        .Select(sale => new
        {
            LocalDate = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(sale.OccurredAtUtc, DateTimeKind.Utc), businessTimeZone).Date,
            sale.OccurredAtUtc,
            sale.Total
        })
        .ToArray();
    var weeklyTotals = recentSalesByDay
        .Where(sale => sale.LocalDate >= currentLocalDate.AddDays(-6))
        .GroupBy(sale => sale.LocalDate)
        .ToDictionary(group => group.Key, group => (Revenue: group.Sum(sale => sale.Total), Count: group.Count()));
    var weeklySales = Enumerable.Range(0, 7)
        .Select(offset =>
        {
            var date = currentLocalDate.AddDays(offset - 6);
            var total = weeklyTotals.GetValueOrDefault(date);
            return new DailySalesPoint(date.ToString("yyyy-MM-dd"), total.Revenue, total.Count);
        })
        .ToArray();
    var salesByUtcHour = recentSalesByDay
        .Where(sale => sale.OccurredAtUtc >= hourlyStartUtc)
        .GroupBy(sale => new DateTime(
            sale.OccurredAtUtc.Ticks - sale.OccurredAtUtc.Ticks % TimeSpan.TicksPerHour,
            DateTimeKind.Utc))
        .ToDictionary(group => group.Key, group => (Revenue: group.Sum(sale => sale.Total), Count: group.Count()));
    var recentHourlySales = Enumerable.Range(0, 24)
        .Select(offset =>
        {
            var hour = hourlyStartUtc.AddHours(offset);
            var total = salesByUtcHour.GetValueOrDefault(hour);
            return new RecentHourlySalesPoint(
                TimeZoneInfo.ConvertTime(new DateTimeOffset(hour, TimeSpan.Zero), businessTimeZone),
                total.Revenue,
                total.Count);
        })
        .ToArray();

    return Results.Ok(new SalesAnalyticsResponse(
        rangeStart,
        rangeEnd,
        monthlySales,
        topProducts,
        hourlySales,
        weeklySales,
        recentHourlySales));
})
.RequireAuthorization("Administrator")
.WithName("GetAdminSalesAnalytics")
.WithSummary("Consulta tendencias de ventas para el panel administrativo")
.WithDescription("Devuelve tendencias mensuales, semanales, por hora y de productos; la semana y las 24 horas recientes se incluyen siempre. Limita el rango solicitado a 366 días.")
.Produces<SalesAnalyticsResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/admin/stock", async (TiendaDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.StockItems.AsNoTracking()
        .OrderBy(item => item.Name)
        .Select(item => new { item.Id, item.Name, item.Unit, item.OnHand, item.UnitCost })
        .ToListAsync(cancellationToken)))
    .RequireAuthorization("Administrator");

app.MapGet("/api/admin/stock/lots", async (
    bool? includeDepleted, ICommerceService commerce, CancellationToken cancellationToken) =>
    Results.Ok(await commerce.GetStockLotsAsync(includeDepleted ?? false, cancellationToken)))
    .RequireAuthorization("Administrator")
    .WithSummary("Consulta lotes de inventario y fechas de caducidad");

app.MapPost("/api/admin/stock/counts", async (
    CreateStockCountRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await commerce.CreateStockCountAsync(request, Actor(context), cancellationToken);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Registra un recuento físico de inventario con auditoría");

app.MapGet("/api/payment-types", async (
    ICommerceService commerce, CancellationToken cancellationToken) =>
    Results.Ok(await commerce.GetCustomPaymentTypesAsync(cancellationToken)))
    .RequireAuthorization("CashierOrAdmin")
    .WithSummary("Lista los medios de pago personalizados activos");

app.MapPost("/api/admin/payment-types", async (
    CreateCustomPaymentTypeRequest request,
    ICommerceService commerce,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await commerce.CreateCustomPaymentTypeAsync(request, cancellationToken);
        return Results.Created("/api/payment-types", result);
    }
    catch (DbUpdateException exception)
    {
        return Results.Conflict(new { error = $"No se pudo crear el medio de pago: {exception.GetBaseException().Message}" });
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Crea un medio de pago personalizado");

app.MapGet("/api/admin/price-lists", async (
    TiendaDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.PriceLists.AsNoTracking()
        .OrderBy(item => item.Name)
        .Select(item => new PriceListResponse(item.Id, item.Name, item.IsActive))
        .ToListAsync(cancellationToken)))
    .RequireAuthorization("Administrator")
    .WithSummary("Lista tarifas preferenciales");

app.MapPost("/api/admin/price-lists", async (
    CreatePriceListRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await commerce.CreatePriceListAsync(request, Actor(context), cancellationToken);
        return Results.Created($"/api/admin/price-lists/{result.Id}", result);
    }
    catch (DbUpdateException exception)
    {
        return Results.Conflict(new { error = $"No se pudo crear la lista: {exception.GetBaseException().Message}" });
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
    .RequireAuthorization("Administrator")
    .WithSummary("Crea una tarifa preferencial en BOB");

app.MapPut("/api/admin/price-lists/{priceListId:guid}/products", async (
    Guid priceListId,
    SetProductListPriceRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.SetProductListPriceAsync(priceListId, request, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
    .RequireAuthorization("Administrator")
    .WithSummary("Establece el precio de un producto dentro de una lista");

app.MapGet("/api/admin/promotions", async (
    ICommerceService commerce, CancellationToken cancellationToken) =>
    Results.Ok(await commerce.GetPromotionsAsync(cancellationToken)))
    .RequireAuthorization("Administrator")
    .WithSummary("Consulta promociones configuradas");

app.MapPost("/api/admin/promotions", async (
    CreatePromotionRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await commerce.CreatePromotionAsync(request, Actor(context), cancellationToken);
        return Results.Created($"/api/admin/promotions/{result.Id}", result);
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (DbUpdateException exception)
    {
        return Results.Conflict(new { error = $"No se pudo crear la promoción: {exception.GetBaseException().Message}" });
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
    .RequireAuthorization("Administrator")
    .WithSummary("Programa una promoción temporal por producto");

app.MapGet("/api/admin/promotion-bundles", async (
    TiendaDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await new PromotionBundleService(db).GetAllAsync(cancellationToken)))
    .RequireAuthorization("Administrator")
    .WithSummary("Consulta combos promocionales, incluidos los eliminados");

app.MapPost("/api/admin/promotion-bundles", async (
    CreatePromotionBundleRequest request,
    TiendaDbContext db,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await new PromotionBundleService(db).UpsertAsync(
            request, Actor(context), enqueueForSync: false, cancellationToken);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (DbUpdateException exception)
    {
        return Results.Conflict(new { error = $"No se pudo guardar el combo: {exception.GetBaseException().Message}" });
    }
})
    .RequireAuthorization("Administrator")
    .WithSummary("Crea un combo promocional");

app.MapDelete("/api/admin/promotion-bundles/{bundleId:guid}", async (
    Guid bundleId,
    TiendaDbContext db,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await new PromotionBundleService(db).DeleteAsync(
            bundleId, Actor(context), enqueueForSync: false, cancellationToken);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
    .RequireAuthorization("Administrator")
    .WithSummary("Desactiva un combo sin borrar su historial");

app.MapGet("/api/admin/promotion-bundles/report", async (
    DateTimeOffset? from,
    DateTimeOffset? to,
    TiendaDbContext db,
    CancellationToken cancellationToken) =>
{
    if (from.HasValue != to.HasValue || from >= to)
    {
        return Results.BadRequest(new { error = "Indica un rango válido con ambos límites." });
    }
    return Results.Ok(await new PromotionBundleService(db).GetSalesReportAsync(from, to, cancellationToken));
})
    .RequireAuthorization("Administrator")
    .WithSummary("Consulta ventas, ingresos y descuentos históricos de combos");

app.MapPost("/api/sync/promotion-bundles", async (
    CreatePromotionBundleRequest request,
    TiendaDbContext db,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await new PromotionBundleService(db).UpsertAsync(
            request, Actor(context), enqueueForSync: false, cancellationToken));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
    .RequireAuthorization("Administrator")
    .WithSummary("Sincroniza cambios de combos mediante la outbox local");

app.MapGet("/api/products/{id:guid}/photo", async Task<Microsoft.AspNetCore.Http.IResult> (
    Guid id, TiendaDbContext db, CancellationToken cancellationToken) =>
{
    var photo = await db.ProductPhotos.AsNoTracking()
        .SingleOrDefaultAsync(item => item.ProductId == id, cancellationToken);
    return photo is null ? Results.NotFound() : Results.File(photo.Data, photo.ContentType);
})
.RequireAuthorization("CashierOrAdmin")
.WithSummary("Obtiene la foto de un producto");

app.MapPut("/api/admin/products/{id:guid}/price", async (
    Guid id,
    UpdateProductPriceRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.UpdateProductPriceAsync(id, request.Price, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Actualiza el precio y audita el cambio");

app.MapPost("/api/admin/products", async (
    CreateProductRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var product = await commerce.CreateProductAsync(request, Actor(context), cancellationToken);
        return Results.Created($"/api/admin/products/{product.Id}", new CatalogProductResponse(
            product.Id, product.Sku, product.Name, product.Price, product.IsActive,
            product.StockItemId, product.IsRecipe, request.Department, product.Barcode, request.PrintStation));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (DbUpdateException exception) { return Results.Conflict(new { error = $"No se pudo crear el producto: {exception.GetBaseException().Message}" }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Crea un artículo, servicio, recarga o producto elaborado");

app.MapPut("/api/admin/products/{id:guid}/recipe", async (
    Guid id,
    UpdateRecipeRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.UpdateRecipeAsync(id, request, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Reemplaza atómicamente las proporciones de una receta");

app.MapPost("/api/admin/stock", async (
    CreateStockItemRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        var item = await commerce.CreateStockItemAsync(request, Actor(context), cancellationToken);
        return Results.Created($"/api/admin/stock/{item.Id}", new
        {
            item.Id, item.Name, item.Unit, item.OnHand, item.ReorderAt, item.UnitCost
        });
    }
    catch (DbUpdateException exception) { return Results.Conflict(new { error = $"No se pudo crear el insumo: {exception.GetBaseException().Message}" }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator");

app.MapPost("/api/admin/stock/{id:guid}/adjust", async (
    Guid id, StockAdjustmentRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.AdjustStockAsync(id, request.Delta, request.Reason, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator");

app.MapPut("/api/admin/products/{id:guid}/photo", async (
    Guid id,
    ProductPhotoUpload upload,
    ICommerceService commerce,
    CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.SetProductPhotoAsync(id, upload, cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Guarda una foto de producto");

app.MapDelete("/api/admin/products/{id:guid}/photo", async (
    Guid id, ICommerceService commerce, CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.DeleteProductPhotoAsync(id, cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator");

app.MapPost("/api/admin/sales/{id:guid}/void", async (
    Guid id,
    VoidSaleRequest request,
    ICommerceService commerce,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.VoidSaleAsync(id, request.Reason, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Anula una venta no iniciada por cocina y registra auditoría");

app.MapPost("/api/customers", async (
    CreateCustomerRequest request, ICommerceService commerce, CancellationToken cancellationToken) =>
{
    try
    {
        var customer = await commerce.CreateCustomerAsync(request, cancellationToken);
        return Results.Created($"/api/customers/{customer.Id}", customer);
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Crea un cliente frecuente");

app.MapGet("/api/customers", async (TiendaDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.Customers.AsNoTracking().Where(item => item.IsActive)
        .OrderBy(item => item.Name)
        .Select(item => new CustomerResponse(
            item.Id, item.Name, item.Phone, item.WalletBalance, item.CreditBalance,
            item.CreditLimit, item.Address, item.TaxId, item.Notes))
        .ToListAsync(cancellationToken)))
.RequireAuthorization("CashierOrAdmin");

app.MapGet("/api/customers/{id:guid}/ledger", async (
    Guid id, TiendaDbContext db, CancellationToken cancellationToken) =>
{
    if (!await db.Customers.AnyAsync(item => item.Id == id, cancellationToken))
    {
        return Results.NotFound();
    }
    return Results.Ok(await db.CustomerLedger.AsNoTracking()
        .Where(entry => entry.CustomerId == id)
        .OrderByDescending(entry => entry.OccurredAtUtc)
        .Take(500)
        .Select(entry => new
        {
            entry.Id,
            entry.SaleId,
            entry.Kind,
            entry.WalletDelta,
            entry.CreditDelta,
            entry.PaymentMethod,
            entry.OccurredAtUtc,
            entry.Actor,
            entry.Description
        })
        .ToListAsync(cancellationToken));
})
.RequireAuthorization("CashierOrAdmin");

app.MapPut("/api/customers/{id:guid}", async (
    Guid id, CreateCustomerRequest request, ICommerceService commerce, HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await commerce.UpdateCustomerAsync(
            id, request, Actor(context), cancellationToken));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Actualiza la ficha de un cliente");

app.MapPost("/api/sync/customer-changes", async (
    CustomerSyncChange? change,
    TiendaDbContext db,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (change is null || change.EventId == Guid.Empty || change.CustomerId == Guid.Empty)
    {
        return Results.BadRequest(new { error = "El cambio de cliente está incompleto." });
    }

    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    if (change.Kind == CustomerSyncChangeKind.ProfileUpsert && change.Profile is { } profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 160
            || profile.CreditLimit < 0 || decimal.Round(profile.CreditLimit, 2) != profile.CreditLimit
            || profile.Phone?.Trim().Length > 32 || profile.Address?.Trim().Length > 240
            || profile.TaxId?.Trim().Length > 40 || profile.Notes?.Trim().Length > 500)
        {
            return Results.BadRequest(new { error = "La ficha del cliente contiene datos inválidos." });
        }

        var customer = await db.Customers.SingleOrDefaultAsync(
            item => item.Id == change.CustomerId, cancellationToken);
        if (customer is null)
        {
            customer = new CustomerAccount
            {
                Id = change.CustomerId,
                Name = profile.Name.Trim(),
                Phone = profile.Phone?.Trim(),
                Address = profile.Address?.Trim(),
                TaxId = profile.TaxId?.Trim(),
                Notes = profile.Notes?.Trim(),
                CreditLimit = profile.CreditLimit
            };
            db.Customers.Add(customer);
        }
        else
        {
            customer.Name = profile.Name.Trim();
            customer.Phone = profile.Phone?.Trim();
            customer.Address = profile.Address?.Trim();
            customer.TaxId = profile.TaxId?.Trim();
            customer.Notes = profile.Notes?.Trim();
            customer.CreditLimit = profile.CreditLimit;
            customer.IsActive = true;
        }
    }
    else if (change.Kind == CustomerSyncChangeKind.CreditPayment && change.CreditPayment is { } payment)
    {
        if (payment.LedgerEntryId == Guid.Empty || payment.Amount <= 0
            || decimal.Round(payment.Amount, 2) != payment.Amount
            || string.IsNullOrWhiteSpace(payment.Description) || payment.Description.Trim().Length > 300
            || string.IsNullOrWhiteSpace(payment.Actor) || payment.Actor.Trim().Length > 160
            || payment.PaymentMethod is not (PaymentMethod.Cash or PaymentMethod.Transfer))
        {
            return Results.BadRequest(new { error = "El abono sincronizado contiene datos inválidos." });
        }

        var existing = await db.CustomerLedger.SingleOrDefaultAsync(
            entry => entry.Id == payment.LedgerEntryId, cancellationToken);
        if (existing is not null)
        {
            if (existing.CustomerId != change.CustomerId
                || existing.Kind != CustomerLedgerKind.CreditPayment
                || existing.CreditDelta != -payment.Amount
                || existing.PaymentMethod != payment.PaymentMethod
                || existing.CashRegisterSessionId != payment.CashRegisterSessionId)
            {
                return Results.Conflict(new { error = "El identificador del abono ya existe con otros datos." });
            }
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok();
        }

        var customer = await db.Customers.SingleOrDefaultAsync(
            item => item.Id == change.CustomerId && item.IsActive, cancellationToken);
        if (customer is null)
        {
            return Results.Conflict(new { error = "Primero debe sincronizarse la ficha del cliente." });
        }
        if (customer.CreditBalance < payment.Amount)
        {
            return Results.Conflict(new { error = "El saldo remoto no alcanza para aplicar este abono." });
        }

        customer.CreditBalance -= payment.Amount;
        db.CustomerLedger.Add(new CustomerLedgerEntry
        {
            Id = payment.LedgerEntryId,
            CustomerId = customer.Id,
            Kind = CustomerLedgerKind.CreditPayment,
            CreditDelta = -payment.Amount,
            PaymentMethod = payment.PaymentMethod,
            CashRegisterSessionId = payment.CashRegisterSessionId,
            OccurredAtUtc = payment.OccurredAt.UtcDateTime,
            Actor = payment.Actor.Trim(),
            Description = payment.Description.Trim()
        });
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            Actor = Actor(context),
            Action = "CUSTOMER_CREDIT_PAYMENT_SYNCED",
            EntityType = nameof(CustomerLedgerEntry),
            EntityId = payment.LedgerEntryId,
            Reason = $"Abono sincronizado de {payment.Amount:0.00} BOB",
            OccurredAtUtc = DateTime.UtcNow
        });
    }
    else
    {
        return Results.BadRequest(new { error = "El cambio de cliente no coincide con su tipo." });
    }

    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok();
})
.RequireAuthorization("CashierOrAdmin")
.WithSummary("Aplica cambios de clientes pendientes desde una caja offline");

app.MapPost("/api/customers/{id:guid}/wallet", async (
    Guid id, CustomerMoneyRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await commerce.AddWalletFundsAsync(
            id, request.Amount, request.Description, Actor(context), request.PaymentMethod, cancellationToken));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("CashierOrAdmin");

app.MapPost("/api/customers/{id:guid}/credit-payment", async (
    Guid id, CustomerMoneyRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await commerce.RecordCreditPaymentAsync(
            id, request.Amount, request.Description, Actor(context), request.PaymentMethod, cancellationToken,
            request.CashRegisterSessionId));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("CashierOrAdmin");

app.MapPost("/api/admin/expenses", async (
    CreateExpenseRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.RecordExpenseAsync(request, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator");

app.MapPost("/api/admin/suppliers", async (
    CreateSupplierRequest request, ICommerceService commerce, CancellationToken cancellationToken) =>
{
    try
    {
        var supplier = await commerce.CreateSupplierAsync(request, cancellationToken);
        return Results.Created($"/api/admin/suppliers/{supplier.Id}", supplier);
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator");

app.MapPut("/api/admin/suppliers/{id:guid}", async (
    Guid id, CreateSupplierRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await commerce.UpdateSupplierAsync(id, request, Actor(context), cancellationToken));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithName("UpdateSupplier")
.WithSummary("Actualiza los datos de contacto de un proveedor")
.WithDescription("Requiere el rol Administrador. Devuelve 404 si el proveedor no existe o está inactivo.")
.Produces<SupplierResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/api/admin/suppliers", async (TiendaDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.Suppliers.AsNoTracking().Where(item => item.IsActive)
        .OrderBy(item => item.Name)
        .Select(item => new SupplierResponse(item.Id, item.Name, item.TaxId, item.Phone, item.Address, item.ContactName))
        .ToListAsync(cancellationToken)))
.RequireAuthorization("Administrator");

app.MapPost("/api/admin/purchases", async (
    ReceivePurchaseRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        var purchase = await commerce.ReceivePurchaseAsync(request, Actor(context), cancellationToken);
        return Results.Created($"/api/admin/purchases/{purchase.Id}", purchase);
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Registra recepción, precio histórico y aumento de stock");

app.MapGet("/api/admin/reports/summary", async (
    DateTimeOffset from,
    DateTimeOffset to,
    ICommerceService commerce,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await commerce.GetReportAsync(from, to, cancellationToken));
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.RequireAuthorization("Administrator")
.WithSummary("Resume ventas, costo de insumos, gastos y utilidad del intervalo");

app.MapGet("/api/admin/cash-register/closures", async (
    DateTimeOffset from,
    DateTimeOffset to,
    string? cashier,
    string? terminal,
    TiendaDbContext db,
    CancellationToken cancellationToken) =>
{
    if (from >= to || cashier?.Trim().Length > 64 || terminal?.Trim().Length > 80)
    {
        return Results.BadRequest(new { error = "El intervalo o los filtros de cierre no son válidos." });
    }

    var query = db.CashRegisterSessions.AsNoTracking()
        .Where(session => session.ClosedAtUtc != null
            && session.ClosedAtUtc >= from.UtcDateTime
            && session.ClosedAtUtc <= to.UtcDateTime);
    if (!string.IsNullOrWhiteSpace(cashier))
    {
        var cashierFilter = cashier.Trim().ToLowerInvariant();
        query = query.Where(session => session.ClosedBy != null
            && session.ClosedBy.ToLower().Contains(cashierFilter));
    }
    if (!string.IsNullOrWhiteSpace(terminal))
    {
        var terminalFilter = terminal.Trim().ToLowerInvariant();
        query = query.Where(session => session.TerminalId.ToLower().Contains(terminalFilter));
    }

    var rows = await query
        .OrderByDescending(session => session.ClosedAtUtc)
        .Take(1001)
        .Select(session => new CashRegisterClosureResponse(
            session.Id,
            session.TerminalId,
            session.OpenedBy,
            new DateTimeOffset(session.OpenedAtUtc, TimeSpan.Zero),
            session.OpeningCash,
            new DateTimeOffset(session.ClosedAtUtc!.Value, TimeSpan.Zero),
            session.ClosedBy!,
            session.CountedCash!.Value,
            session.ExpectedCash!.Value,
            session.Variance!.Value,
            session.ClosingNote))
        .ToListAsync(cancellationToken);
    var hasMore = rows.Count > 1000;
    if (hasMore)
    {
        rows.RemoveAt(rows.Count - 1);
    }
    return Results.Ok(new CashRegisterClosureListResponse(rows, hasMore));
})
.RequireAuthorization("Administrator")
.WithName("GetConsolidatedCashRegisterClosures")
.WithSummary("Consulta los cierres consolidados de todas las terminales")
.WithDescription("Filtra desde la base de datos cloud por fecha de cierre, cajero y terminal.")
.Produces<CashRegisterClosureListResponse>()
.Produces(StatusCodes.Status400BadRequest);

app.MapGet("/api/admin/audit", async (
    DateTimeOffset from, DateTimeOffset to, TiendaDbContext db, CancellationToken cancellationToken) =>
{
    if (from >= to || to - from > TimeSpan.FromDays(366))
    {
        return Results.BadRequest(new { error = "El rango debe ser creciente y no superar 366 días." });
    }
    return Results.Ok(await db.AuditEntries.AsNoTracking()
        .Where(entry => entry.OccurredAtUtc >= from.UtcDateTime && entry.OccurredAtUtc < to.UtcDateTime)
        .OrderByDescending(entry => entry.OccurredAtUtc)
        .Take(10_000)
        .Select(entry => new
        {
            entry.Id, entry.Actor, entry.Action, entry.EntityType, entry.EntityId, entry.Reason,
            entry.PreviousValue, entry.NewValue, entry.Observation, entry.TerminalId, entry.OccurredAtUtc
        })
        .ToListAsync(cancellationToken));
})
.RequireAuthorization("Administrator");

app.MapPut("/api/admin/audit/retention", async (
    UpdateAuditRetentionRequest? request, TiendaDbContext db, CancellationToken cancellationToken) =>
{
    if (request is null || request.RetentionDays is < 30 or > 3650)
    {
        return Results.BadRequest(new { error = "La retención debe estar entre 30 y 3650 días." });
    }
    if (await AuditRetentionService.GetRetentionDaysAsync(db, cancellationToken) != request.RetentionDays)
    {
        await AuditRetentionService.SaveRetentionDaysAsync(db, request.RetentionDays, cancellationToken);
        var configAuditIds = await db.AuditEntries
            .Where(entry => entry.EntityType == nameof(BusinessAsset)
                && entry.Action == "BUSINESS_SETTINGS_CHANGED"
                && entry.Reason == "audit-retention-days")
            .Select(entry => entry.Id)
            .ToArrayAsync(cancellationToken);
        var generatedOutbox = await db.AuditOutbox
            .Where(item => configAuditIds.Contains(item.AuditEntryId) && !item.IsDelivered)
            .ToListAsync(cancellationToken);
        foreach (var item in generatedOutbox)
        {
            item.IsDelivered = true;
        }
        if (generatedOutbox.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
    return Results.Ok(new { retentionDays = request.RetentionDays });
})
.RequireAuthorization("Administrator");

app.MapPost("/api/sync/audit", async (
    IReadOnlyList<AuditEntry>? entries, TiendaDbContext db, CancellationToken cancellationToken) =>
{
    if (entries is null || entries.Count is 0 or > 100)
    {
        return Results.BadRequest(new { error = "El lote de auditoría debe tener entre 1 y 100 registros." });
    }
    foreach (var incoming in entries)
    {
        if (incoming is null || incoming.Id == Guid.Empty || string.IsNullOrWhiteSpace(incoming.Actor)
            || string.IsNullOrWhiteSpace(incoming.Action) || incoming.Action.Length > 80
            || string.IsNullOrWhiteSpace(incoming.EntityType) || incoming.EntityType.Length > 80
            || string.IsNullOrWhiteSpace(incoming.Reason) || incoming.Reason.Length > 500
            || incoming.TerminalId?.Length > 80
            || incoming.PreviousValue?.Length > 4000 || incoming.NewValue?.Length > 4000
            || incoming.Observation?.Length > 500)
        {
            return Results.BadRequest(new { error = "Un registro de auditoría contiene campos inválidos." });
        }
        if (!await db.AuditEntries.AnyAsync(item => item.Id == incoming.Id, cancellationToken))
        {
            db.AuditEntries.Add(incoming);
        }
    }
    await db.SaveChangesAsync(cancellationToken);
    var deliveredIds = entries.Select(entry => entry.Id).ToArray();
    var generatedOutbox = await db.AuditOutbox
        .Where(item => deliveredIds.Contains(item.AuditEntryId) && !item.IsDelivered)
        .ToListAsync(cancellationToken);
    foreach (var item in generatedOutbox)
    {
        item.IsDelivered = true;
    }
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { accepted = entries.Count });
})
.RequireAuthorization("CashierOrAdmin");

app.MapGet("/api/held-sales", async (
    string terminalId, ICommerceService commerce, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(terminalId) || terminalId.Length > 80)
    {
        return Results.BadRequest(new { error = "Identificador de caja inválido." });
    }
    return Results.Ok(await commerce.GetHeldSalesAsync(terminalId, cancellationToken));
})
.RequireAuthorization("CashierOrAdmin");

app.MapPut("/api/held-sales/{id:guid}", async (
    Guid id, HoldSaleRequest request, ICommerceService commerce, CancellationToken cancellationToken) =>
{
    if (request.Id != id)
    {
        return Results.BadRequest(new { error = "El identificador de la ruta no coincide con el cuerpo." });
    }
    try
    {
        var saved = await commerce.SaveHeldSaleAsync(request, cancellationToken);
        return Results.Ok(saved);
    }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    catch (System.Text.Json.JsonException exception) { return Results.BadRequest(new { error = $"JSON de venta en espera inválido: {exception.Message}" }); }
})
.RequireAuthorization("CashierOrAdmin");

app.MapDelete("/api/held-sales/{id:guid}", async (
    Guid id, string terminalId, ICommerceService commerce, CancellationToken cancellationToken,
    int? expectedVersion = null) =>
{
    try
    {
        await commerce.DeleteHeldSaleAsync(id, terminalId, cancellationToken, expectedVersion);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
})
.RequireAuthorization("CashierOrAdmin");

app.MapGet("/api/kitchen/tickets", async (
    ICommerceService commerce, CancellationToken cancellationToken) =>
    Results.Ok(await commerce.GetKitchenQueueAsync(cancellationToken)))
.RequireAuthorization("KitchenOrAdmin");

app.MapPatch("/api/kitchen/tickets/{id:guid}", async (
    Guid id, UpdateKitchenTicketRequest request, ICommerceService commerce, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        await commerce.UpdateKitchenTicketAsync(id, request.State, Actor(context), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
})
.RequireAuthorization("KitchenOrAdmin");

app.MapGet("/api/admin/purchase-history", async (
    Guid stockItemId, DateTimeOffset from, DateTimeOffset to, TiendaDbContext db, CancellationToken cancellationToken) =>
{
    if (from >= to || to - from > TimeSpan.FromDays(366) || stockItemId == Guid.Empty)
    {
        return Results.BadRequest(new { error = "Insumo o intervalo inválido." });
    }
    return Results.Ok(await db.PurchaseLines.AsNoTracking()
        .Where(line => line.StockItemId == stockItemId
            && line.PurchaseOrder.ReceivedAtUtc >= from.UtcDateTime
            && line.PurchaseOrder.ReceivedAtUtc < to.UtcDateTime)
        .OrderByDescending(line => line.PurchaseOrder.ReceivedAtUtc)
        .Select(line => new
        {
            line.PurchaseOrder.ReceivedAtUtc,
            line.PurchaseOrder.InvoiceReference,
            Supplier = line.PurchaseOrder.SupplierId,
            line.Quantity,
            line.UnitCost,
            line.LineTotal
        })
        .ToListAsync(cancellationToken));
})
.RequireAuthorization("Administrator");

app.Run();

static string Actor(HttpContext context) =>
    context.User.Identity?.Name
    ?? context.User.FindFirst("sub")?.Value
    ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
    ?? "unknown";

public partial class Program { }
