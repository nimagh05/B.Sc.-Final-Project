using System.Net.Mail;
using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Application.Options;
using DocSearch.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    IJwtTokenGenerator jwtTokenGenerator,
    ICurrentUser currentUser,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest? request, CancellationToken ct)
    {
        if (request == null)
        {
            throw new ValidationException("request", "Request body cannot be null.");
        }

        if (!authOptions.Value.AllowSelfRegistration)
        {
            throw new ForbiddenException("Self-registration is disabled.", "auth.forbidden");
        }

        var email = request.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        {
            throw new ValidationException("email", "A valid email address is required.");
        }

        var password = request.Password ?? string.Empty;
        ValidatePasswordPolicy(password);

        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser != null)
        {
            throw new ConflictException("A user with this email address already exists.", "auth.email_taken");
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            var errors = createResult.Errors
                .GroupBy(e => e.Code)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());

            throw new ValidationException("Registration failed due to password or user constraints.", errors);
        }

        await userManager.AddToRoleAsync(user, AdminBootstrapSeeder.UserRole);

        logger.LogInformation("New user registered: {Email}", email);

        return StatusCode(StatusCodes.Status201Created, new RegisterResponse(user.Id, user.Email!));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new UnauthorizedException("Invalid email or password.", "auth.invalid_credentials");
        }

        var email = request.Email.Trim();
        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
        {
            throw new UnauthorizedException("Invalid email or password.", "auth.invalid_credentials");
        }

        var passwordValid = await userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            throw new UnauthorizedException("Invalid email or password.", "auth.invalid_credentials");
        }

        var roles = await userManager.GetRolesAsync(user);
        var token = jwtTokenGenerator.GenerateToken(user.Id, user.Email ?? email, roles);

        return Ok(new LoginResponse(token.AccessToken, token.ExpiresAtUtc, token.Roles));
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(currentUser.UserId);
        if (user == null)
        {
            throw new NotFoundException("Current user account was not found.");
        }

        var roles = await userManager.GetRolesAsync(user);
        return Ok(new CurrentUserResponse(user.Id, user.Email ?? currentUser.Email, roles.ToList()));
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidatePasswordPolicy(string password)
    {
        var errors = new List<string>();

        if (password.Length < 10)
        {
            errors.Add("Password must be at least 10 characters long.");
        }

        if (!password.Any(char.IsUpper))
        {
            errors.Add("Password must contain at least one uppercase letter.");
        }

        if (!password.Any(char.IsLower))
        {
            errors.Add("Password must contain at least one lowercase letter.");
        }

        if (!password.Any(char.IsDigit))
        {
            errors.Add("Password must contain at least one digit.");
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["password"] = errors.ToArray()
            });
        }
    }
}
