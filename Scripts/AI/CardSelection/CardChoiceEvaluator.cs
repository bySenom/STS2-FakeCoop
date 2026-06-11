using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal sealed class CardChoiceEvaluator
{
    private readonly CardEvaluationContextFactory _contextFactory = new();
    private readonly AiBuildPreferenceEvaluator _buildPreferenceEvaluator = new();
    private readonly AiDeckSynergyAnalyzer _deckSynergyAnalyzer = new();

    public CardEvaluationContextFactory ContextFactory => _contextFactory;

    public CardChoiceDecision EvaluateCandidates(
        IEnumerable<CardModel> candidates,
        CardEvaluationContext context)
    {
        AiCardRewardTuning tuning = AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CardRewards;
        List<CardEvaluationResult> ranked = candidates
            .Select((card, index) => EvaluateCard(card, index, context))
            .OrderByDescending(static result => result.FinalScore)
            .ThenBy(result => result.Candidate.Name, StringComparer.Ordinal)
            .ToList();

        double skipThreshold = GetSkipThreshold(context, tuning);
        CardEvaluationResult? best = ranked.FirstOrDefault();
        string? skipReason = GetSkipReason(best, context, skipThreshold);
        bool shouldTake = best != null && (!context.SkipAllowed || skipReason == null);

        return new CardChoiceDecision
        {
            RankedResults = ranked,
            SkipThreshold = skipThreshold,
            ShouldTakeCard = shouldTake,
            ActiveBuildId = context.ActiveBuild?.Profile.BuildId ?? "none",
            ActiveBuildLocked = context.ActiveBuild?.IsLocked == true,
            SkipReason = skipReason
        };
    }

    private CardEvaluationResult EvaluateCard(CardModel cardModel, int index, CardEvaluationContext context)
    {
        AiCardRewardTuning tuning = AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CardRewards;
        ResolvedCardView card = _contextFactory.ResolveCandidate(cardModel, index);
        CardFeatureVector features = CardFeatureVector.From(card);

        double intrinsic = ScoreIntrinsic(card, features, tuning);
        double deckFit = ScoreDeckFit(card, features, context, tuning);
        double needs = ScoreDeckNeeds(card, features, context, tuning);
        double redundancy = ScoreRedundancy(card, features, context, tuning);
        double contextAdjustment = ScoreContext(card, features, context, tuning);
        AiBuildPreferenceResult buildPreference = _buildPreferenceEvaluator.Evaluate(card, context);
        AiDeckSynergyResult synergy = _deckSynergyAnalyzer.Evaluate(card, context);
        double final = intrinsic + deckFit + needs + contextAdjustment + buildPreference.Score + synergy.Score - redundancy;

        List<string> reasons = [];
        if (intrinsic > 0)
        {
            reasons.Add($"intrinsic +{intrinsic:F1}");
        }

        if (deckFit > 0)
        {
            reasons.Add($"fit +{deckFit:F1}");
        }

        if (needs > 0)
        {
            reasons.Add($"needs +{needs:F1}");
        }

        if (redundancy > 0)
        {
            reasons.Add($"redundancy -{redundancy:F1}");
        }

        if (contextAdjustment != 0)
        {
            reasons.Add($"context {(contextAdjustment > 0 ? "+" : string.Empty)}{contextAdjustment:F1}");
        }

        if (buildPreference.Score != 0)
        {
            reasons.Add($"build {(buildPreference.Score > 0 ? "+" : string.Empty)}{buildPreference.Score:F1}: {buildPreference.Reason}");
        }

        if (synergy.Score != 0)
        {
            string reason = synergy.Reasons.Count > 0
                ? string.Join(", ", synergy.Reasons.Take(3))
                : "deck synergy";
            reasons.Add($"synergy {(synergy.Score > 0 ? "+" : string.Empty)}{synergy.Score:F1}: {reason}");
        }

        return new CardEvaluationResult
        {
            CandidateCard = cardModel,
            Candidate = card,
            FinalScore = final,
            IntrinsicScore = intrinsic,
            DeckFitScore = deckFit,
            NeedCoverageScore = needs,
            RedundancyPenalty = redundancy,
            ContextAdjustmentScore = contextAdjustment,
            BuildPreferenceScore = buildPreference.Score,
            SynergyScore = synergy.Score,
            IsOffBuild = buildPreference.IsOffBuild,
            Reasons = reasons
        };
    }

    private static string? GetSkipReason(CardEvaluationResult? best, CardEvaluationContext context, double skipThreshold)
    {
        if (best == null)
        {
            return "no_candidates";
        }

        if (!context.SkipAllowed || context.ChoiceSource == CardChoiceSource.ForcedChoice)
        {
            return null;
        }

        if (best.FinalScore < skipThreshold)
        {
            return $"below_threshold score={best.FinalScore:F1}";
        }

        if (best.IsOffBuild &&
            context.ChoiceSource is CardChoiceSource.Reward or CardChoiceSource.Shop or CardChoiceSource.ChooseScreen &&
            best.FinalScore < skipThreshold + 12d)
        {
            return $"off_build score={best.FinalScore:F1} required={(skipThreshold + 12d):F1}";
        }

        return null;
    }

    private static double ScoreIntrinsic(ResolvedCardView card, CardFeatureVector features, AiCardRewardTuning tuning)
    {
        AiCardRewardIntrinsicWeights intrinsic = tuning.IntrinsicWeights;
        double score = 0d;
        score += features.Damage * intrinsic.DamageValuePerPoint;
        score += features.Block * intrinsic.BlockValuePerPoint;
        score += features.Draw * intrinsic.DrawValue;
        score += features.Energy * intrinsic.EnergyValue;
        score += features.Vulnerable * intrinsic.VulnerableValue;
        score += features.Weak * intrinsic.WeakValue;
        score += features.PersistentStrength * intrinsic.PersistentStrengthValue;
        score += features.PersistentDexterity * intrinsic.PersistentDexterityValue;
        score += features.TemporaryStrength * intrinsic.TemporaryStrengthValue;
        score += features.TemporaryDexterity * intrinsic.TemporaryDexterityValue;
        score += features.RepeatCount * intrinsic.RepeatValue;
        score += GetRarityBonus(card.Rarity, intrinsic);

        if (card.Type == CardType.Power)
        {
            score += intrinsic.PowerBonus;
        }

        if (card.EffectiveCost == 0)
        {
            score += intrinsic.ZeroCostBonus;
        }
        else if (card.EffectiveCost > 1)
        {
            score -= (card.EffectiveCost - 1) * intrinsic.HighCostPenaltyPerExtraEnergy;
        }

        if (card.Retain)
        {
            score += intrinsic.RetainBonus;
        }

        if (card.Exhaust)
        {
            score += score >= 18d ? intrinsic.GoodExhaustBonus : -intrinsic.BadExhaustPenalty;
        }

        if (card.Ethereal)
        {
            score -= intrinsic.EtherealPenalty;
        }

        if (features.TotalKnownValue <= 0 &&
            card.Type is CardType.Attack or CardType.Skill or CardType.Power)
        {
            score -= intrinsic.UnknownValuePenalty;
        }

        return score;
    }

    private static double ScoreDeckFit(ResolvedCardView card, CardFeatureVector features, CardEvaluationContext context, AiCardRewardTuning tuning)
    {
        AiCardRewardSynergyWeights synergy = tuning.SynergyWeights;
        DeckSummary deck = context.DeckSummary;
        double score = 0d;

        if (features.Draw > 0 && (deck.HighCostCards >= 4 || deck.AverageCost >= 1.35d))
        {
            score += features.Draw * synergy.DrawWithHighCurveValue;
        }

        if (features.Energy > 0 && (deck.HighCostCards >= 5 || deck.DrawSources >= 2))
        {
            score += features.Energy * synergy.EnergyWithHeavyCurveValue;
        }

        if (features.PersistentStrength > 0 || features.TemporaryStrength > 0 || features.Vulnerable > 0)
        {
            score += Math.Min(deck.AttackCount, 8) * synergy.AttackScalingSynergyPerAttack;
        }

        if (features.PersistentDexterity > 0 || features.TemporaryDexterity > 0)
        {
            score += Math.Min(deck.BlockSources, 8) * synergy.DefenseScalingSynergyPerBlockSource;
        }

        if (card.Type == CardType.Power && deck.DrawSources > 0)
        {
            score += synergy.PowerWithDrawBonus;
        }

        if (card.Retain && deck.HighCostCards > 0)
        {
            score += synergy.RetainWithHighCostBonus;
        }

        if (card.Exhaust && (features.Draw > 0 || features.Energy > 0 || features.TotalKnownValue >= 18))
        {
            score += synergy.ExhaustSynergyBonus;
        }

        return score;
    }

    private static double ScoreDeckNeeds(ResolvedCardView card, CardFeatureVector features, CardEvaluationContext context, AiCardRewardTuning tuning)
    {
        AiCardRewardSynergyWeights synergy = tuning.SynergyWeights;
        DeckSummary deck = context.DeckSummary;
        double score = 0d;

        if (deck.FrontloadDamageSources < DesiredDamageSources(deck))
        {
            score += Math.Min(features.Damage / synergy.DamageNeedScale, synergy.DamageNeedCap);
            score += features.Vulnerable * synergy.VulnerableNeedValue;
        }

        if (deck.BlockSources < DesiredBlockSources(deck))
        {
            score += Math.Min(features.Block / synergy.BlockNeedScale, synergy.BlockNeedCap);
            score += features.Weak * synergy.WeakNeedValue;
            score += features.PersistentDexterity * synergy.DexterityNeedValue;
        }

        if (deck.DrawSources < DesiredDrawSources(deck))
        {
            score += features.Draw * synergy.DrawNeedValue;
        }

        if (deck.EnergySources < DesiredEnergySources(deck))
        {
            score += features.Energy * synergy.EnergyNeedValue;
        }

        if (deck.ScalingSources < DesiredScalingSources(deck))
        {
            score += (features.PersistentStrength + features.PersistentDexterity) * synergy.ScalingNeedValue;
            if (card.Type == CardType.Power)
            {
                score += synergy.PowerScalingBonus;
            }
        }

        if (deck.CardCount <= 15 && card.EffectiveCost <= 1)
        {
            score += Math.Min(features.Damage + features.Block, synergy.EarlyCheapCardTempoCap) * synergy.EarlyCheapCardTempoScale;
        }

        return score;
    }

    private static double ScoreRedundancy(ResolvedCardView card, CardFeatureVector features, CardEvaluationContext context, AiCardRewardTuning tuning)
    {
        AiCardRewardDisciplineWeights discipline = tuning.DisciplineWeights;
        DeckSummary deck = context.DeckSummary;
        int copiesInDeck = context.DeckCards.Count(deckCard =>
            string.Equals(deckCard.CardId, card.CardId, StringComparison.Ordinal));

        double penalty = copiesInDeck * discipline.DuplicatePenaltyPerCopy;

        if (deck.DrawSources >= DesiredDrawSources(deck) + 1 && features.Draw > 0)
        {
            penalty += features.Draw * discipline.ExcessDrawPenalty;
        }

        if (deck.EnergySources >= DesiredEnergySources(deck) + 1 && features.Energy > 0)
        {
            penalty += features.Energy * discipline.ExcessEnergyPenalty;
        }

        if (deck.FrontloadDamageSources >= DesiredDamageSources(deck) + 2 && features.Damage > 0)
        {
            penalty += Math.Min(features.Damage / discipline.ExcessDamagePenaltyScale, discipline.ExcessDamagePenaltyCap);
        }

        if (deck.BlockSources >= DesiredBlockSources(deck) + 2 && features.Block > 0)
        {
            penalty += Math.Min(features.Block / discipline.ExcessBlockPenaltyScale, discipline.ExcessBlockPenaltyCap);
        }

        if (deck.ScalingSources >= DesiredScalingSources(deck) + 2 &&
            (features.PersistentStrength > 0 || features.PersistentDexterity > 0 || card.Type == CardType.Power))
        {
            penalty += discipline.ExcessScalingPenalty;
        }

        if (deck.PowerCount >= 5 && card.Type == CardType.Power)
        {
            penalty += discipline.ExcessPowerPenalty;
        }

        if (card.Ethereal && card.EffectiveCost >= 2)
        {
            penalty += discipline.EtherealHighCostPenalty;
        }

        return penalty;
    }

    private static double ScoreContext(ResolvedCardView card, CardFeatureVector features, CardEvaluationContext context, AiCardRewardTuning tuning)
    {
        AiCardRewardDisciplineWeights discipline = tuning.DisciplineWeights;
        AiCardRewardSynergyWeights synergy = tuning.SynergyWeights;
        double score = context.ChoiceSource switch
        {
            CardChoiceSource.Reward => discipline.RewardContextBonus,
            CardChoiceSource.ChooseScreen => discipline.ChooseScreenContextBonus,
            CardChoiceSource.Event => discipline.EventContextBonus,
            CardChoiceSource.Shop => ScoreShopContext(context, tuning),
            _ => 0d
        };

        if (context.CurrentActIndex == 0 && context.TotalFloor <= 10)
        {
            score += Math.Min(features.Damage + features.Block, synergy.EarlyActTempoCap) * synergy.EarlyActTempoScale;
        }

        if (context.AscensionLevel >= 10 && features.Block > 0)
        {
            score += synergy.HighAscensionBlockBonus;
        }

        score += ScoreSilentContext(card, features, context);
        score += ScoreDefectContext(card, features, context);
        return score;
    }

    private static double ScoreSilentContext(ResolvedCardView card, CardFeatureVector features, CardEvaluationContext context)
    {
        if (!string.Equals(AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CharacterId, "silent", StringComparison.OrdinalIgnoreCase))
        {
            return 0d;
        }

        double score = 0d;
        bool earlyAct = context.CurrentActIndex == 0 && context.TotalFloor <= 10;
        if (HasToken(card, "MASTERPLANNER"))
        {
            score += 24d;
        }

        if (HasToken(card, "SLY", "ACROBATICS", "PREPARED", "REFLEX", "TACTICIAN", "CALCULATEDGAMBLE", "CONCENTRATE", "DISCARD"))
        {
            score += 16d + features.Draw * 3d + features.Energy * 4d;
        }

        if (HasToken(card, "NOXIOUS", "DEADLYPOISON", "ACCELERANT", "BOUNCINGFLASK"))
        {
            score += earlyAct ? 22d : 16d;
        }

        if (HasToken(card, "CATALYST", "BURST") && context.DeckCards.Any(deckCard => HasToken(deckCard, "POISON", "NOXIOUS", "DEADLY", "BOUNCING")))
        {
            score += 18d;
        }

        if (HasToken(card, "NEUTRALIZE"))
        {
            score += earlyAct ? 14d : 8d;
        }

        if (HasToken(card, "FOOTWORK", "LEGSWEEP", "BACKFLIP"))
        {
            score += context.DeckSummary.BlockSources < 7 ? 16d : 8d;
        }

        if (HasToken(card, "ACCURACY"))
        {
            int shivCards = context.DeckCards.Count(deckCard => HasToken(deckCard, "BLADEDANCE", "CLOAKANDDAGGER", "SHIV"));
            score += shivCards > 0 ? 22d : 10d;
        }

        if (HasToken(card, "BLADEDANCE", "CLOAKANDDAGGER", "SHIV", "FANOFKNIVES"))
        {
            score += earlyAct ? 14d : 8d;
        }

        return score;
    }

    private static double ScoreDefectContext(ResolvedCardView card, CardFeatureVector features, CardEvaluationContext context)
    {
        if (!string.Equals(AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CharacterId, "defect", StringComparison.OrdinalIgnoreCase))
        {
            return 0d;
        }

        double score = 0d;
        bool earlyAct = context.CurrentActIndex == 0 && context.TotalFloor <= 10;
        bool hasOrbEvidence = context.DeckCards.Any(static c =>
            HasToken(c, "ZAP", "BALLLIGHTNING", "COLDSNAP", "GLACIER", "DARKNESS"));

        if (HasToken(card, "DEFRA", "FOCUS"))
        {
            score += 28d;
            score += hasOrbEvidence ? 16d : 0d;
        }

        if (HasToken(card, "ZAP", "BALLLIGHTNING", "LIGHTNING"))
        {
            score += earlyAct ? 22d : 14d;
        }

        if (HasToken(card, "COLDSNAP", "FROST", "GLACIER", "CHILL", "COOLHEADED"))
        {
            score += earlyAct ? 18d : 12d;
        }

        if (HasToken(card, "DARKNESS", "DARK"))
        {
            score += 14d;
            score += context.DeckCards.Count(static c => HasToken(c, "DUALCAST", "MULTICAST", "RECURSION")) > 0 ? 12d : 0d;
        }

        if (HasToken(card, "CAPACITOR"))
        {
            score += 20d;
            score += hasOrbEvidence ? 14d : 0d;
        }

        if (HasToken(card, "ECHOFORM"))
        {
            score += 30d;
        }

        if (HasToken(card, "ELECTRODYNAMICS"))
        {
            int lightningCards = context.DeckCards.Count(static c => HasToken(c, "ZAP", "BALLLIGHTNING", "LIGHTNING", "STATIC"));
            score += 22d + Math.Min(lightningCards, 6) * 4d;
        }

        if (HasToken(card, "CLAW"))
        {
            int clawCount = context.DeckCards.Count(static c => HasToken(c, "CLAW"));
            score += 18d + clawCount * 6d;
        }

        if (HasToken(card, "ALLFORONE", "SCRAPE"))
        {
            int zeroCostCount = context.DeckCards.Count(static c => c.EffectiveCost == 0);
            score += 14d + Math.Min(zeroCostCount, 8) * 3d;
        }

        if (HasToken(card, "DUALCAST", "MULTICAST"))
        {
            score += hasOrbEvidence ? 18d : 8d;
        }

        if (HasToken(card, "TURBO"))
        {
            score += features.Energy * 4d;
            score += earlyAct ? 14d : 8d;
        }

        return score;
    }

    private static bool HasToken(ResolvedCardView card, params string[] tokens)
    {
        string normalizedName = AiBuildProfileAnalyzer.Normalize(card.Name);
        string normalizedId = AiBuildProfileAnalyzer.Normalize(card.CardId);
        return tokens.Any(token =>
        {
            string normalizedToken = AiBuildProfileAnalyzer.Normalize(token);
            return normalizedName.Contains(normalizedToken, StringComparison.Ordinal) ||
                   normalizedId.Contains(normalizedToken, StringComparison.Ordinal);
        });
    }

    private static double ScoreShopContext(CardEvaluationContext context, AiCardRewardTuning tuning)
    {
        AiCardRewardDisciplineWeights discipline = tuning.DisciplineWeights;
        if (!context.CandidateGoldCost.HasValue)
        {
            return -discipline.ShopMissingCostPenalty;
        }

        double gold = Math.Max(context.Gold, 1);
        double cost = context.CandidateGoldCost.Value;
        return -(cost / Math.Max(gold, 50d)) * discipline.ShopCostRatioPenaltyScale;
    }

    private static double GetSkipThreshold(CardEvaluationContext context, AiCardRewardTuning tuning)
    {
        AiCardRewardDisciplineWeights discipline = tuning.DisciplineWeights;
        if (!context.SkipAllowed || context.ChoiceSource == CardChoiceSource.ForcedChoice)
        {
            return double.NegativeInfinity;
        }

        return context.ChoiceSource switch
        {
            CardChoiceSource.Reward => discipline.RewardSkipThreshold,
            CardChoiceSource.ChooseScreen => discipline.ChooseScreenSkipThreshold,
            CardChoiceSource.Event => discipline.EventSkipThreshold,
            CardChoiceSource.Shop => discipline.ShopSkipThresholdBase + (context.CandidateGoldCost ?? 0) * discipline.ShopSkipThresholdCostFactor,
            _ => discipline.EventSkipThreshold
        };
    }

    private static int DesiredDamageSources(DeckSummary deck)
    {
        return deck.CardCount < 15 ? 6 : 8;
    }

    private static int DesiredBlockSources(DeckSummary deck)
    {
        return deck.CardCount < 15 ? 5 : 7;
    }

    private static int DesiredDrawSources(DeckSummary deck)
    {
        return deck.CardCount < 18 ? 2 : 3;
    }

    private static int DesiredEnergySources(DeckSummary deck)
    {
        return deck.CardCount < 18 ? 1 : 2;
    }

    private static int DesiredScalingSources(DeckSummary deck)
    {
        return deck.CardCount < 15 ? 1 : 2;
    }

    private static double GetRarityBonus(string rarity, AiCardRewardIntrinsicWeights intrinsic)
    {
        return rarity switch
        {
            "Rare" => intrinsic.RareBonus,
            "Uncommon" => intrinsic.UncommonBonus,
            "Common" => 0d,
            "Basic" => intrinsic.BasicBonus,
            "Curse" => intrinsic.CursePenalty,
            "Status" => intrinsic.StatusPenalty,
            "Quest" => intrinsic.QuestPenalty,
            "Event" => intrinsic.EventBonus,
            "Ancient" => intrinsic.AncientBonus,
            _ => 0d
        };
    }

    private readonly record struct CardFeatureVector(
        int Damage,
        int Block,
        int Draw,
        int Energy,
        int Vulnerable,
        int Weak,
        int PersistentStrength,
        int PersistentDexterity,
        int TemporaryStrength,
        int TemporaryDexterity,
        int RepeatCount)
    {
        public int TotalKnownValue =>
            Damage +
            Block +
            (Draw * 4) +
            (Energy * 5) +
            (Vulnerable * 3) +
            (Weak * 3) +
            (PersistentStrength * 3) +
            (PersistentDexterity * 3) +
            (TemporaryStrength * 2) +
            (TemporaryDexterity * 2);

        public static CardFeatureVector From(ResolvedCardView card)
        {
            int temporaryStrength = card.GetSelfTemporaryStrengthAmount();
            int temporaryDexterity = card.GetSelfTemporaryDexterityAmount();

            return new CardFeatureVector(
                card.GetEstimatedDamage(),
                card.GetEstimatedBlock(),
                card.GetCardsDrawn(),
                card.GetEnergyGain(),
                card.GetEnemyVulnerableAmount(),
                card.GetEnemyWeakAmount(),
                Math.Max(0, card.GetSelfStrengthAmount() - temporaryStrength),
                Math.Max(0, card.GetSelfDexterityAmount() - temporaryDexterity),
                temporaryStrength,
                temporaryDexterity,
                Math.Max(card.ReplayCount, 1));
        }
    }
}
