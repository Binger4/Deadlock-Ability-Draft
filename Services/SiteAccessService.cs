using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace abilitydraft.Services;

public sealed record SiteAccessStatus(bool IsClosed, bool HasDeveloperPassword);

public sealed class SiteAccessService
{
    public const string AccessClaimType = "abilitydraft_site_access";
    public const string AccessVersionClaimType = "abilitydraft_site_access_version";

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 120_000;

    private readonly object sync = new();
    private readonly ILogger<SiteAccessService> logger;
    private readonly string settingsPath;
    private PersistedSettings settings;

    public SiteAccessService(IWebHostEnvironment environment, ILogger<SiteAccessService> logger)
    {
        this.logger = logger;
        settingsPath = Path.Combine(environment.ContentRootPath, "Data", "site-access.json");
        settings = Load();
    }

    public event Action? Changed;

    public bool IsClosed
    {
        get
        {
            lock (sync)
            {
                return settings.IsClosed;
            }
        }
    }

    public SiteAccessStatus GetStatus()
    {
        lock (sync)
        {
            return new SiteAccessStatus(settings.IsClosed, HasPassword(settings));
        }
    }

    public bool CanBypass(ClaimsPrincipal user)
    {
        if (user.IsInRole("Admin"))
        {
            return true;
        }

        var accessVersion = user.FindFirstValue(AccessVersionClaimType);
        lock (sync)
        {
            return user.HasClaim(AccessClaimType, "true") &&
                   HasPassword(settings) &&
                   !string.IsNullOrWhiteSpace(accessVersion) &&
                   string.Equals(accessVersion, settings.AccessVersion, StringComparison.Ordinal);
        }
    }

    public bool VerifyDeveloperPassword(string password, out string accessVersion)
    {
        lock (sync)
        {
            accessVersion = settings.AccessVersion;
            if (!HasPassword(settings))
            {
                return false;
            }

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(settings.PasswordSalt);
                expected = Convert.FromBase64String(settings.PasswordHash);
            }
            catch (FormatException)
            {
                return false;
            }

            var actual = Derive(password, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }

    public SiteAccessStatus Save(bool isClosed, string? newPassword)
    {
        lock (sync)
        {
            var next = settings;
            var closingSiteAgain = !settings.IsClosed && isClosed;
            if (!string.IsNullOrEmpty(newPassword))
            {
                var salt = RandomNumberGenerator.GetBytes(SaltSize);
                next = next with
                {
                    PasswordSalt = Convert.ToBase64String(salt),
                    PasswordHash = Convert.ToBase64String(Derive(newPassword, salt)),
                    AccessVersion = Guid.NewGuid().ToString("N")
                };
            }

            // Every new closure is a fresh maintenance session. This forces
            // anyone who bypassed an earlier closure to enter the password again.
            if (closingSiteAgain && string.Equals(next.AccessVersion, settings.AccessVersion, StringComparison.Ordinal))
            {
                next = next with { AccessVersion = Guid.NewGuid().ToString("N") };
            }

            if (isClosed && !HasPassword(next))
            {
                throw new InvalidOperationException("Set a developer password before closing the site.");
            }

            next = next with { IsClosed = isClosed };
            Persist(next);
            settings = next;

            var status = new SiteAccessStatus(next.IsClosed, HasPassword(next));
            Changed?.Invoke();
            return status;
        }
    }

    private PersistedSettings Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new PersistedSettings();
            }

            var loaded = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(settingsPath));
            return loaded ?? new PersistedSettings();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load saved site access settings; using an open site.");
            return new PersistedSettings();
        }
    }

    private void Persist(PersistedSettings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporaryPath = settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, settingsPath, overwrite: true);
    }

    private static bool HasPassword(PersistedSettings value) =>
        !string.IsNullOrWhiteSpace(value.PasswordHash) &&
        !string.IsNullOrWhiteSpace(value.PasswordSalt) &&
        !string.IsNullOrWhiteSpace(value.AccessVersion);

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

    private sealed record PersistedSettings
    {
        public bool IsClosed { get; init; }
        public string PasswordHash { get; init; } = string.Empty;
        public string PasswordSalt { get; init; } = string.Empty;
        public string AccessVersion { get; init; } = string.Empty;
    }
}
