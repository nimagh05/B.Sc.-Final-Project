using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Prompts;

public sealed class PromptBuilder(ITokenCounter tokenCounter) : IPromptBuilder
{
    private static readonly ConcurrentDictionary<string, string> _systemPromptCache = new();

    public BuiltPrompt BuildPrompt(
        string question,
        IReadOnlyList<ScoredChunk> chunks,
        int maxContextTokens,
        string promptVersion = "v1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(chunks);

        var systemPrompt = GetSystemPrompt(promptVersion);
        var includedChunks = new List<ScoredChunk>();
        var contextBuilder = new StringBuilder();

        var totalTokens = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var excerptNumber = i + 1;
            var excerpt = FormatExcerpt(excerptNumber, chunk);
            var excerptTokens = tokenCounter.Count(excerpt);

            if (includedChunks.Count > 0 && totalTokens + excerptTokens > maxContextTokens)
            {
                break;
            }

            if (contextBuilder.Length > 0)
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine();
            }

            contextBuilder.Append(excerpt);
            includedChunks.Add(chunk);
            totalTokens += excerptTokens;
        }

        var contextText = contextBuilder.Length > 0 ? contextBuilder.ToString() : "(no excerpts)";
        var userPrompt = $"CONTEXT:\n{contextText}\n\nQUESTION: {question.Trim()}";

        return new BuiltPrompt(systemPrompt, userPrompt, includedChunks);
    }

    private static string FormatExcerpt(int index, ScoredChunk chunk)
    {
        var page = chunk.PageStart == chunk.PageEnd
            ? chunk.PageStart.ToString(CultureInfo.InvariantCulture)
            : $"{chunk.PageStart.ToString(CultureInfo.InvariantCulture)}-{chunk.PageEnd.ToString(CultureInfo.InvariantCulture)}";

        var section = string.IsNullOrWhiteSpace(chunk.SectionTitle)
            ? "—"
            : chunk.SectionTitle.Trim();

        return $"[{index}] (file: {chunk.FileName}, page: {page}, section: {section})\n{chunk.Text.Trim()}";
    }

    private static string GetSystemPrompt(string promptVersion)
    {
        return _systemPromptCache.GetOrAdd(promptVersion, version =>
        {
            var assembly = typeof(PromptBuilder).Assembly;
            var resourceName = $"DocSearch.Application.Prompts.system.{version}.txt";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException($"System prompt resource '{resourceName}' not found in assembly.");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Trim();
        });
    }
}
