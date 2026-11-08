using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DocSearch.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace DocSearch.Infrastructure.Identity;

public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string UserId =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? httpContextAccessor.HttpContext?.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? httpContextAccessor.HttpContext?.User.FindFirstValue("sub")
        ?? string.Empty;

    public string Email =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email)
        ?? httpContextAccessor.HttpContext?.User.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? httpContextAccessor.HttpContext?.User.FindFirstValue("email")
        ?? string.Empty;

    public bool IsAdmin
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user == null)
            {
                return false;
            }

            return user.IsInRole("Admin")
                || user.FindAll(ClaimTypes.Role).Any(c => string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase))
                || user.FindAll("role").Any(c => string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase));
        }
    }
}
