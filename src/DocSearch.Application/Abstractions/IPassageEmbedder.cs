namespace DocSearch.Application.Abstractions;

public interface IPassageEmbedder
{
    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct);
}
