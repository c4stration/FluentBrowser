using MessagePack;
using System;

[MessagePackObject]
public sealed class SuggestionClick
{
    [Key(0)]
    public string GroupId { get; init; } = "";
    [Key(1)]
    public string CandidateId { get; init; } = "";
    [Key(2)]
    public DateTimeOffset Timestamp { get; init; }
}