using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public sealed class LocalAuthenticationService(TiendaDbContext db)
{
    private const int Iterations = 600_000;
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    public async Task EnsureInitialAdministratorAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        ValidateCredentials(username, password);
        var normalizedUsername = NormalizeUsername(username);
        db.Users.Add(new AppUser
        {
            Id = Guid.NewGuid(),
            Username = normalizedUsername,
            PasswordHash = HashPassword(password),
            Role = AppRole.Administrator,
            AuthVersion = Guid.NewGuid(),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LocalUserIdentity?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var normalizedUsername = NormalizeUsername(username);
        var user = await db.Users.SingleOrDefaultAsync(
            item => item.Username == normalizedUsername, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (user.LockedUntilUtc is DateTime lockedUntil && lockedUntil > now)
        {
            return null;
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaximumAttempts)
            {
                user.FailedLoginAttempts = 0;
                user.LockedUntilUtc = now.Add(LockoutDuration);
            }
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        return new LocalUserIdentity(user.Id, user.Username, user.Role);
    }

    public async Task<IReadOnlyList<LocalUserIdentity>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new LocalUserIdentity(user.Id, user.Username, user.Role, user.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<LocalUserIdentity> CreateUserAsync(
        string username,
        string password,
        AppRole role,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(username, password);
        if (!Enum.IsDefined(role))
        {
            throw new InvalidOperationException("El rol seleccionado no es válido.");
        }

        var normalizedUsername = NormalizeUsername(username);
        if (await db.Users.AnyAsync(user => user.Username == normalizedUsername, cancellationToken))
        {
            throw new InvalidOperationException("Ese nombre de usuario ya existe.");
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = normalizedUsername,
            PasswordHash = HashPassword(password),
            Role = role,
            AuthVersion = Guid.NewGuid(),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return new LocalUserIdentity(user.Id, user.Username, user.Role);
    }

    public async Task SetUserActiveAsync(
        Guid userId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new KeyNotFoundException("El usuario local no existe.");
        if (!isActive && user.IsActive && user.Role == AppRole.Administrator
            && await db.Users.CountAsync(
                item => item.IsActive && item.Role == AppRole.Administrator && item.Id != userId,
                cancellationToken) == 0)
        {
            throw new InvalidOperationException("No se puede desactivar el último administrador local.");
        }

        user.IsActive = isActive;
        user.AuthVersion = Guid.NewGuid();
        user.FailedLoginAttempts = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10 || password.Length > 256)
        {
            throw new InvalidOperationException("La contraseña debe tener entre 10 y 256 caracteres.");
        }

        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new KeyNotFoundException("El usuario local no existe.");
        user.PasswordHash = HashPassword(password);
        user.AuthVersion = Guid.NewGuid();
        user.FailedLoginAttempts = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length is < 3 or > 64
            || username.Any(char.IsControl))
        {
            throw new InvalidOperationException("El usuario debe tener entre 3 y 64 caracteres.");
        }
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10 || password.Length > 256)
        {
            throw new InvalidOperationException("La contraseña debe tener entre 10 y 256 caracteres.");
        }
    }

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derivedKey)}";
    }

    private static bool VerifyPassword(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256"
            || !int.TryParse(parts[1], out var iterations)
            || iterations is < 100_000 or > 2_000_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record LocalUserIdentity(Guid Id, string Username, AppRole Role, bool IsActive = true);
