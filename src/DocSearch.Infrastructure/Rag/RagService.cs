using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using DocSearch.Application.Prompts;
using DocSearch.Application.Rag;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Rag;

public sealed partial class RagService : IRagService
{
    private readonly IAccessService _accessService;
    private readonly IRetriever _retriever;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ICitationParser _citationParser;
    private readonly IChatClient _chatClient;
    private readonly AppDbContext _dbContext;
    private readonly IOptions<RagOptions> _ragOptions;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ITextNormalizer _textNormalizer;
    private readonly ILogger<RagService> _logger;

    public RagService(
        IAccessService accessService,
        IRetriever retriever,
        IPromptBuilder promptBuilder,
        ICitationParser citationParser,
        IChatClient chatClient,
        AppDbContext dbContext,
        IOptions<RagOptions> ragOptions,
        IOptions<AiOptions> aiOptions,
        ITextNormalizer textNormalizer,
        ILogger<RagService> logger)
    {
        _accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _citationParser = citationParser ?? throw new ArgumentNullException(nameof(citationParser));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _ragOptions = ragOptions ?? throw new ArgumentNullException(nameof(ragOptions));
        _aiOptions = aiOptions ?? throw new ArgumentNullException(nameof(aiOptions));
        _textNormalizer = textNormalizer ?? throw new ArgumentNullException(nameof(textNormalizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AnswerResult> AskAsync(AskRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var stopwatch = Stopwatch.StartNew();

        // 1. Enforce ACL across requested collections
        var readableCollectionIds = await _accessService
            .GetReadableCollectionIdsAsync(request.UserId, request.CollectionIds, ct)
            .ConfigureAwait(false);

        if (readableCollectionIds.Count == 0)
        {
            throw new NotFoundException("None of the requested collections were found or visible.");
        }

        // 2. Retrieve candidate chunks
        var retrieval = await _retriever
            .RetrieveAsync(request.Question, readableCollectionIds, request.TopK, ct)
            .ConfigureAwait(false);

        // 3. Pre-LLM Refusal Check: Zero chunks
        if (retrieval.Chunks.Count == 0)
        {
            var result = new AnswerResult(
                Answer: RagConstants.PersianRefusalSentence,
                NotFound: true,
                Degraded: false,
                Sources: [],
                ConversationId: request.ConversationId);

            return await CompleteAndPersistAsync(
                request,
                result,
                NoAnswerReason.NoChunksRetrieved,
                retrieval.Chunks.Count,
                retrieval.BestVectorScore,
                stopwatch,
                ct).ConfigureAwait(false);
        }

        // 4. Pre-LLM Refusal Check: Low similarity
        if (retrieval.BestVectorScore < _ragOptions.Value.MinSimilarity && !retrieval.HasFtsHit)
        {
            var sources = _ragOptions.Value.ReturnSourcesWhenNotFound
                ? FormatSources(retrieval.Chunks)
                : (IReadOnlyList<SourceCitation>)[];

            var result = new AnswerResult(
                Answer: RagConstants.PersianRefusalSentence,
                NotFound: true,
                Degraded: false,
                Sources: sources,
                ConversationId: request.ConversationId);

            return await CompleteAndPersistAsync(
                request,
                result,
                NoAnswerReason.LowSimilarity,
                retrieval.Chunks.Count,
                retrieval.BestVectorScore,
                stopwatch,
                ct).ConfigureAwait(false);
        }

        // 5. Construct Prompt within context token budget
        var builtPrompt = _promptBuilder.BuildPrompt(
            request.Question,
            retrieval.Chunks,
            _ragOptions.Value.MaxContextTokens,
            _ragOptions.Value.PromptVersion);

        // 6. Invoke LLM with Degraded Mode & Timeout Resilience
        var chatTimeoutSeconds = _aiOptions.Value.Chat.TimeoutSeconds;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(chatTimeoutSeconds));

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, builtPrompt.SystemPrompt),
            new(ChatRole.User, builtPrompt.UserPrompt)
        };

        var chatOptions = new ChatOptions
        {
            Temperature = (float)_ragOptions.Value.Temperature,
            MaxOutputTokens = _aiOptions.Value.Chat.MaxOutputTokens
        };

        string? rawLlmResponse = null;
        var isDegraded = false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = await _chatClient
                    .GetResponseAsync(chatMessages, chatOptions, linkedCts.Token)
                    .ConfigureAwait(false);

                rawLlmResponse = response.Text;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsDegradedException(ex))
            {
                if (attempt == 1 && !linkedCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(200, linkedCts.Token).ConfigureAwait(false);
                        continue;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout on backoff delay -> proceed to fallback
                    }
                }

