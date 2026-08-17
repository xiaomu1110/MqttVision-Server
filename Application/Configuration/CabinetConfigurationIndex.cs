using MqttVision.Server.Domain;

namespace MqttVision.Server.Application.Configuration;

public sealed record ConfigurationMarkerOccurrence(
    string NormalizedMarker,
    string Side,
    CabinetConfiguration Configuration,
    CabinetTerminalStripConfiguration Strip,
    CabinetTerminalConfiguration Terminal);

public sealed class CabinetConfigurationIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ConfigurationMarkerOccurrence>> occurrencesByMarker;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ConfigurationMarkerOccurrence>> looseOccurrencesByMarker;

    private CabinetConfigurationIndex(
        IReadOnlyList<CabinetConfiguration> configurations,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigurationMarkerOccurrence>> occurrencesByMarker,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigurationMarkerOccurrence>> looseOccurrencesByMarker)
    {
        Configurations = configurations;
        this.occurrencesByMarker = occurrencesByMarker;
        this.looseOccurrencesByMarker = looseOccurrencesByMarker;
    }

    public IReadOnlyList<CabinetConfiguration> Configurations { get; }

    public static CabinetConfigurationIndex Build(IEnumerable<CabinetConfiguration> configurations)
    {
        var uniqueConfigurations = configurations
            .Where(configuration => configuration is not null)
            .GroupBy(configuration => configuration.CabinetId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var byMarker = new Dictionary<string, List<ConfigurationMarkerOccurrence>>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuration in uniqueConfigurations)
        {
            foreach (var strip in GetStrips(configuration))
            {
                foreach (var terminal in strip.Terminals)
                {
                    AddOccurrence(byMarker, configuration, strip, terminal, "left", terminal.LeftWireMarker);
                    AddOccurrence(byMarker, configuration, strip, terminal, "right", terminal.RightWireMarker);

                    if (string.IsNullOrWhiteSpace(terminal.LeftWireMarker) &&
                        string.IsNullOrWhiteSpace(terminal.RightWireMarker))
                    {
                        foreach (var marker in terminal.WireMarkers.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            AddOccurrence(byMarker, configuration, strip, terminal, "wire", marker);
                        }
                    }
                }
            }
        }

        var frozen = byMarker.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ConfigurationMarkerOccurrence>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var loose = new Dictionary<string, List<ConfigurationMarkerOccurrence>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in byMarker)
        {
            var looseMarker = ConfigurationMarkerNormalizer.NormalizeLoose(pair.Key);
            if (looseMarker is null)
            {
                continue;
            }

            if (!loose.TryGetValue(looseMarker, out var occurrences))
            {
                occurrences = [];
                loose[looseMarker] = occurrences;
            }

            foreach (var occurrence in pair.Value)
            {
                if (!occurrences.Any(existing => SameOccurrence(existing, occurrence)))
                {
                    occurrences.Add(occurrence);
                }
            }
        }

        var frozenLoose = loose.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ConfigurationMarkerOccurrence>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return new CabinetConfigurationIndex(uniqueConfigurations, frozen, frozenLoose);
    }

    public IReadOnlyList<ConfigurationMarkerOccurrence> Find(string? marker)
    {
        var normalized = ConfigurationMarkerNormalizer.Normalize(marker);
        return normalized is not null && occurrencesByMarker.TryGetValue(normalized, out var occurrences)
            ? occurrences
            : Array.Empty<ConfigurationMarkerOccurrence>();
    }

    public IReadOnlyList<ConfigurationMarkerOccurrence> FindLoose(string? marker)
    {
        var normalized = ConfigurationMarkerNormalizer.NormalizeLoose(marker);
        return normalized is not null && looseOccurrencesByMarker.TryGetValue(normalized, out var occurrences)
            ? occurrences
            : Array.Empty<ConfigurationMarkerOccurrence>();
    }

    public IReadOnlyList<ConfigurationMarkerOccurrence> FindFuzzy(string? marker, int maxDistance = 1)
    {
        var normalized = ConfigurationMarkerNormalizer.NormalizeLoose(marker);
        if (normalized is null || normalized.Length < 4)
        {
            return Array.Empty<ConfigurationMarkerOccurrence>();
        }

        var results = new List<ConfigurationMarkerOccurrence>();
        foreach (var pair in looseOccurrencesByMarker)
        {
            if (Math.Abs(pair.Key.Length - normalized.Length) > maxDistance ||
                Math.Abs(pair.Key.Count(char.IsDigit) - normalized.Count(char.IsDigit)) > maxDistance ||
                Math.Abs(pair.Key.Count(char.IsLetter) - normalized.Count(char.IsLetter)) > maxDistance)
            {
                continue;
            }

            if (LevenshteinDistance(normalized, pair.Key) > maxDistance)
            {
                continue;
            }

            foreach (var occurrence in pair.Value)
            {
                if (!results.Any(existing => SameOccurrence(existing, occurrence)))
                {
                    results.Add(occurrence);
                }
            }
        }

        return results;
    }

    private static bool SameOccurrence(
        ConfigurationMarkerOccurrence left,
        ConfigurationMarkerOccurrence right) =>
        left.Configuration.CabinetId.Equals(right.Configuration.CabinetId, StringComparison.OrdinalIgnoreCase) &&
        left.Strip.StripId.Equals(right.Strip.StripId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Terminal.TerminalLabel, right.Terminal.TerminalLabel, StringComparison.OrdinalIgnoreCase) &&
        left.Side.Equals(right.Side, StringComparison.OrdinalIgnoreCase);

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void AddOccurrence(
        IDictionary<string, List<ConfigurationMarkerOccurrence>> byMarker,
        CabinetConfiguration configuration,
        CabinetTerminalStripConfiguration strip,
        CabinetTerminalConfiguration terminal,
        string side,
        string? marker)
    {
        var normalized = ConfigurationMarkerNormalizer.Normalize(marker);
        if (normalized is null)
        {
            return;
        }

        if (!byMarker.TryGetValue(normalized, out var occurrences))
        {
            occurrences = [];
            byMarker[normalized] = occurrences;
        }

        if (occurrences.Any(occurrence =>
                occurrence.Configuration.CabinetId.Equals(configuration.CabinetId, StringComparison.OrdinalIgnoreCase) &&
                occurrence.Strip.StripId.Equals(strip.StripId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(occurrence.Terminal.TerminalLabel, terminal.TerminalLabel, StringComparison.OrdinalIgnoreCase) &&
                occurrence.Side.Equals(side, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        occurrences.Add(new ConfigurationMarkerOccurrence(normalized, side, configuration, strip, terminal));
    }

    private static IReadOnlyList<CabinetTerminalStripConfiguration> GetStrips(CabinetConfiguration configuration)
    {
        if (configuration.TerminalStrips.Count > 0)
        {
            return configuration.TerminalStrips;
        }

        return
        [
            new CabinetTerminalStripConfiguration
            {
                StripId = $"{configuration.CabinetId}-default",
                StripCode = "default",
                Orientation = "unknown",
                Terminals = configuration.Terminals
            }
        ];
    }
}
