using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DocSearch.Infrastructure.Identity;

public sealed class JwtTokenGenerator(
    IOptions<JwtOptions> jwtOptions,
    TimeProvider? timeProvider = null) : IJwtTokenGenerator
{
    private readonly JwtOptions _jwtOptions = jwtOptions?.Value ?? throw new ArgumentNullException(nameof(jwtOptions));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public GeneratedToken GenerateToken(string userId, string email, IEnumerable<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(roles);

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(_jwtOptions.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var roleList = roles.ToList();
        foreach (var role in roleList)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var tokenDescriptor = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var tokenHandler = new JwtSecurityTokenHandler();
        var tokenString = tokenHandler.WriteToken(tokenDescriptor);

        return new GeneratedToken(tokenString, expiresAt, roleList);
    }
}