                _logger.LogWarning(ex, "Chat LLM provider failed. Falling back to degraded retrieval-only mode.");
                isDegraded = true;
                break;
            }
        }

        if (isDegraded || rawLlmResponse == null)
        {
            var degradedResult = new AnswerResult(
                Answer: null,
                NotFound: false,
                Degraded: true,
                Sources: FormatSources(builtPrompt.IncludedChunks),
                ConversationId: request.ConversationId);

            return await CompleteAndPersistAsync(
                request,
                degradedResult,
                NoAnswerReason.None,
                retrieval.Chunks.Count,
                retrieval.BestVectorScore,
                stopwatch,
                ct).ConfigureAwait(false);
        }

        // 7. Post-LLM Self-Refusal Check
        if (IsModelRefusal(rawLlmResponse))
        {
            var sources = _ragOptions.Value.ReturnSourcesWhenNotFound
                ? FormatSources(builtPrompt.IncludedChunks)
                : (IReadOnlyList<SourceCitation>)[];

            var refusalResult = new AnswerResult(
                Answer: RagConstants.PersianRefusalSentence,
                NotFound: true,
                Degraded: false,
                Sources: sources,
                ConversationId: request.ConversationId);

            return await CompleteAndPersistAsync(
                request,
                refusalResult,
                NoAnswerReason.ModelRefusal,
                retrieval.Chunks.Count,
                retrieval.BestVectorScore,
                stopwatch,
                ct).ConfigureAwait(false);
        }

        // 8. Citation Extraction & Validation
        var citationResult = _citationParser.ParseAndValidate(rawLlmResponse, builtPrompt.IncludedChunks);
        IReadOnlyList<SourceCitation> finalSources;
        var finalAnswer = citationResult.CleanedAnswer;

        if (!citationResult.HasCitations)
        {
            _logger.LogWarning("{Warning}: LLM generated answer without citations. Falling back to all context chunks.", RagConstants.ModelWithoutCitationsWarning);
            finalSources = FormatSources(builtPrompt.IncludedChunks);
        }
        else
        {
            finalSources = citationResult.Sources;
        }

        var finalResult = new AnswerResult(
            Answer: finalAnswer,
            NotFound: false,
            Degraded: false,
            Sources: finalSources,
            ConversationId: request.ConversationId);

        return await CompleteAndPersistAsync(
            request,
            finalResult,
            NoAnswerReason.None,
            retrieval.Chunks.Count,
            retrieval.BestVectorScore,
            stopwatch,
            ct).ConfigureAwait(false);
    }

    private static bool IsDegradedException(Exception ex)
    {
        return ex is HttpRequestException
            or TimeoutException
            or OperationCanceledException
            or ClientResultException;
    }

    [GeneratedRegex(@"\[(.*?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationBracketRegex();

    private bool IsModelRefusal(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        var cleaned = CitationBracketRegex().Replace(response, "");
        var normalizedResponse = _textNormalizer.NormalizeForSearch(cleaned);
        var normalizedCore = _textNormalizer.NormalizeForSearch(RagConstants.PersianRefusalCorePhrase);

        return normalizedResponse.Contains(normalizedCore, StringComparison.Ordinal)
            || response.Contains(RagConstants.PersianRefusalCorePhrase, StringComparison.Ordinal)
            || response.Contains(RagConstants.PersianRefusalSentence, StringComparison.Ordinal);
    }

    private static List<SourceCitation> FormatSources(IReadOnlyList<ScoredChunk> chunks)
    {
        var list = new List<SourceCitation>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            list.Add(new SourceCitation(
                Index: i + 1,
                DocumentId: c.DocumentId,
                FileName: c.FileName,
                Page: c.PageStart,
                SectionTitle: c.SectionTitle,
                Snippet: CitationParser.CreateSnippet(c.Text),
                Score: c.FusedScore));
        }
        return list;
    }

    private async Task<AnswerResult> CompleteAndPersistAsync(
        AskRequest request,
        AnswerResult result,
        NoAnswerReason noAnswerReason,
        int retrievedCount,
        double bestScore,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var effectiveConvId = request.ConversationId;

        try
        {
            if (effectiveConvId.HasValue)
            {
                var conv = await _dbContext.Conversations
                    .FirstOrDefaultAsync(c => c.Id == effectiveConvId.Value && c.UserId == request.UserId, ct)
                    .ConfigureAwait(false);

                if (conv == null)
                {
                    throw new NotFoundException("Conversation was not found.");
                }
            }
            else
            {
                var title = request.Question.Length <= 60
                    ? request.Question
                    : request.Question[..60];

                var newConv = new Conversation
                {
                    Id = Guid.CreateVersion7(),
                    UserId = request.UserId,
                    Title = title,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                await _dbContext.Conversations.AddAsync(newConv, ct).ConfigureAwait(false);
                effectiveConvId = newConv.Id;
            }

            var userMsg = new Message
            {
                Id = Guid.CreateVersion7(),
                ConversationId = effectiveConvId.Value,
                Role = MessageRole.User,
                Content = request.Question,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var assistantMsg = new Message
            {
                Id = Guid.CreateVersion7(),
                ConversationId = effectiveConvId.Value,
                Role = MessageRole.Assistant,
                Content = result.Answer ?? string.Empty,
                SourcesJson = result.Sources.Count > 0 ? JsonSerializer.Serialize(result.Sources) : null,
                Degraded = result.Degraded,
                NotFound = result.NotFound,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _dbContext.Messages.AddAsync(userMsg, ct).ConfigureAwait(false);
            await _dbContext.Messages.AddAsync(assistantMsg, ct).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist conversation history for AskAsync.");
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "AskCompleted: ElapsedMs={ElapsedMs}, RetrievedCount={RetrievedCount}, BestScore={BestScore:F3}, NotFound={NotFound}, Degraded={Degraded}, NoAnswerReason={NoAnswerReason}",
            stopwatch.ElapsedMilliseconds,
            retrievedCount,
            bestScore,
            result.NotFound,
            result.Degraded,
            noAnswerReason);

        return result with { ConversationId = effectiveConvId };
    }
}
