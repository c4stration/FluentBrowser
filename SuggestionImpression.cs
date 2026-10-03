using System;

public sealed class SuggestionImpression
{
    public string GroupId { get; init; } = "";
    public string Query { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string Display { get; init; } = "";
    public string Uri { get; init; } = "";

    public int Position { get; init; }

    public float BaseScore { get; init; }
    public float IsHistory { get; init; }
    public float IsOpenTab { get; init; }
    public float DisplayLength { get; init; }
    public float QueryLength { get; init; }
    public float ExactMatch { get; init; }
    public float PrefixMatch { get; init; }
    public float HostPrefix { get; init; }
    public float TitleContains { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}