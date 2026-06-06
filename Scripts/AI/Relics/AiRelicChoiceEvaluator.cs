using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal sealed class AiRelicChoiceEvaluator
{
    private readonly CardEvaluationContextFactory _contextFactory = new();

    public AiRelicChoiceDecision Evaluate(Player player, IReadOnlyList<RelicModel> relics)
    {
        CardEvaluationContext context = _contextFactory.Create(
            player,
            CardChoiceSource.ForcedChoice,
            skipAllowed: false,
            debugSource: "relic_choice");

        List<AiRelicEvaluationResult> ranked = relics
            .Select(relic => EvaluateRelic(relic, context))
            .OrderByDescending(static result => result.Score)
            .ThenBy(result => result.Name, StringComparer.Ordinal)
            .ToList();

        return new AiRelicChoiceDecision
        {
            RankedResults = ranked,
            SelectedRelic = ranked.FirstOrDefault()?.Relic
        };
    }

    private static AiRelicEvaluationResult EvaluateRelic(RelicModel relic, CardEvaluationContext context)
    {
        string relicId = relic.Id.Entry.ToUpperInvariant();
        string name = relic.Title?.ToString() ?? relic.Id.Entry;
        DeckSummary deck = context.DeckSummary;
        List<string> reasons = [];
        double score = GetRarityBaseline(relic.Rarity.ToString());
        reasons.Add($"rarityBaseline={score:F1}");
        AddBuildProfileRelicScore(context, relicId, reasons, ref score);

        AddPatternBonus("DEAD_BRANCH", deck.ExhaustCards >= 2, 18d, "exhaust deck synergy");
        AddPatternBonus("SHURIKEN", deck.AttackCount >= 7 || HasDeckCard(context, "BLADE", "SHIV"), 15d, "attack spam scaling");
        AddPatternBonus("KUNAI", deck.AttackCount >= 7 || HasDeckCard(context, "BLADE", "SHIV"), 14d, "attack spam defense scaling");
        AddPatternBonus("INSERTER", HasOrbDeckEvidence(context), 14d, "orb slot scaling");
        AddPatternBonus("DATA_DISK", HasOrbDeckEvidence(context), 12d, "focus improves orb deck");
        AddPatternBonus("STRIKE_DUMMY", CountDeckCards(context, "STRIKE") >= 3, 12d, "starter strike payoff");
        AddPatternBonus("SNECKO", deck.HighCostCards >= 5 && deck.AverageCost >= 1.4d, 13d, "high-cost deck discount potential");
        AddPatternBonus("BAG_OF_PREPARATION", true, 10d, "opening draw consistency");
        AddPatternBonus("ANCHOR", deck.BlockSources < 7, 8d, "early block consistency");
        AddPatternBonus("ORICHALCUM", deck.BlockSources < 7, 8d, "passive defense floor");
        AddPatternBonus("VAJRA", deck.AttackCount >= 5, 7d, "passive attack scaling");
        AddPatternBonus("PANTOGRAPH", true, 7d, "boss sustain value");
        AddPatternBonus("MEMBERSHIP", true, 8d, "future shop discount");
        AddPatternBonus("COURIER", true, 8d, "future shop flexibility");

        if (context.RelicIds.Contains(relicId))
        {
            score -= 20d;
            reasons.Add("already owned penalty -20.0");
        }

        return new AiRelicEvaluationResult
        {
            Relic = relic,
            RelicId = relic.Id.Entry,
            Name = name,
            Score = score,
            Reasons = reasons
        };

        void AddPatternBonus(string pattern, bool condition, double bonus, string reason)
        {
            if (condition && relicId.Contains(pattern, StringComparison.Ordinal))
            {
                score += bonus;
                reasons.Add($"{reason} +{bonus:F1}");
            }
        }
    }

    private static bool HasOrbDeckEvidence(CardEvaluationContext context)
    {
        return HasDeckCard(context, "ZAP", "DEFRA", "GLACIER", "COOLHEADED", "DARKNESS", "ELECTRODYNAMICS", "CAPACITOR");
    }

    private static void AddBuildProfileRelicScore(
        CardEvaluationContext context,
        string relicId,
        List<string> reasons,
        ref double score)
    {
        AiBuildArchetype? active = SelectActiveBuild(context);
        if (active == null)
        {
            return;
        }

        double multiplier = IsBuildLocked(active, context.DeckCards) ? 1d : 0.55d;
        if (MatchesAny(active.KeyRelics, relicId))
        {
            double bonus = 24d * multiplier;
            score += bonus;
            reasons.Add($"{active.DisplayName} key relic +{bonus:F1}");
        }

        if (MatchesAny(active.GoodRelics, relicId))
        {
            double bonus = 12d * multiplier;
            score += bonus;
            reasons.Add($"{active.DisplayName} good relic +{bonus:F1}");
        }

        if (MatchesAny(active.AvoidRelics, relicId))
        {
            double penalty = 18d * multiplier;
            score -= penalty;
            reasons.Add($"{active.DisplayName} avoid relic -{penalty:F1}");
        }
    }

    private static AiBuildArchetype? SelectActiveBuild(CardEvaluationContext context)
    {
        return AiBuildArchetypeCatalog.ForCharacter(AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CharacterId)
            .Select(profile => new
            {
                Profile = profile,
                Score = ScoreProfile(profile, context.DeckCards),
                Locked = IsBuildLocked(profile, context.DeckCards)
            })
            .Where(static candidate => candidate.Score > TierBonus(candidate.Profile.Tier))
            .OrderByDescending(static candidate => candidate.Locked)
            .ThenByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => TierBonus(candidate.Profile.Tier))
            .ThenBy(static candidate => candidate.Profile.DisplayName, StringComparer.Ordinal)
            .Select(static candidate => candidate.Profile)
            .FirstOrDefault();
    }

    private static bool IsBuildLocked(AiBuildArchetype profile, IReadOnlyList<ResolvedCardView> deckCards)
    {
        int coreMatches = deckCards.Count(card => MatchesAny(profile.CoreCards, card.CardId, card.Name));
        return coreMatches >= profile.LockCoreCardCount || ScoreProfile(profile, deckCards) >= profile.LockEvidenceScore;
    }

    private static double ScoreProfile(AiBuildArchetype profile, IReadOnlyList<ResolvedCardView> deckCards)
    {
        int coreMatches = deckCards.Count(card => MatchesAny(profile.CoreCards, card.CardId, card.Name));
        int supportMatches = deckCards.Count(card => MatchesAny(profile.SupportCards, card.CardId, card.Name));
        int avoidMatches = deckCards.Count(card => MatchesAny(profile.AvoidCards, card.CardId, card.Name));
        return TierBonus(profile.Tier) + (coreMatches * 14d) + (supportMatches * 6d) - (avoidMatches * 10d);
    }

    private static bool MatchesAny(IReadOnlyList<string> tokens, params string[] values)
    {
        return tokens.Any(token => values.Any(value => Normalize(value).Contains(Normalize(token), StringComparison.Ordinal)));
    }

    private static bool HasDeckCard(CardEvaluationContext context, params string[] tokens)
    {
        return context.DeckCards.Any(card => tokens.Any(token =>
            card.CardId.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            card.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static int CountDeckCards(CardEvaluationContext context, string token)
    {
        return context.DeckCards.Count(card =>
            card.CardId.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            card.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static double GetRarityBaseline(string rarity)
    {
        return rarity switch
        {
            "Ancient" => 28d,
            "Rare" => 21d,
            "Uncommon" => 15d,
            "Common" => 10d,
            _ => 8d
        };
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
}

internal sealed class AiRelicChoiceDecision
{
    public required IReadOnlyList<AiRelicEvaluationResult> RankedResults { get; init; }

    public required RelicModel? SelectedRelic { get; init; }
}

internal sealed class AiRelicEvaluationResult
{
    public required RelicModel Relic { get; init; }

    public required string RelicId { get; init; }

    public required string Name { get; init; }

    public required double Score { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];

    public string Describe()
    {
        return $"relic={RelicId} name={Name} score={Score:F1} reasons=[{string.Join("; ", Reasons)}]";
    }
}
