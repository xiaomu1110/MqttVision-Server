namespace MqttVision.Server.Domain;

public sealed class CabinetConfiguration
{
    public string CabinetId { get; init; } = string.Empty;

    public int TerminalStartNumber { get; init; } = 1;

    public List<CabinetTerminalConfiguration> Terminals { get; init; } = new();

    public List<CabinetTerminalStripConfiguration> TerminalStrips { get; init; } = new();

    public JsonConfigurationSource? JsonSource { get; init; }

    public List<string> ImportWarnings { get; init; } = new();
}

public sealed class CabinetTerminalConfiguration
{
    public int TerminalNumber { get; init; }

    public string? TerminalLabel { get; init; }

    public string? ExpectedWireMarker { get; init; }

    public List<string> WireMarkers { get; init; } = new();

    public string? LeftWireMarker { get; init; }

    public string? RightWireMarker { get; init; }

    public string? WirePrefix { get; init; }

    public bool IsWirePrefixInherited { get; init; }

    public string? AuxiliaryValue { get; init; }

    public string? Destination { get; init; }

    public string? StripId { get; init; }

    public string? StripCode { get; init; }

    public int SourceOrdinal { get; init; }

    public bool IsExpectedEmpty { get; init; }

    public string? Note { get; init; }
}

public sealed class CabinetTerminalStripConfiguration
{
    public string StripId { get; init; } = string.Empty;

    public string StripCode { get; init; } = string.Empty;

    public string Orientation { get; init; } = "vertical";

    public List<CabinetTerminalConfiguration> Terminals { get; init; } = new();
}

public sealed class JsonConfigurationSource
{
    public string OriginalFileName { get; init; } = string.Empty;

    public string? Sha256 { get; init; }

    public string Format { get; init; } = string.Empty;

    public string? SourcePath { get; init; }

    public string? ImportedAt { get; init; }
}

public sealed class ConfigurationComparisonResult
{
    public string CabinetId { get; init; } = string.Empty;

    public int? ResolvedTerminalStartNumber { get; init; }

    public int? ResolvedTerminalEndNumber { get; init; }

    public string AlignmentStrategy { get; init; } = string.Empty;

    public ConfigurationLocationResult Location { get; init; } = new();

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
