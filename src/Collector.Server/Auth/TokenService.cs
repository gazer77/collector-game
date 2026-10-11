using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Collector.Server.Auth;

public sealed class TokenService(SigningKeyStore keys, IOptions<CollectorOptions> options, TimeProvider time)
{
    public const string Issuer = "collector";
    public const string Audience = "collector";

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid playerId)
    {
        var expires = time.GetUtcNow().AddDays(options.Value.TokenLifetimeDays);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, playerId.ToString())]),
            IssuedAt = time.GetUtcNow().UtcDateTime,
            NotBefore = time.GetUtcNow().UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    public static Guid? PlayerId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;
}

/// <summary>
/// The token signing key. Taken from configuration ("Collector:SigningKey", base64) if set, otherwise generated
/// once and kept in the data directory so tokens survive restarts.
/// </summary>
public sealed class SigningKeyStore
{
    public SigningKeyStore(IConfiguration configuration, IOptions<CollectorOptions> options, IHostEnvironment env)
    {
        var configured = configuration[$"{CollectorOptions.Section}:SigningKey"];
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            bytes = Convert.FromBase64String(configured);
        }
        else
        {
            var path = Path.Combine(DataPaths.Resolve(options.Value, env), "signing.key");
            if (File.Exists(path))
            {
                bytes = Convert.FromBase64String(File.ReadAllText(path));
            }
            else
            {
                bytes = RandomNumberGenerator.GetBytes(64);
                File.WriteAllText(path, Convert.ToBase64String(bytes));
            }
        }
        if (bytes.Length < 32)
            throw new InvalidOperationException("The signing key must be at least 32 bytes.");
        Key = new SymmetricSecurityKey(bytes);
    }

    public SymmetricSecurityKey Key { get; }
}

public static class DataPaths
{
    /// <summary>The data directory as an absolute path, created if needed.</summary>
    public static string Resolve(CollectorOptions options, IHostEnvironment env)
    {
        var path = Path.IsPathRooted(options.DataDirectory)
            ? options.DataDirectory
            : Path.Combine(env.ContentRootPath, options.DataDirectory);
        Directory.CreateDirectory(path);
        return path;
    }
}
