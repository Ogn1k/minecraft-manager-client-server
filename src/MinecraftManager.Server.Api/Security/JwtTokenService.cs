using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.Api.Security;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 10;
}

public sealed class JwtTokenService(IOptions<AuthenticationOptions> options, ISystemClock clock) : ITokenService
{
    private readonly AuthenticationOptions settings = options.Value;
    public (string Token, DateTimeOffset ExpiresAtUtc) CreateDeviceAccessToken(Machine machine) => Create([
        new Claim(JwtRegisteredClaimNames.Sub, machine.Id.ToString()), new Claim("principal_type", "device"), new Claim("machine_id", machine.Id.ToString()), new Claim("authorization_version", machine.AuthorizationVersion.ToString())]);
    public (string Token, DateTimeOffset ExpiresAtUtc) CreateAdminAccessToken(Guid userId, string userName, IEnumerable<string> roles) => Create([
        new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, userName), new Claim("principal_type", "admin"), .. roles.Select(x => new Claim(ClaimTypes.Role, x))]);
    private (string, DateTimeOffset) Create(IEnumerable<Claim> claims)
    {
        var expires = clock.UtcNow.AddMinutes(settings.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor { Subject = new ClaimsIdentity(claims), Issuer = settings.Issuer, Audience = settings.Audience, Expires = expires.UtcDateTime, IssuedAt = clock.UtcNow.UtcDateTime, SigningCredentials = new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256), Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString() } };
        return (new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor)), expires);
    }
}
