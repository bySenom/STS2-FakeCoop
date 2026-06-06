using System;
using System.Collections.Generic;
using System.Linq;

namespace AITeammate.Scripts;

internal sealed class AiBuildPreferenceEvaluator
{
    private const double ActiveBuildCoreBonus = 22d;
    private const double ActiveBuildSupportBonus = 10d;
    private const double EarlyCoreSignalBonus = 12d;
    private const double PivotBonus = 9d;
    private const double OffBuildPenalty = 8d;

    public AiBuildPreferenceResult Evaluate(ResolvedCardView card, CardEvaluationContext context)
    {
        IReadOnlyList<AiBuildArchetype> archetypes = AiBuildArchetypeCatalog.ForCharacter(
            AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CharacterId);
        if (archetypes.Count == 0)
        {
            return AiBuildPreferenceResult.Empty;
        }

        List<AiBuildAffinity> affinities = archetypes
            .Select(archetype => ScoreAffinity(archetype, context.DeckCards))
            .OrderByDescending(static affinity => affinity.Score)
            .ThenBy(static affinity => affinity.Archetype.DisplayName, StringComparer.Ordinal)
            .ToList();

        AiBuildAffinity? active = SelectActiveBuild(affinities);
        AiBuildArchetype? candidateBuild = archetypes
            .Where(archetype => IsCardRelevantToBuild(archetype, card))
            .OrderByDescending(archetype => ScoreCandidateSignal(archetype, card, context.DeckCards))
            .ThenByDescending(archetype => TierBonus(archetype.Tier))
            .ThenBy(archetype => archetype.DisplayName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (candidateBuild == null)
        {
            if (active == null || !active.IsLocked || context.DeckSummary.CardCount <= 10)
            {
                return AiBuildPreferenceResult.Empty;
            }

            return new AiBuildPreferenceResult
            {
                Score = -OffBuildPenalty,
                Reason = $"off-build vs {active.Archetype.DisplayName} -{OffBuildPenalty:F1}",
                ActiveBuildName = active.Archetype.DisplayName,
                IsBuildRelevant = false,
                IsOffBuild = true
            };
        }

        double score = 0d;
        List<string> reasons = [];
        bool candidateIsCore = ContainsCard(candidateBuild.CoreCards, card);
        bool candidateIsSupport = ContainsCard(candidateBuild.SupportCards, card);
        bool candidateIsAvoid = ContainsCard(candidateBuild.AvoidCards, card);

        if (candidateIsAvoid)
        {
            score -= OffBuildPenalty * 2d;
            reasons.Add($"{candidateBuild.DisplayName} avoid card -{(OffBuildPenalty * 2d):F1}");
        }
        else if (active != null && string.Equals(active.Archetype.BuildId, candidateBuild.BuildId, StringComparison.Ordinal))
        {
            score += candidateIsCore ? ActiveBuildCoreBonus : ActiveBuildSupportBonus;
            if (active.IsLocked)
            {
                score += 4d;
                reasons.Add($"{candidateBuild.DisplayName} locked build fit +{score:F1}");
            }
            else
            {
                reasons.Add($"{candidateBuild.DisplayName} tentative build fit +{score:F1}");
            }
        }
        else if (active == null)
        {
            double openerBonus = EarlyCoreSignalBonus + TierBonus(candidateBuild.Tier);
            score += openerBonus;
            reasons.Add($"{candidateBuild.DisplayName} build signal +{openerBonus:F1}");
        }
        else if (!active.IsLocked)
        {
            double candidateSignal = ScoreAffinity(candidateBuild, context.DeckCards.Concat([card]).ToList()).Score;
            double flexibleBonus = Math.Max(4d, candidateSignal - active.Score + TierBonus(candidateBuild.Tier));
            score += flexibleBonus;
            reasons.Add($"unlocked pivot candidate {candidateBuild.DisplayName} +{flexibleBonus:F1}");
        }
        else
        {
            double currentScore = active.Score;
            double pivotScore = ScoreAffinity(candidateBuild, context.DeckCards.Concat([card]).ToList()).Score;
            if (pivotScore >= currentScore + 12d)
            {
                score += PivotBonus + TierBonus(candidateBuild.Tier);
                reasons.Add($"pivot to {candidateBuild.DisplayName} +{score:F1}");
            }
            else if (candidateIsCore)
            {
                score += Math.Max(2d, TierBonus(candidateBuild.Tier));
                reasons.Add($"secondary {candidateBuild.DisplayName} core +{score:F1}");
            }
            else
            {
                score -= OffBuildPenalty;
                reasons.Add($"off-build vs {active.Archetype.DisplayName} -{OffBuildPenalty:F1}");
            }
        }

        if (candidateIsCore && !DeckHasCard(context.DeckCards, card))
        {
            score += 10d;
            reasons.Add("missing core card +10.0");
        }

        if (active != null && context.DeckSummary.CardCount >= active.Archetype.DesiredMaxDeckSize && !candidateIsCore)
        {
            double deckSizePenalty = active.IsLocked ? 8d : 4d;
            score -= deckSizePenalty;
            reasons.Add($"{active.Archetype.DisplayName} deck already large -{deckSizePenalty:F1}");
        }

        return new AiBuildPreferenceResult
        {
            Score = score,
            Reason = string.Join(", ", reasons),
            ActiveBuildName = active?.Archetype.DisplayName ?? candidateBuild.DisplayName,
            IsBuildRelevant = score > 0,
            IsOffBuild = score < 0
        };
    }

    private static AiBuildAffinity ScoreAffinity(AiBuildArchetype archetype, IReadOnlyList<ResolvedCardView> deckCards)
    {
        int coreMatches = deckCards.Count(card => ContainsCard(archetype.CoreCards, card));
        int supportMatches = deckCards.Count(card => ContainsCard(archetype.SupportCards, card));
        int avoidMatches = deckCards.Count(card => ContainsCard(archetype.AvoidCards, card));
        double score = TierBonus(archetype.Tier) + (coreMatches * 14d) + (supportMatches * 6d) - (avoidMatches * 10d);
        bool isLocked = coreMatches >= archetype.LockCoreCardCount || score >= archetype.LockEvidenceScore;
        return new AiBuildAffinity(archetype, score, coreMatches + supportMatches, coreMatches, supportMatches, isLocked);
    }

    private static double ScoreCandidateSignal(
        AiBuildArchetype archetype,
        ResolvedCardView card,
        IReadOnlyList<ResolvedCardView> deckCards)
    {
        double signal = TierBonus(archetype.Tier);
        if (ContainsCard(archetype.CoreCards, card))
        {
            signal += DeckHasCard(deckCards, card) ? 9d : 18d;
        }

        if (ContainsCard(archetype.SupportCards, card))
        {
            signal += 7d;
        }

        if (ContainsCard(archetype.AvoidCards, card))
        {
            signal -= 18d;
        }

        return signal;
    }

    private static AiBuildAffinity? SelectActiveBuild(IReadOnlyList<AiBuildAffinity> affinities)
    {
        return affinities
            .Where(static affinity => affinity.EvidenceCards > 0)
            .OrderByDescending(static affinity => affinity.IsLocked)
            .ThenByDescending(static affinity => affinity.Score)
            .ThenByDescending(static affinity => TierBonus(affinity.Archetype.Tier))
            .ThenBy(static affinity => affinity.Archetype.DisplayName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsCardRelevantToBuild(AiBuildArchetype archetype, ResolvedCardView card)
    {
        return ContainsCard(archetype.CoreCards, card) ||
               ContainsCard(archetype.SupportCards, card) ||
               ContainsCard(archetype.AvoidCards, card);
    }

    private static bool DeckHasCard(IReadOnlyList<ResolvedCardView> deckCards, ResolvedCardView candidate)
    {
        return deckCards.Any(deckCard => string.Equals(deckCard.CardId, candidate.CardId, StringComparison.Ordinal));
    }

    private static bool ContainsCard(IReadOnlyList<string> cardNames, ResolvedCardView card)
    {
        string cardName = Normalize(card.Name);
        string cardId = Normalize(card.CardId);
        return cardNames.Any(name =>
        {
            string token = Normalize(name);
            return cardName.Contains(token, StringComparison.Ordinal) ||
                   cardId.Contains(token, StringComparison.Ordinal);
        });
    }

    private static double TierBonus(AiBuildTier tier)
    {
        return tier switch
        {
            AiBuildTier.S => 10d,
            AiBuildTier.A => 4d,
            AiBuildTier.B => 2d,
            _ => 0d
        };
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private sealed class AiBuildAffinity(
        AiBuildArchetype archetype,
        double score,
        int evidenceCards,
        int coreMatches,
        int supportMatches,
        bool isLocked)
    {
        public AiBuildArchetype Archetype { get; } = archetype;

        public double Score { get; } = score;

        public int EvidenceCards { get; } = evidenceCards;

        public int CoreMatches { get; } = coreMatches;

        public int SupportMatches { get; } = supportMatches;

        public bool IsLocked { get; } = isLocked;
    }
}

internal sealed class AiBuildPreferenceResult
{
    public static readonly AiBuildPreferenceResult Empty = new()
    {
        Score = 0d,
        Reason = string.Empty,
        ActiveBuildName = null,
        IsBuildRelevant = false,
        IsOffBuild = false
    };

    public required double Score { get; init; }

    public required string Reason { get; init; }

    public required string? ActiveBuildName { get; init; }

    public required bool IsBuildRelevant { get; init; }

    public required bool IsOffBuild { get; init; }
}
