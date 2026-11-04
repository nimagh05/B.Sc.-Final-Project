namespace DocSearch.Application.Rag;

public static class RagConstants
{
    public const string PersianRefusalCorePhrase = "در اسناد موجود پاسخی برای این پرسش پیدا نشد";
    public const string PersianRefusalSentence = "«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»";
    public const int MaxRefusalRemainderLength = 15;
    public const string ModelWithoutCitationsWarning = "ModelWithoutCitations";
}
