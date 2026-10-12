using MessagePack;
using System;

[MessagePackObject]
public sealed class SuggestionImpression
{
    [Key(0)]
    public string GroupId { get; init; } = "";
    [Key(1)]
    public string Query { get; init; } = "";
    [Key(2)]
    public string CandidateId { get; init; } = "";
    [Key(3)]
    public string Display { get; init; } = "";
    [Key(4)]
    public string Uri { get; init; } = "";

    [Key(5)]
    public int Position { get; init; }

    [Key(6)]
    public float BaseScore { get; init; }
    [Key(7)]
    public float IsHistory { get; init; }
    [Key(8)]
    public float IsOpenTab { get; init; }
    [Key(9)]
    public float DisplayLength { get; init; }
    [Key(10)]
    public float QueryLength { get; init; }
    [Key(11)]
    public float ExactMatch { get; init; }
    [Key(12)]
    public float PrefixMatch { get; init; }
    [Key(13)]
    public float HostPrefix { get; init; }
    [Key(14)]
    public float TitleContains { get; init; }

    [Key(15)]
    public DateTimeOffset Timestamp { get; init; }
}