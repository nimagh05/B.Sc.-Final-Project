namespace DocSearch.Application.Abstractions;

public interface ICurrentUser
{
    string UserId { get; }
    string Email { get; }
    bool IsAdmin { get; }
}
