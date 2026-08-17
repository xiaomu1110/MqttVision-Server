using MqttVision.Server.Domain;

namespace MqttVision.Server.Application.Configuration;

public sealed class VisualConfigurationMatcher
{
    private const double AmbiguousScoreMargin = 3.0d;

    public ConfigurationLocationResult Match(
        CabinetConfigurationIndex index,
        IReadOnlyList<ConfigurationMarkerObservation> observations,
        string? preferredCabinetId = null)
    {
        var normalizedObservations = observations
            .Select(observation => new NormalizedObservation(
                observation,
                ConfigurationMarkerNormalizer.Normalize(observation.Text)))
            .ToArray();
        var usable = normalizedObservations
            .Where(observation => observation.NormalizedText is not null)
            .ToArray();
        var evidence = normalizedObservations
            .Select(observation => CreateUnmatchedEvidence(observation, "未提供可用于索引的线号管编号。"))
            .ToList();
        var rounds = new List<ConfigurationLocationRound>(3);

        if (usable.Length == 0)
        {
            AddSkippedRounds(rounds);
            return new ConfigurationLocationResult
            {
                Status = "unresolved",
                Strategy = "marker-index-no-usable-observation",
                ObservedMarkerCount = 0,
                Rounds = rounds,
                Evidence = evidence
            };
        }

        var accumulators = new Dictionary<LocationKey, LocationAccumulator>();
        var pending = OrderCenterOut(usable);
        var roundDefinitions = new[]
        {
            new MatchRound(1, "exact-normalized", "exact", observation => index.Find(observation.NormalizedText)),
            new MatchRound(2, "separator-insensitive", "variant", observation => index.FindLoose(observation.NormalizedText)),
            new MatchRound(3, "bounded-fuzzy", "fuzzy", observation => index.FindFuzzy(observation.NormalizedText))
        };

        foreach (var roundDefinition in roundDefinitions)
        {
            var input = pending;
            var nextPending = new List<(NormalizedObservation Observation, int Rank)>();
            var matchedObservationCount = 0;
            var roundCandidateKeys = new HashSet<LocationKey>(LocationKeyComparer.Instance);

            foreach (var (observation, rank) in input)
            {
                var occurrences = roundDefinition.Find(observation);
                var occurrenceGroups = occurrences
                    .GroupBy(occurrence => new LocationKey(
                        occurrence.Configuration.CabinetId,
                        occurrence.Strip.StripId),
                        LocationKeyComparer.Instance)
                    .ToArray();
                if (occurrenceGroups.Length == 0)
                {
                    nextPending.Add((observation, rank));
                    continue;
                }

                matchedObservationCount++;
                foreach (var group in occurrenceGroups)
                {
                    roundCandidateKeys.Add(group.Key);
                }

                var evidenceIndex = evidence.FindIndex(item => item.DetectionId == observation.Source.DetectionId);
                evidence[evidenceIndex] = new ConfigurationLocationEvidence
                {
                    DetectionId = observation.Source.DetectionId,
                    ObservedText = observation.Source.Text,
                    NormalizedText = observation.NormalizedText,
                    Matched = true,
                    Round = roundDefinition.Round,
                    MatchMethod = roundDefinition.Method,
                    CandidateCount = occurrenceGroups.Length,
                    Reason = roundDefinition.Method switch
                    {
                        "exact" => $"第 {roundDefinition.Round} 轮精确命中 {occurrences.Count} 条配置记录。",
                        "variant" => $"第 {roundDefinition.Round} 轮忽略分隔符后命中 {occurrences.Count} 条配置记录。",
                        _ => $"第 {roundDefinition.Round} 轮容错检索命中 {occurrences.Count} 条近似配置记录。"
                    }
                };

                foreach (var group in occurrenceGroups)
                {
                    var occurrence = group.First();
                    if (!accumulators.TryGetValue(group.Key, out var accumulator))
                    {
                        accumulator = new LocationAccumulator(occurrence.Configuration, occurrence.Strip);
                        accumulators[group.Key] = accumulator;
                    }

                    accumulator.AddMatch(
                        observation.NormalizedText!,
                        observation.Source.DetectionId,
                        observation.Source.Confidence,
                        group.Count(),
                        rank,
                        roundDefinition.Method);
                }
            }

            rounds.Add(new ConfigurationLocationRound
            {
                Round = roundDefinition.Round,
                Name = roundDefinition.Name,
                InputCount = input.Count,
                MatchedObservationCount = matchedObservationCount,
                CandidateCount = roundCandidateKeys.Count,
                Result = matchedObservationCount > 0 ? "matched" : "no-match"
            });
            pending = nextPending;
        }

        if (accumulators.Count == 0)
        {
            foreach (var (observation, _) in pending)
            {
                var evidenceIndex = evidence.FindIndex(item => item.DetectionId == observation.Source.DetectionId);
                evidence[evidenceIndex] = new ConfigurationLocationEvidence
                {
                    DetectionId = observation.Source.DetectionId,
                    ObservedText = observation.Source.Text,
                    NormalizedText = observation.NormalizedText,
                    Matched = false,
                    Round = 3,
                    MatchMethod = "fuzzy",
                    CandidateCount = 0,
                    Reason = "已完成精确、分隔符容错和有限距离三轮检索，仍未找到对应 CAD 配置。"
                };
            }

            return new ConfigurationLocationResult
            {
                Status = "no-configuration",
                Strategy = "marker-index-three-round-no-match",
                ObservedMarkerCount = usable.Length,
                Rounds = rounds,
                Evidence = evidence
            };
        }

        var candidates = accumulators.Values
            .Select(accumulator => accumulator.ToCandidate(preferredCabinetId))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.DistinctMatchedMarkerCount)
            .ThenBy(candidate => candidate.CabinetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.StripId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var best = candidates[0];
        var second = candidates.ElementAtOrDefault(1);
        var scoreMargin = second is null ? best.Score : best.Score - second.Score;
        var hasCrossValidation = best.DistinctMatchedMarkerCount >= 2;
        var fuzzyOnly = best.ExactMatchCount == 0 && best.VariantMatchCount == 0;
        var fuzzyEvidenceReliable = best.FuzzyMatchCount >= 2 && hasCrossValidation;
        var unambiguous = second is null || scoreMargin >= AmbiguousScoreMargin || hasCrossValidation;
        var status = fuzzyOnly && !fuzzyEvidenceReliable
            ? "no-configuration"
            : unambiguous ? "matched" : "ambiguous";
        var confidence = CalculateConfidence(best, second, usable.Length, status == "matched");

        if (status == "no-configuration")
        {
            foreach (var evidenceItem in evidence.Where(item => item.Matched && item.MatchMethod == "fuzzy").ToList())
            {
                var evidenceIndex = evidence.FindIndex(item => item.DetectionId == evidenceItem.DetectionId);
                evidence[evidenceIndex] = new ConfigurationLocationEvidence
                {
                    DetectionId = evidenceItem.DetectionId,
                    ObservedText = evidenceItem.ObservedText,
                    NormalizedText = evidenceItem.NormalizedText,
                    Matched = false,
                    Round = evidenceItem.Round,
                    MatchMethod = evidenceItem.MatchMethod,
                    CandidateCount = evidenceItem.CandidateCount,
                    Reason = "有限距离容错仅命中不足两个独立线号管，未达到可靠定位门槛。"
                };
            }
        }

        return new ConfigurationLocationResult
        {
            Status = status,
            Strategy = status == "no-configuration"
                ? "marker-index-fuzzy-insufficient-evidence"
                : "marker-index-center-out-vote",
            CabinetId = status == "matched" ? best.CabinetId : null,
            StripId = status == "matched" ? best.StripId : null,
            StripCode = status == "matched" ? best.StripCode : null,
            ObservedMarkerCount = usable.Length,
            MatchedMarkerCount = best.MatchedMarkerCount,
            DistinctMatchedMarkerCount = best.DistinctMatchedMarkerCount,
            Score = best.Score,
            Confidence = confidence,
            Candidates = candidates.Take(8).ToList(),
            Rounds = rounds,
            Evidence = evidence
        };
    }

