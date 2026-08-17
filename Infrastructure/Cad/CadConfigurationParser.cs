using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aspose.CAD;
using Aspose.CAD.FileFormats.Cad;
using Aspose.CAD.FileFormats.Cad.CadConsts;
using Aspose.CAD.FileFormats.Cad.CadObjects;
using MqttVision.Server.Domain;

namespace MqttVision.Server.Infrastructure.Cad;

public sealed record CadTextItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("block")] string Block,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

public sealed record CadConfigurationParseResult(
    CabinetConfiguration Configuration,
    IReadOnlyList<CadTextItem> ExtractedText,
    IReadOnlyList<string> Warnings,
    string ParserProfile);

public interface ICadConfigurationParser
{
    CadConfigurationParseResult Parse(
        string cadPath,
        string cabinetId,
        CadConfigurationSource source);
}

public sealed class AsposeCadConfigurationParser : ICadConfigurationParser
{
    private readonly ILogger<AsposeCadConfigurationParser> logger;

    public AsposeCadConfigurationParser(ILogger<AsposeCadConfigurationParser> logger)
    {
        this.logger = logger;
    }

    public CadConfigurationParseResult Parse(
        string cadPath,
        string cabinetId,
        CadConfigurationSource source)
    {
        var extracted = new List<CadTextItem>();
        using (var cadImage = (CadImage)Image.Load(cadPath))
        {
            if (cadImage.Entities is not null)
            {
                foreach (var entity in cadImage.Entities)
                {
                    ProcessEntity(entity, "*Model_Space", extracted);
                }
            }

            // Some drawings put text in a block without exposing an INSERT in the model-space list.
            // Keep it as evidence, but the table parser intentionally only consumes model-space text
            // so that a block definition is not counted once per definition and once per insertion.
            if (cadImage.BlockEntities is System.Collections.IDictionary blockEntities)
            {
                foreach (System.Collections.DictionaryEntry entry in blockEntities)
                {
                    if (entry.Value is not CadBlockEntity block)
                    {
                        continue;
                    }

                    if (block.Entities is null)
                    {
                        continue;
                    }

                    foreach (var entity in block.Entities)
                    {
                        ProcessEntity(entity, block.Name ?? string.Empty, extracted);
                    }
                }
            }
        }

        var deduplicated = extracted
            .GroupBy(item => new
            {
                Text = NormalizeText(item.Text),
                item.Layer,
                item.Block,
                X = Math.Round(item.X, 2),
                Y = Math.Round(item.Y, 2)
            })
            .Select(group => group.First())
            .ToArray();

        var parsed = CadTableExtractor.Parse(cabinetId, source, deduplicated);
        logger.LogInformation(
            "CAD text extracted. File={FileName}, TextCount={TextCount}, StripCount={StripCount}, WarningCount={WarningCount}",
            Path.GetFileName(cadPath),
            deduplicated.Length,
            parsed.Configuration.TerminalStrips.Count,
            parsed.Warnings.Count);
        return parsed with { ExtractedText = deduplicated };
    }

    private static void ProcessEntity(
        CadEntityBase entity,
        string blockName,
        ICollection<CadTextItem> extracted)
    {
        if (entity.TypeName == CadEntityTypeName.TEXT && entity is CadText text)
        {
            var value = NormalizeText(text.DefaultValue);
            if (value.Length == 0)
            {
                return;
            }

            var x = text.FirstAlignment?.X ?? text.SecondAlignmentPoint?.X ?? 0d;
            var y = text.FirstAlignment?.Y ?? text.SecondAlignmentPoint?.Y ?? 0d;
            extracted.Add(new CadTextItem("TEXT", value, text.LayerName ?? string.Empty, blockName, x, y));
            return;
        }

        if (entity.TypeName == CadEntityTypeName.MTEXT && entity is CadMText mtext)
        {
            var value = NormalizeText(mtext.FullClearText ?? mtext.Text);
            if (value.Length == 0)
            {
                return;
            }

            var x = mtext.InsertionPoint?.X ?? 0d;
            var y = mtext.InsertionPoint?.Y ?? 0d;
            extracted.Add(new CadTextItem("MTEXT", value, mtext.LayerName ?? string.Empty, blockName, x, y));
            return;
        }

        if (entity.TypeName == CadEntityTypeName.INSERT && entity is CadInsertObject insert && insert.ChildObjects is not null)
        {
            foreach (var child in insert.ChildObjects.OfType<CadEntityBase>())
            {
                ProcessEntity(child, blockName, extracted);
            }
        }
    }

