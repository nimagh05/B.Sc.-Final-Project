using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Prompts;

public sealed record ParsedCitationsResult(
    string CleanedAnswer,
    IReadOnlyList<SourceCitation> Sources,
    bool HasCitations);

public interface ICitationParser
{
    ParsedCitationsResult ParseAndValidate(
        string answer,
        IReadOnlyList<ScoredChunk> availableChunks);
}
