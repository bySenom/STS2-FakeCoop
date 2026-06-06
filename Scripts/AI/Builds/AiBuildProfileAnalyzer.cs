using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AITeammate.Scripts;

internal static class AiBuildProfileAnalyzer
{
    public static AiBuildProfileMatch? SelectActiveProfile(Player player, IReadOnlyList<ResolvedCardView> deckCards)
    {
        return AiBuildArchetypeCatalog.ForCharacter(AiCharacterCombatConfigLoader.LoadForPlayer(player).CharacterId)
            .Select(profile => ScoreProfile(profile, deckCards))
            .Where(static match => match.EvidenceCards > 0)
            .OrderByDescending(static match => match.IsLocked)
            .ThenByDescending(static match => match.Score)
            .ThenByDescending(static match => TierBonus(match.Profile.Tier))
            .ThenBy(static match => match.Profile.DisplayName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static AiBuildProfileMatch ScoreProfile(AiBuildArchetype profile, IReadOnlyList<ResolvedCardView> deckCards)
    {
        int coreMatches = deckCards.Count(card => IsCoreCard(profile, card));
        int supportMatches = deckCards.Count(card => IsSupportCard(profile, card));
        int avoidMatches = deckCards.Count(card => IsAvoidCard(profile, card));
        double score = TierBonus(profile.Tier) + (coreMatches * 14d) + (supportMatches * 6d) - (avoidMatches * 10d);
        bool isLocked = coreMatches >= profile.LockCoreCardCount || score >= profile.LockEvidenceScore;
        return new AiBuildProfileMatch
        {
            Profile = profile,
            Score = score,
            EvidenceCards = coreMatches + supportMatches,
            CoreMatches = coreMatches,
            SupportMatches = supportMatches,
            AvoidMatches = avoidMatches,
            IsLocked = isLocked
        };
    }

    public static bool IsCoreCard(AiBuildArchetype profile, ResolvedCardView card)
    {
        return MatchesAny(profile.CoreCards, card.CardId, card.Name);
    }

    public static bool IsSupportCard(AiBuildArchetype profile, ResolvedCardView card)
    {
        return MatchesAny(profile.SupportCards, card.CardId, card.Name);
    }

    public static bool IsAvoidCard(AiBuildArchetype profile, ResolvedCardView card)
    {
        return MatchesAny(profile.AvoidCards, card.CardId, card.Name);
    }

    public static bool IsKeyRelic(AiBuildArchetype profile, string relicId)
    {
        return MatchesAny(profile.KeyRelics, relicId);
    }

    public static bool IsGoodRelic(AiBuildArchetype profile, string relicId)
    {
        return MatchesAny(profile.GoodRelics, relicId);
    }

    public static bool IsAvoidRelic(AiBuildArchetype profile, string relicId)
    {
        return MatchesAny(profile.AvoidRelics, relicId);
    }

    public static bool MatchesAny(IReadOnlyList<string> tokens, params string[] values)
    {
        return tokens.Any(token => values.Any(value => Normalize(value).Contains(Normalize(token), StringComparison.Ordinal)));
    }

    public static double TierBonus(AiBuildTier tier)
    {
        return tier switch
        {
            AiBuildTier.S => 10d,
            AiBuildTier.A => 4d,
            AiBuildTier.B => 2d,
            _ => 0d
        };
    }

    public static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}

internal sealed class AiBuildProfileMatch
{
    public required AiBuildArchetype Profile { get; init; }

    public required double Score { get; init; }

    public required int EvidenceCards { get; init; }

    public required int CoreMatches { get; init; }

    public required int SupportMatches { get; init; }

    public required int AvoidMatches { get; init; }

    public required bool IsLocked { get; init; }
}