    private static string NormalizeText(string? value) =>
        (value ?? string.Empty)
            .Replace("\\P", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
}

internal static class CadTableExtractor
{
    private const double CoordinateTolerance = 1.5;
    private static readonly Regex TerminalLabelPattern = new("^\\d+[a-z]?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CadConfigurationParseResult Parse(
        string cabinetId,
        CadConfigurationSource source,
        IReadOnlyList<CadTextItem> items)
    {
        var modelSpace = items
            .Where(IsModelSpace)
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();
        var warnings = new List<string>();
        var strips = new List<CabinetTerminalStripConfiguration>();
        var headers = modelSpace
            .Where(item => IsStripHeader(item.Text))
            .OrderByDescending(item => item.Y)
            .ThenBy(item => item.X)
            .ToArray();

        foreach (var header in headers)
        {
            if (string.Equals(header.Text, "3D", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("发现 3D 端子排，但当前版本只解析带端子/线号管的 1D 和 2D 端子排。");
                continue;
            }

            if (IsLikelyVertical(header, modelSpace))
            {
                var verticalRows = ParseVerticalRows(header, modelSpace, warnings);
                if (verticalRows.Count > 0)
                {
                    AddOrReplaceStrip(strips, CreateStrip(header.Text, "vertical", verticalRows));
                }

                continue;
            }

            var horizontalRows = ParseHorizontalRows(header, modelSpace, warnings);
            if (horizontalRows.Count > 0)
            {
                AddOrReplaceStrip(strips, CreateStrip(header.Text, "horizontal", horizontalRows));
            }
        }

        // The vertical and horizontal views are duplicate representations of the same list.
        // Prefer vertical rows because they include explicit blank terminals and the full destination column.
        var normalizedStrips = strips
            .GroupBy(strip => strip.StripCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(strip => strip.Terminals.Count)
                .ThenBy(strip => string.Equals(strip.Orientation, "vertical", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .First())
            .OrderBy(strip => strip.StripCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedStrips.Count == 0)
        {
            warnings.Add("未识别到包含端子号的 1D/2D 端子排。请确认 CAD 使用的是当前支持的端子表模板。");
        }

        var flattened = normalizedStrips
            .SelectMany(strip => strip.Terminals)
            .ToList();
        var configuration = new CabinetConfiguration
        {
            CabinetId = cabinetId,
            TerminalStartNumber = 1,
            Terminals = flattened,
            TerminalStrips = normalizedStrips,
            CadSource = source,
            ImportWarnings = warnings
        };

        return new CadConfigurationParseResult(configuration, items, warnings, "cad-terminal-table-v1");
    }

    private static List<CabinetTerminalConfiguration> ParseVerticalRows(
        CadTextItem header,
        IReadOnlyList<CadTextItem> modelSpace,
        ICollection<string> warnings)
    {
        var candidates = modelSpace
            .Where(item => item.Y < header.Y - 3 && Math.Abs(item.X - header.X) <= 42)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var columnWindows = GetVerticalColumnWindows(header.Text, header.X);
        var rows = Cluster(candidates, item => item.Y, CoordinateTolerance)
            .OrderByDescending(row => row.Average(item => item.Y))
            .ToArray();
        var result = new List<CabinetTerminalConfiguration>();
        var ordinal = 0;
        foreach (var row in rows)
        {
            var columns = columnWindows
                .Select(window => row
                    .Where(item => item.X >= window.Min && item.X <= window.Max)
                    .OrderBy(item => Math.Abs(item.X - window.Center))
                    .ThenByDescending(item => item.Text.Length)
                    .FirstOrDefault())
                .ToArray();
            var terminalText = columns.ElementAtOrDefault(1)?.Text.Trim();
            if (string.IsNullOrWhiteSpace(terminalText))
            {
                continue;
            }

            if (!IsTerminalLabel(terminalText))
            {
                warnings.Add($"端子排 {header.Text} 的中间列“{terminalText}”不是标准端子号，已保留为解析警告。");
                continue;
            }

            var isTwoDimensional = string.Equals(header.Text, "2D", StringComparison.OrdinalIgnoreCase);
            var right = isTwoDimensional ? columns.ElementAtOrDefault(3)?.Text : columns.ElementAtOrDefault(2)?.Text;
            var auxiliary = isTwoDimensional ? columns.ElementAtOrDefault(2)?.Text : null;
            var destination = isTwoDimensional
                ? null
                : columns.ElementAtOrDefault(3)?.Text;
            result.Add(CreateTerminal(
                header.Text,
                "vertical",
                ordinal++,
                terminalText,
                columns.ElementAtOrDefault(0)?.Text,
                right,
                auxiliary,
                destination,
                row.Average(item => item.X),
                row.Average(item => item.Y),
                isTwoDimensional));
        }

        return result;
    }

    private static List<CabinetTerminalConfiguration> ParseHorizontalRows(
        CadTextItem header,
        IReadOnlyList<CadTextItem> modelSpace,
        ICollection<string> warnings)
    {
        var nextHeaderX = modelSpace
            .Where(item => IsStripHeader(item.Text) && item.X > header.X + 20)
            .Select(item => item.X)
            .DefaultIfEmpty(header.X + 180)
            .Min();
        var candidates = modelSpace
            .Where(item => item.X > header.X - 45 && item.X < nextHeaderX - 5)
            .Where(item => item.Y < header.Y - 3 && item.Y >= header.Y - 500)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var terminalBand = candidates
            .Where(item => IsTerminalLabel(item.Text))
            .GroupBy(item => Math.Round(item.Y / CoordinateTolerance) * CoordinateTolerance)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (terminalBand is null)
        {
            return [];
        }

        var terminalY = terminalBand.Average(item => item.Y);
        var result = new List<CabinetTerminalConfiguration>();
        var ordinal = 0;
        foreach (var terminal in terminalBand.OrderBy(item => item.X))
        {
            var left = FindNearest(candidates, terminal.X - 15, terminalY, 5);
            var upper = FindNearest(candidates, terminal.X, terminalY + 7, 5);
            var destination = FindNearest(candidates, terminal.X + 18, terminalY, 5);
            var isTwoDimensional = string.Equals(header.Text, "2D", StringComparison.OrdinalIgnoreCase);
            result.Add(CreateTerminal(
                header.Text,
                "horizontal",
                ordinal++,
                terminal.Text,
                left?.Text,
                isTwoDimensional ? destination?.Text : upper?.Text,
                isTwoDimensional ? upper?.Text : null,
                isTwoDimensional ? destination?.Text : destination?.Text,
                terminal.X,
                terminal.Y,
                isTwoDimensional));
        }

        if (result.Count > 0)
        {
            warnings.Add($"端子排 {header.Text} 使用水平视图解析；若同一端子排存在垂直表，系统会优先采用垂直表。");
        }

        return result;
    }

    private static bool IsLikelyVertical(
        CadTextItem header,
        IReadOnlyList<CadTextItem> modelSpace)
    {
        var nearby = modelSpace
            .Where(item => item.Y < header.Y - 3 && Math.Abs(item.X - header.X) <= 42)
            .ToArray();
        if (nearby.Length == 0)
        {
            return false;
        }

        var xSpan = nearby.Max(item => item.X) - nearby.Min(item => item.X);
        var ySpan = nearby.Max(item => item.Y) - nearby.Min(item => item.Y);
        return ySpan > xSpan * 1.4;
    }

    private static CabinetTerminalConfiguration CreateTerminal(
        string stripCode,
        string orientation,
        int ordinal,
        string terminalLabel,
        string? left,
        string? right,
        string? auxiliary,
        string? destination,
        double x,
        double y,
        bool isTwoDimensional)
    {
        var leftValue = Normalize(left);
        var rightValue = Normalize(right);
        var markers = new List<string>();
        if (!string.IsNullOrWhiteSpace(leftValue))
        {
            markers.Add(leftValue);
        }

        if (!string.IsNullOrWhiteSpace(rightValue)
            && !markers.Contains(rightValue, StringComparer.OrdinalIgnoreCase))
        {
            markers.Add(rightValue);
        }
        var parsedNumber = int.TryParse(
            new string(terminalLabel.TakeWhile(char.IsDigit).ToArray()),
            out var number)
            ? number
            : 0;
        return new CabinetTerminalConfiguration
        {
            TerminalNumber = parsedNumber,
            TerminalLabel = terminalLabel,
            ExpectedWireMarker = markers.FirstOrDefault(),
            WireMarkers = markers,
            LeftWireMarker = leftValue,
            RightWireMarker = rightValue,
            AuxiliaryValue = Normalize(auxiliary),
            Destination = Normalize(destination),
            StripId = $"{stripCode.ToLowerInvariant()}-{orientation}",
            StripCode = stripCode,
            SourceOrdinal = ordinal,
            IsExpectedEmpty = markers.Count == 0,
            CadX = Math.Round(x, 3),
            CadY = Math.Round(y, 3),
            Note = isTwoDimensional ? "CAD 2D 端子排" : null
        };
    }

    private static CabinetTerminalStripConfiguration CreateStrip(
        string code,
        string orientation,
        IReadOnlyList<CabinetTerminalConfiguration> rows) =>
        new()
        {
            StripId = $"{code.ToLowerInvariant()}-{orientation}",
            StripCode = code,
            Orientation = orientation,
            Terminals = rows.ToList()
        };

    private static void AddOrReplaceStrip(
        ICollection<CabinetTerminalStripConfiguration> strips,
        CabinetTerminalStripConfiguration candidate)
    {
        var existing = strips.FirstOrDefault(strip =>
            string.Equals(strip.StripCode, candidate.StripCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(strip.Orientation, candidate.Orientation, StringComparison.OrdinalIgnoreCase));
        if (existing is null || candidate.Terminals.Count > existing.Terminals.Count)
        {
            if (existing is not null)
            {
                strips.Remove(existing);
            }

            strips.Add(candidate);
        }
    }

    private static IReadOnlyList<(double Min, double Max, double Center)> GetVerticalColumnWindows(
        string stripCode,
        double headerX)
    {
        // The reference CAD template places the header over the third column for both 1D and 2D.
        // Relative windows keep this profile independent of the absolute drawing origin.
        var isTwoDimensional = string.Equals(stripCode, "2D", StringComparison.OrdinalIgnoreCase);
        var offsets = isTwoDimensional
            ? new[] { (-30d, -8d), (-8d, -1d), (-1d, 7d), (7d, 20d) }
            : new[] { (-30d, -8d), (-8d, -1d), (-1d, 7d), (7d, 20d) };
        return offsets
            .Select(window => (headerX + window.Item1, headerX + window.Item2, headerX + (window.Item1 + window.Item2) / 2))
            .ToArray();
    }

    private static CadTextItem? FindNearest(
        IReadOnlyList<CadTextItem> items,
        double x,
        double y,
        double tolerance)
    {
        return items
            .Where(item => Math.Abs(item.X - x) <= tolerance && Math.Abs(item.Y - y) <= tolerance)
            .OrderBy(item => Math.Abs(item.X - x) + Math.Abs(item.Y - y))
            .ThenByDescending(item => item.Text.Length)
            .FirstOrDefault();
    }

    private static IReadOnlyList<List<CadTextItem>> Cluster(
        IReadOnlyList<CadTextItem> items,
        Func<CadTextItem, double> coordinate,
        double tolerance)
    {
        var groups = new List<List<CadTextItem>>();
        foreach (var item in items.OrderByDescending(coordinate))
        {
            var group = groups.FirstOrDefault(existing =>
                Math.Abs(coordinate(item) - existing.Average(coordinate)) <= tolerance);
            if (group is null)
            {
                groups.Add([item]);
            }
            else
            {
                group.Add(item);
            }
        }

        return groups;
    }

    private static bool IsModelSpace(CadTextItem item) =>
        string.Equals(item.Block, "*Model_Space", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.Block, "Model_Space", StringComparison.OrdinalIgnoreCase);

    private static bool IsStripHeader(string value) =>
        value.Trim() is "1D" or "2D" or "3D";

    private static bool IsTerminalLabel(string value) =>
        TerminalLabelPattern.IsMatch(value.Trim());

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