    private static double CalculateConfidence(
        ConfigurationLocationCandidate best,
        ConfigurationLocationCandidate? second,
        int observedCount,
        bool unambiguous)
    {
        var coverage = Math.Clamp((double)best.DistinctMatchedMarkerCount / Math.Max(1, Math.Min(observedCount, 3)), 0d, 1d);
        var margin = second is null
            ? 1d
            : Math.Clamp((best.Score - second.Score) / Math.Max(best.Score, 1d), 0d, 1d);
        var ocrConfidence = best.Confidence > 0d ? best.Confidence : 0.6d;
        var confidence = coverage * 0.45d + margin * 0.35d + ocrConfidence * 0.2d;
        return Math.Round(unambiguous ? confidence : confidence * 0.55d, 4);
    }

    private static IReadOnlyList<(NormalizedObservation Observation, int Rank)> OrderCenterOut(
        IReadOnlyList<NormalizedObservation> observations)
    {
        var centerX = Median(observations.Select(observation => observation.Source.X));
        var centerY = Median(observations.Select(observation => observation.Source.Y));
        return observations
            .OrderBy(observation => Math.Abs(observation.Source.X - centerX) + Math.Abs(observation.Source.Y - centerY))
            .ThenBy(observation => observation.Source.DetectionId)
            .Select((observation, rank) => (observation, rank))
            .ToArray();
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0d;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }

