using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Prompts;

public sealed record BuiltPrompt(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<ScoredChunk> IncludedChunks);

public interface IPromptBuilder
{
    BuiltPrompt BuildPrompt(
        string question,
        IReadOnlyList<ScoredChunk> chunks,
        int maxContextTokens,
        string promptVersion = "v1");
}
