namespace DocSearch.Application.Models;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);

public sealed record RegisterResponse(string Id, string Email);

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, IReadOnlyList<string> Roles);

public sealed record CurrentUserResponse(string Id, string Email, IReadOnlyList<string> Roles);
