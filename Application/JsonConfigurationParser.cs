using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Application;

public sealed record JsonConfigurationParseResult(
    CabinetConfiguration Configuration,
    IReadOnlyList<string> Warnings,
    string Format);

public interface IJsonConfigurationParser
{
    Task<JsonConfigurationParseResult> ParseAsync(
        string jsonPath,
        string cabinetId,
        JsonConfigurationSource source,
        CancellationToken cancellationToken = default);
}

public sealed class JsonConfigurationParser : IJsonConfigurationParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex TerminalLabelPattern = new(
        "^\\d+[a-z]?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<JsonConfigurationParseResult> ParseAsync(
        string jsonPath,
        string cabinetId,
        JsonConfigurationSource source,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(jsonPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("JSON 配置根节点必须是对象。");
        }

        var warnings = new List<string>();
        if (LooksLikeServerConfiguration(root))
        {
            var configuration = root.Deserialize<CabinetConfiguration>(JsonOptions)
                ?? throw new InvalidOperationException("JSON 柜体配置内容为空。");
            return new JsonConfigurationParseResult(
                NormalizeServerConfiguration(configuration, cabinetId, source, warnings),
                warnings,
                "server-cabinet-configuration-v1");
        }

        return new JsonConfigurationParseResult(
            ParseRelationMap(root, cabinetId, source, warnings),
            warnings,
            "wire-marker-relation-map-v1");
    }

    private static bool LooksLikeServerConfiguration(JsonElement root) =>
        root.EnumerateObject().Any(property =>
            property.Name.Equals("terminals", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("terminalStrips", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("cabinetId", StringComparison.OrdinalIgnoreCase));

    private static CabinetConfiguration ParseRelationMap(
        JsonElement root,
        string cabinetId,
        JsonConfigurationSource source,
        ICollection<string> warnings)
    {
        var strips = new List<StripBuilder>();
        var stripByCode = new Dictionary<string, StripBuilder>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;

        foreach (var property in root.EnumerateObject())
        {
            var marker = property.Name.Trim();
            if (marker.Length == 0)
            {
                warnings.Add("发现空的线号管编号，已跳过。");
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"线号管“{marker}”的值不是对象，已跳过。");
                continue;
            }

            var stripCode = ReadString(property.Value, "terminal_block");
            var terminalLabel = ReadString(property.Value, "terminal");
            if (string.IsNullOrWhiteSpace(stripCode) || string.IsNullOrWhiteSpace(terminalLabel))
            {
                warnings.Add($"线号管“{marker}”缺少 terminal_block 或 terminal，已跳过。");
                continue;
            }

            stripCode = stripCode.Trim();
            terminalLabel = terminalLabel.Trim();
            if (!TerminalLabelPattern.IsMatch(terminalLabel))
            {
                warnings.Add($"端子“{terminalLabel}”不是数字或数字加小写字母格式，已跳过线号管“{marker}”。");
                continue;
            }

            if (!stripByCode.TryGetValue(stripCode, out var strip))
            {
                strip = new StripBuilder(stripCode);
                stripByCode[stripCode] = strip;
                strips.Add(strip);
            }

            strip.Add(terminalLabel, marker);
            entryCount++;
        }

        if (entryCount == 0 || strips.Count == 0)
        {
            throw new InvalidOperationException("JSON 中没有可用的线号管关系记录。");
        }

        var stripConfigurations = strips
            .Select(strip => BuildStrip(strip, cabinetId, warnings))
            .ToList();
        var terminals = stripConfigurations
            .SelectMany(strip => strip.Terminals)
            .ToList();
        var firstNumber = terminals
            .Where(terminal => terminal.TerminalNumber > 0)
            .Select(terminal => terminal.TerminalNumber)
            .DefaultIfEmpty(1)
            .Min();

        return new CabinetConfiguration
        {
            CabinetId = cabinetId,
            TerminalStartNumber = firstNumber,
            Terminals = terminals,
            TerminalStrips = stripConfigurations,
            JsonSource = source,
            ImportWarnings = warnings.ToList()
        };
    }

    private static CabinetConfiguration NormalizeServerConfiguration(
        CabinetConfiguration configuration,
        string cabinetId,
        JsonConfigurationSource source,
        ICollection<string> warnings)
    {
        var sourceStrips = (configuration.TerminalStrips ?? []).Count > 0
            ? configuration.TerminalStrips
            :
            [
                new CabinetTerminalStripConfiguration
                {
                    StripCode = "default",
                    StripId = $"{cabinetId}-default",
                    Orientation = "json",
                    Terminals = configuration.Terminals ?? []
                }
            ];
        var strips = sourceStrips
            .Select((strip, stripIndex) => NormalizeStrip(strip, cabinetId, stripIndex, warnings))
            .ToList();
        var terminals = strips.SelectMany(strip => strip.Terminals).ToList();
        if (terminals.Count == 0)
        {
            throw new InvalidOperationException("JSON 柜体配置中没有端子记录。");
        }

        return new CabinetConfiguration
        {
            CabinetId = cabinetId,
            TerminalStartNumber = configuration.TerminalStartNumber > 0
                ? configuration.TerminalStartNumber
                : terminals.Min(terminal => terminal.TerminalNumber),
            Terminals = terminals,
            TerminalStrips = strips,
            JsonSource = source,
            ImportWarnings = (configuration.ImportWarnings ?? [])
                .Concat(warnings)
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    private static CabinetTerminalStripConfiguration NormalizeStrip(
        CabinetTerminalStripConfiguration strip,
        string cabinetId,
        int stripIndex,
        ICollection<string> warnings)
    {
        var stripCode = string.IsNullOrWhiteSpace(strip.StripCode)
            ? $"strip-{stripIndex + 1}"
            : strip.StripCode.Trim();
        var stripId = string.IsNullOrWhiteSpace(strip.StripId)
            ? $"{cabinetId}-{stripCode.ToLowerInvariant()}"
            : strip.StripId.Trim();
        var terminals = (strip.Terminals ?? [])
            .Select((terminal, ordinal) => NormalizeTerminal(terminal, stripCode, stripId, ordinal, warnings))
            .ToList();
        return new CabinetTerminalStripConfiguration
        {
            StripId = stripId,
            StripCode = stripCode,
            Orientation = string.IsNullOrWhiteSpace(strip.Orientation) ? "json" : strip.Orientation,
            Terminals = terminals
        };
    }

    private static CabinetTerminalConfiguration NormalizeTerminal(
        CabinetTerminalConfiguration terminal,
        string stripCode,
        string stripId,
        int ordinal,
        ICollection<string> warnings)
    {
        var label = string.IsNullOrWhiteSpace(terminal.TerminalLabel)
            ? terminal.TerminalNumber.ToString(CultureInfo.InvariantCulture)
            : terminal.TerminalLabel.Trim();
        if (!TerminalLabelPattern.IsMatch(label))
        {
            warnings.Add($"端子排“{stripCode}”中的端子“{label}”格式不符合要求。");
        }

        var markers = GetMarkers(terminal).ToList();
        var number = ParseTerminalNumber(label, terminal.TerminalNumber);
        return new CabinetTerminalConfiguration
        {
            TerminalNumber = number,
            TerminalLabel = label,
            ExpectedWireMarker = markers.FirstOrDefault(),
            WireMarkers = markers,
            LeftWireMarker = terminal.LeftWireMarker,
            RightWireMarker = terminal.RightWireMarker,
            WirePrefix = terminal.WirePrefix ?? markers.Select(GetWirePrefix).FirstOrDefault(prefix => prefix is not null),
            IsWirePrefixInherited = terminal.IsWirePrefixInherited,
            AuxiliaryValue = terminal.AuxiliaryValue,
            Destination = terminal.Destination,
            StripId = stripId,
            StripCode = stripCode,
            SourceOrdinal = ordinal,
            IsExpectedEmpty = markers.Count == 0,
            Note = terminal.Note
        };
    }

    private static CabinetTerminalStripConfiguration BuildStrip(
        StripBuilder strip,
        string cabinetId,
        ICollection<string> warnings)
    {
        var rowBuilders = new Dictionary<string, TerminalBuilder>(
            strip.Terminals,
            StringComparer.OrdinalIgnoreCase);
        var numericLabels = rowBuilders.Keys
            .Select(ParseTerminalNumber)
            .Where(number => number > 0)
            .ToArray();
        var minimum = numericLabels.DefaultIfEmpty(1).Min();
        var maximum = numericLabels.DefaultIfEmpty(0).Max();
        for (var number = minimum; number <= maximum; number++)
        {
            var hasObservedLabel = rowBuilders.Keys.Any(label => ParseTerminalNumber(label) == number);
            if (!hasObservedLabel)
            {
                var label = number.ToString(CultureInfo.InvariantCulture);
                rowBuilders[label] = new TerminalBuilder(label);
                warnings.Add($"端子排“{strip.Code}”缺少端子“{label}”的线号管记录，已补为空端子。");
            }
        }

        var stripId = $"{cabinetId}-{strip.Code.ToLowerInvariant()}";
        var terminals = rowBuilders.Values
            .OrderBy(builder => ParseTerminalNumber(builder.Label))
            .ThenBy(builder => builder.Label.EndsWith('a') ? 1 : 0)
            .ThenBy(builder => builder.Label, StringComparer.Ordinal)
            .Select((builder, ordinal) =>
            {
                var markers = builder.Markers.ToList();
                return new CabinetTerminalConfiguration
                {
                    TerminalNumber = ParseTerminalNumber(builder.Label),
                    TerminalLabel = builder.Label,
                    ExpectedWireMarker = markers.FirstOrDefault(),
                    WireMarkers = markers,
                    WirePrefix = markers.Select(GetWirePrefix).FirstOrDefault(prefix => prefix is not null),
                    StripId = stripId,
                    StripCode = strip.Code,
                    SourceOrdinal = ordinal,
                    IsExpectedEmpty = markers.Count == 0,
                    Note = markers.Count == 0 ? "JSON 未提供该端子的线号管关系" : null
                };
            })
            .ToList();
        return new CabinetTerminalStripConfiguration
        {
            StripId = stripId,
            StripCode = strip.Code,
            Orientation = "json",
            Terminals = terminals
        };
    }

    private static IReadOnlyList<string> GetMarkers(CabinetTerminalConfiguration terminal) =>
        (terminal.WireMarkers ?? [])
            .Concat([terminal.ExpectedWireMarker, terminal.LeftWireMarker, terminal.RightWireMarker])
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Select(marker => marker!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? GetWirePrefix(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return null;
        }

        var separator = marker.IndexOf('/');
        return separator > 0 ? marker[..separator] : null;
    }

    private static int ParseTerminalNumber(string? label, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return fallback;
        }

        var digits = new string(label.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
    }

    private static string? ReadString(JsonElement objectElement, string propertyName)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                _ => null
            };
        }

        return null;
    }

    private sealed class StripBuilder(string code)
    {
        public string Code { get; } = code;

        public Dictionary<string, TerminalBuilder> Terminals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string label, string marker)
        {
            if (!Terminals.TryGetValue(label, out var terminal))
            {
                terminal = new TerminalBuilder(label);
                Terminals[label] = terminal;
            }

            if (!terminal.Markers.Contains(marker, StringComparer.OrdinalIgnoreCase))
            {
                terminal.Markers.Add(marker);
            }
        }
    }

    private sealed class TerminalBuilder(string label)
    {
        public string Label { get; } = label;

        public List<string> Markers { get; } = [];
    }
}