    private static ConfigurationLocationEvidence CreateUnmatchedEvidence(
        NormalizedObservation observation,
        string reason) =>
        new()
        {
            DetectionId = observation.Source.DetectionId,
            ObservedText = observation.Source.Text,
            NormalizedText = observation.NormalizedText,
            Matched = false,
            CandidateCount = 0,
            Reason = reason
        };

    private static void AddSkippedRounds(ICollection<ConfigurationLocationRound> rounds)
    {
        rounds.Add(new ConfigurationLocationRound
        {
            Round = 1,
            Name = "exact-normalized",
            Result = "skipped-no-observation"
        });
        rounds.Add(new ConfigurationLocationRound
        {
            Round = 2,
            Name = "separator-insensitive",
            Result = "skipped-no-observation"
        });
        rounds.Add(new ConfigurationLocationRound
        {
            Round = 3,
            Name = "bounded-fuzzy",
            Result = "skipped-no-observation"
        });
    }

    private sealed record NormalizedObservation(
        ConfigurationMarkerObservation Source,
        string? NormalizedText);

    private sealed record MatchRound(
        int Round,
        string Name,
        string Method,
        Func<NormalizedObservation, IReadOnlyList<ConfigurationMarkerOccurrence>> Find);

    private sealed class LocationAccumulator
    {
        private readonly HashSet<string> matchedMarkers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> matchedDetectionIds = [];
        private double ocrConfidenceTotal;
        private int ocrConfidenceCount;
        private double score;
        private int occurrenceCount;
        private int exactMatchCount;
        private int variantMatchCount;
        private int fuzzyMatchCount;

        public LocationAccumulator(CabinetConfiguration configuration, CabinetTerminalStripConfiguration strip)
        {
            Configuration = configuration;
            Strip = strip;
        }

        public CabinetConfiguration Configuration { get; }

        public CabinetTerminalStripConfiguration Strip { get; }

        public void AddMatch(
            string marker,
            int detectionId,
            double? ocrConfidence,
            int occurrenceCount,
            int rank,
            string method)
        {
            if (!matchedDetectionIds.Add(detectionId))
            {
                return;
            }

            matchedMarkers.Add(marker);
            this.occurrenceCount += occurrenceCount;
            var uniquenessBonus = occurrenceCount == 1 ? 4d : 1d;
            var centerOutWeight = Math.Max(0.55d, 1d - rank * 0.08d);
            var confidenceWeight = ocrConfidence is null
                ? 0.7d
                : 0.5d + Math.Clamp(ocrConfidence.Value, 0d, 1d) * 0.5d;
            var methodWeight = method switch
            {
                "exact" => 14d,
                "variant" => 9d,
                _ => 4d
            };
            score += (methodWeight + uniquenessBonus) * centerOutWeight * confidenceWeight;
            switch (method)
            {
                case "exact":
                    exactMatchCount++;
                    break;
                case "variant":
                    variantMatchCount++;
                    break;
                default:
                    fuzzyMatchCount++;
                    break;
            }

            if (ocrConfidence is not null)
            {
                ocrConfidenceTotal += Math.Clamp(ocrConfidence.Value, 0d, 1d);
                ocrConfidenceCount++;
            }
        }

        public ConfigurationLocationCandidate ToCandidate(string? preferredCabinetId)
        {
            var preferredBonus = string.Equals(Configuration.CabinetId, preferredCabinetId, StringComparison.OrdinalIgnoreCase)
                ? 1d
                : 0d;
            var finalScore = score + preferredBonus;
            return new ConfigurationLocationCandidate
            {
                CabinetId = Configuration.CabinetId,
                StripId = Strip.StripId,
                StripCode = Strip.StripCode,
                MatchedMarkerCount = matchedDetectionIds.Count,
                DistinctMatchedMarkerCount = matchedMarkers.Count,
                OccurrenceCount = occurrenceCount,
                ExactMatchCount = exactMatchCount,
                VariantMatchCount = variantMatchCount,
                FuzzyMatchCount = fuzzyMatchCount,
                Score = Math.Round(finalScore, 4),
                Confidence = ocrConfidenceCount == 0
                    ? 0d
                    : Math.Round(ocrConfidenceTotal / ocrConfidenceCount, 4),
                MatchedMarkers = matchedMarkers.OrderBy(marker => marker, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
    }

    private readonly record struct LocationKey(string CabinetId, string StripId);

    private sealed class LocationKeyComparer : IEqualityComparer<LocationKey>
    {
        public static LocationKeyComparer Instance { get; } = new();

        public bool Equals(LocationKey x, LocationKey y) =>
            string.Equals(x.CabinetId, y.CabinetId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.StripId, y.StripId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(LocationKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CabinetId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.StripId));
    }
}
