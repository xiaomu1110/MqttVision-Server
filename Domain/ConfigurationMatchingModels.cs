namespace MqttVision.Server.Domain;

public sealed record ConfigurationMarkerObservation(
    int DetectionId,
    string? Text,
    double? Confidence,
    double X,
    double Y);

public sealed class ConfigurationLocationResult
{
    public string Status { get; init; } = "unresolved";

    public string Strategy { get; init; } = string.Empty;

    public string? CabinetId { get; init; }

    public string? StripId { get; init; }

    public string? StripCode { get; init; }

    public int ObservedMarkerCount { get; init; }

    public int MatchedMarkerCount { get; init; }

    public int DistinctMatchedMarkerCount { get; init; }

    public double Score { get; init; }

    public double Confidence { get; init; }

    public List<ConfigurationLocationRound> Rounds { get; init; } = new();

    public List<ConfigurationLocationCandidate> Candidates { get; init; } = new();

    public List<ConfigurationLocationEvidence> Evidence { get; init; } = new();
}

public sealed class ConfigurationLocationCandidate
{
    public string CabinetId { get; init; } = string.Empty;

    public string StripId { get; init; } = string.Empty;

    public string StripCode { get; init; } = string.Empty;

    public int MatchedMarkerCount { get; init; }

    public int DistinctMatchedMarkerCount { get; init; }

    public int OccurrenceCount { get; init; }

    public int ExactMatchCount { get; init; }

    public int VariantMatchCount { get; init; }

    public int FuzzyMatchCount { get; init; }

    public double Score { get; init; }

    public double Confidence { get; init; }

    public List<string> MatchedMarkers { get; init; } = new();
}

public sealed class ConfigurationLocationEvidence
{
    public int DetectionId { get; init; }

    public string? ObservedText { get; init; }

    public string? NormalizedText { get; init; }

    public bool Matched { get; init; }

    public int Round { get; init; }

    public string MatchMethod { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class ConfigurationLocationRound
{
    public int Round { get; init; }

    public string Name { get; init; } = string.Empty;

    public int InputCount { get; init; }

    public int MatchedObservationCount { get; init; }

    public int CandidateCount { get; init; }

    public string Result { get; init; } = string.Empty;
}
