using MqttVision.Server.Domain;

namespace MqttVision.Server.Configuration;

public sealed record CabinetConfigurationSummary(
    string CabinetId,
    int TerminalStartNumber,
    int TerminalCount,
    string FilePath,
    DateTimeOffset UpdatedAt);

public sealed record CabinetConfigurationSaveResult(
    bool Success,
    string Message,
    CabinetConfigurationEditorForm? Cabinet,
    IReadOnlyList<ConfigurationValidationIssue> Issues);

public sealed class CabinetConfigurationEditorForm
{
    public string CabinetId { get; set; } = string.Empty;

    public int TerminalStartNumber { get; set; } = 1;

    public List<CabinetTerminalEditorRow> Terminals { get; set; } = new();

    public static CabinetConfigurationEditorForm FromDomain(CabinetConfiguration configuration) =>
        new()
        {
            CabinetId = configuration.CabinetId,
            TerminalStartNumber = configuration.TerminalStartNumber,
            Terminals = configuration.Terminals
                .OrderBy(terminal => terminal.TerminalNumber)
                .Select(CabinetTerminalEditorRow.FromDomain)
                .ToList()
        };

    public CabinetConfiguration ToDomain() =>
        new()
        {
            CabinetId = CabinetId.Trim(),
            TerminalStartNumber = TerminalStartNumber,
            Terminals = Terminals
                .Where(terminal => terminal.TerminalNumber > 0)
                .OrderBy(terminal => terminal.TerminalNumber)
                .Select(terminal => terminal.ToDomain())
                .ToList()
        };
}

public sealed class CabinetTerminalEditorRow
{
    public int TerminalNumber { get; set; }

    public string? ExpectedWireMarker { get; set; }

    public string? Note { get; set; }

    public static CabinetTerminalEditorRow FromDomain(CabinetTerminalConfiguration terminal) =>
        new()
        {
            TerminalNumber = terminal.TerminalNumber,
            ExpectedWireMarker = terminal.ExpectedWireMarker,
            Note = terminal.Note
        };

    public CabinetTerminalConfiguration ToDomain() =>
        new()
        {
            TerminalNumber = TerminalNumber,
            ExpectedWireMarker = Normalize(ExpectedWireMarker),
            Note = Normalize(Note)
        };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
