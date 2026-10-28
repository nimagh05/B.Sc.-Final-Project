namespace DocSearch.Application.Abstractions;

public interface IQueryEmbedder
{
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);
}
