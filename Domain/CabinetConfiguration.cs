namespace MqttVision.Server.Domain;

public sealed class CabinetConfiguration
{
    public string CabinetId { get; init; } = string.Empty;

    public int TerminalStartNumber { get; init; } = 1;

    public List<CabinetTerminalConfiguration> Terminals { get; init; } = new();
}

public sealed class CabinetTerminalConfiguration
{
    public int TerminalNumber { get; init; }

    public string? ExpectedWireMarker { get; init; }

    public string? Note { get; init; }
}

public sealed class ConfigurationComparisonResult
{
    public string CabinetId { get; init; } = string.Empty;

    public int? ResolvedTerminalStartNumber { get; init; }

    public int? ResolvedTerminalEndNumber { get; init; }

    public string AlignmentStrategy { get; init; } = string.Empty;

    public int CheckedCount { get; init; }

    public int MatchedCount { get; init; }

    public int MismatchCount { get; init; }

    public int UnrecognizedCount { get; init; }

    public List<ConfigurationComparisonItem> Items { get; init; } = new();
}

public sealed class ConfigurationComparisonItem
{
    public int PairIndex { get; init; }

    public int TerminalNumber { get; init; }

    public int TerminalDetectionId { get; init; }

    public int? WireMarkerTubeDetectionId { get; init; }

    public string PairCategory { get; init; } = string.Empty;

    public string? ExpectedWireMarker { get; init; }

    public string? ActualWireMarker { get; init; }

    public double? Confidence { get; init; }

    public string OcrStatus { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
