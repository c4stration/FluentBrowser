using System;

public sealed class SuggestionClick
{
    public string GroupId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}