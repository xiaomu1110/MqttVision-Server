using System.Text.Json;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Configuration;

public sealed class CabinetConfigurationAdminService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly RuntimeConfigurationService configuration;
    private readonly IHostEnvironment environment;
    private readonly SemaphoreSlim saveLock = new(1, 1);

    public CabinetConfigurationAdminService(
        RuntimeConfigurationService configuration,
        IHostEnvironment environment)
    {
        this.configuration = configuration;
        this.environment = environment;
    }

    public async Task<IReadOnlyList<CabinetConfigurationSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var root = ResolveCabinetRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var summaries = new List<CabinetConfigurationSummary>();
        foreach (var filePath in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var cabinet = await ReadCabinetAsync(filePath, cancellationToken);
                summaries.Add(new CabinetConfigurationSummary(
                    cabinet.CabinetId,
                    cabinet.TerminalStartNumber,
                    cabinet.Terminals.Count,
                    filePath,
                    File.GetLastWriteTime(filePath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                summaries.Add(new CabinetConfigurationSummary(
                    Path.GetFileNameWithoutExtension(filePath),
                    1,
                    0,
                    filePath,
                    File.GetLastWriteTime(filePath)));
            }
        }

        return summaries
            .OrderBy(summary => summary.CabinetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CabinetConfigurationEditorForm> GetAsync(
        string cabinetId,
        CancellationToken cancellationToken = default)
    {
        var safeCabinetId = NormalizeCabinetId(cabinetId);
        var filePath = ResolveCabinetPath(safeCabinetId);
        if (!File.Exists(filePath))
        {
            return new CabinetConfigurationEditorForm
            {
                CabinetId = safeCabinetId,
                TerminalStartNumber = 1
            };
        }

        return CabinetConfigurationEditorForm.FromDomain(await ReadCabinetAsync(filePath, cancellationToken));
    }

    public async Task<CabinetConfigurationSaveResult> SaveAsync(
        CabinetConfigurationEditorForm form,
        CancellationToken cancellationToken = default)
    {
        var cabinet = form.ToDomain();
        var issues = Validate(cabinet);
        if (issues.Any(issue => !issue.IsWarning))
        {
            return new CabinetConfigurationSaveResult(false, "柜体配置校验未通过，未保存。", null, issues);
        }

        await saveLock.WaitAsync(cancellationToken);
        try
        {
            var filePath = ResolveCabinetPath(cabinet.CabinetId);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, cabinet, JsonOptions, cancellationToken);
            }

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, $"{filePath}.bak", true);
            }
            else
            {
                File.Move(tempPath, filePath);
            }

            var saved = await GetAsync(cabinet.CabinetId, cancellationToken);
            return new CabinetConfigurationSaveResult(true, "柜体配置已保存，新检测任务会使用最新配置。", saved, issues);
        }
        finally
        {
            saveLock.Release();
        }
    }

    public IReadOnlyList<ConfigurationValidationIssue> Validate(CabinetConfiguration configuration)
    {
        var issues = new List<ConfigurationValidationIssue>();
        if (!IsValidCabinetId(configuration.CabinetId))
        {
            issues.Add(new ConfigurationValidationIssue("cabinetId", "柜体编号只能包含字母、数字、下划线和短横线。"));
        }

        if (configuration.TerminalStartNumber < 1)
        {
            issues.Add(new ConfigurationValidationIssue("terminalStartNumber", "起始端子号必须大于 0。"));
        }

        var duplicateTerminal = configuration.Terminals
            .Where(terminal => terminal.TerminalNumber > 0)
            .GroupBy(terminal => terminal.TerminalNumber)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTerminal is not null)
        {
            issues.Add(new ConfigurationValidationIssue("terminals", $"端子号 {duplicateTerminal.Key} 重复。"));
        }

        if (configuration.Terminals.Any(terminal => terminal.TerminalNumber < 1))
        {
            issues.Add(new ConfigurationValidationIssue("terminals", "端子号必须大于 0。"));
        }

        if (configuration.Terminals.Count == 0)
        {
            issues.Add(new ConfigurationValidationIssue("terminals", "至少需要配置一个端子。"));
        }

        var duplicateWireMarker = configuration.Terminals
            .Where(terminal => !string.IsNullOrWhiteSpace(terminal.ExpectedWireMarker))
            .GroupBy(terminal => terminal.ExpectedWireMarker!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateWireMarker is not null)
        {
            issues.Add(new ConfigurationValidationIssue(
                "terminals",
                $"线号 {duplicateWireMarker.Key} 被配置到多个端子，请确认是否符合现场设计。",
                true));
        }

        return issues;
    }

    public string ResolveCabinetRoot()
    {
        var root = configuration.Current.Processing.CabinetConfigurationRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "Configuration";
        }

        var expanded = Environment.ExpandEnvironmentVariables(root.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(environment.ContentRootPath, expanded));
    }

    private async Task<CabinetConfiguration> ReadCabinetAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var cabinet = await JsonSerializer.DeserializeAsync<CabinetConfiguration>(
            stream,
            JsonOptions,
            cancellationToken: cancellationToken);
        return cabinet ?? new CabinetConfiguration
        {
            CabinetId = Path.GetFileNameWithoutExtension(filePath),
            TerminalStartNumber = 1
        };
    }

    private string ResolveCabinetPath(string cabinetId)
    {
        var safeCabinetId = NormalizeCabinetId(cabinetId);
        return Path.Combine(ResolveCabinetRoot(), $"{safeCabinetId}.json");
    }

    private static string NormalizeCabinetId(string cabinetId)
    {
        var normalized = cabinetId.Trim();
        if (!IsValidCabinetId(normalized))
        {
            throw new InvalidOperationException("柜体编号只能包含字母、数字、下划线和短横线。");
        }

        return normalized;
    }

    private static bool IsValidCabinetId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');

    public void Dispose()
    {
        saveLock.Dispose();
    }
}
