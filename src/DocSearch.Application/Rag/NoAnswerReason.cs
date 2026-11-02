namespace DocSearch.Application.Rag;

public enum NoAnswerReason
{
    None = 0,
    NoChunksRetrieved,
    LowSimilarity,
    ModelRefusal
}
