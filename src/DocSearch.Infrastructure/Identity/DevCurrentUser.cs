using DocSearch.Application.Abstractions;

namespace DocSearch.Infrastructure.Identity;

public sealed class DevCurrentUser : ICurrentUser
{
    public const string DefaultUserId = "00000000-0000-0000-0000-000000000001";
    public const string DefaultEmail = "dev@docsearch.local";

    public string UserId => DefaultUserId;
    public string Email => DefaultEmail;
    public bool IsAdmin => false;
}
