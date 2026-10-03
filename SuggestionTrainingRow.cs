namespace FluentBrowser;

public sealed class SuggestionTrainingRow
{
    public uint GroupId { get; set; }

    public float Label { get; set; }

    public float BaseScore { get; set; }
    public float IsHistory { get; set; }
    public float IsOpenTab { get; set; }
    public float DisplayLength { get; set; }
    public float QueryLength { get; set; }
    public float ExactMatch { get; set; }
    public float PrefixMatch { get; set; }
    public float HostPrefix { get; set; }
    public float TitleContains { get; set; }
}