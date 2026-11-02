using DocSearch.Application.Rag;
using Xunit;

namespace DocSearch.Application.Tests.Rag;

public sealed class RagConstantsEncodingTests
{
    [Fact]
    public void PersianRefusalSentence_MatchesExactUnicodeEscapedSequence()
    {
        const string expected = "\u00AB\u062F\u0631 \u0627\u0633\u0646\u0627\u062F \u0645\u0648\u062C\u0648\u062F \u067E\u0627\u0633\u062E\u06CC \u0628\u0631\u0627\u06CC \u0627\u06CC\u0646 \u067E\u0631\u0633\u0634 \u067E\u06CC\u062F\u0627 \u0646\u0634\u062F.\u00BB";

        Assert.Equal(expected, RagConstants.PersianRefusalSentence);
    }

    [Fact]
    public void PersianRefusalCorePhrase_MatchesExactUnicodeEscapedSequence()
    {
        const string expected = "\u062F\u0631 \u0627\u0633\u0646\u0627\u062F \u0645\u0648\u062C\u0648\u062F \u067E\u0627\u0633\u062E\u06CC \u0628\u0631\u0627\u06CC \u0627\u06CC\u0646 \u067E\u0631\u0633\u0634 \u067E\u06CC\u062F\u0627 \u0646\u0634\u062F";

        Assert.Equal(expected, RagConstants.PersianRefusalCorePhrase);
    }
}
