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

    // JSON 导入配置可能包含多个端子排，端子号在不同端子排中允许重复。
    // 保留完整端子排数据，确保后台备份恢复不会把它们压平成冲突的全局编号。
    public List<CabinetTerminalStripConfiguration> TerminalStrips { get; set; } = new();

    public static CabinetConfigurationEditorForm FromDomain(CabinetConfiguration configuration) =>
        new()
        {
            CabinetId = configuration.CabinetId,
            TerminalStartNumber = configuration.TerminalStartNumber,
            TerminalStrips = configuration.TerminalStrips,
            Terminals = configuration.Terminals
                .OrderBy(terminal => terminal.TerminalNumber)
                .Select(CabinetTerminalEditorRow.FromDomain)
                .ToList()
        };

    public CabinetConfiguration ToDomain() =>
        TerminalStrips.Count > 0
            ? new CabinetConfiguration
            {
                CabinetId = CabinetId.Trim(),
                TerminalStartNumber = TerminalStartNumber,
                TerminalStrips = TerminalStrips,
                Terminals = TerminalStrips.SelectMany(strip => strip.Terminals).ToList()
            }
            : new CabinetConfiguration
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
