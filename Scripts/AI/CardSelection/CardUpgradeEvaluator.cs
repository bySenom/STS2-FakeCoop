using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal sealed class CardUpgradeEvaluator
{
    private readonly CardEvaluationContextFactory _contextFactory = new();
    private readonly AiBuildPreferenceEvaluator _buildPreferenceEvaluator = new();
    private readonly AiDeckSynergyAnalyzer _deckSynergyAnalyzer = new();

    public CardUpgradeDecision Evaluate(Player player, IEnumerable<CardModel> candidates)
    {
        List<CardModel> candidateList = candidates.Where(static card => card.IsUpgradable).ToList();
        CardEvaluationContext context = _contextFactory.Create(
            player,
            CardChoiceSource.ForcedChoice,
            skipAllowed: false,
            debugSource: "upgrade_choice");

        List<CardUpgradeEvaluationResult> ranked = candidateList
            .Select((card, index) => EvaluateCard(card, index, context, player))
            .OrderByDescending(static result => result.Score)
            .ThenBy(result => result.Name, StringComparer.Ordinal)
            .ToList();

        return new CardUpgradeDecision
        {
            RankedResults = ranked,
            SelectedCards = ranked.Select(static result => result.Card).ToList()
        };
    }

    private CardUpgradeEvaluationResult EvaluateCard(
        CardModel card,
        int index,
        CardEvaluationContext context,
        Player player)
    {
        ResolvedCardView resolved = _contextFactory.ResolveCandidate(card, index);
        AiEventTuning tuning = AiCharacterCombatConfigLoader.LoadForPlayer(player).Events;
        AiBuildPreferenceResult buildPreference = _buildPreferenceEvaluator.Evaluate(resolved, context);
        AiDeckSynergyResult synergy = _deckSynergyAnalyzer.Evaluate(resolved, context);

        double score = 0d;
        List<string> reasons = [];
        if (!CardCatalogRepository.Shared.TryGet(card.Id.Entry, out CardCatalogEntry? entry) || entry == null)
        {
            score += 8d;
            reasons.Add("missing catalog entry fallback +8.0");
        }
        else
        {
            score += EvaluateUpgradeSpec(entry.UpgradeSpec, reasons, tuning);
            if (entry.Type == CardType.Power)
            {
                score += tuning.OutcomeWeights.UpgradePowerCardBonus;
                reasons.Add($"power upgrade bias +{tuning.OutcomeWeights.UpgradePowerCardBonus:F1}");
            }

            if (entry.Rarity == "Basic")
            {
                double basicAdjustment = buildPreference.IsBuildRelevant ? 1d : -4d;
                score += basicAdjustment;
                reasons.Add($"basic card adjustment {(basicAdjustment >= 0 ? "+" : string.Empty)}{basicAdjustment:F1}");
            }
        }

        if (buildPreference.Score != 0)
        {
            double buildUpgradeScore = buildPreference.IsOffBuild
                ? buildPreference.Score * 2d
                : buildPreference.Score * 1.4d;
            score += buildUpgradeScore;
            reasons.Add($"build upgrade {(buildUpgradeScore >= 0 ? "+" : string.Empty)}{buildUpgradeScore:F1}: {buildPreference.Reason}");
        }

        if (buildPreference.IsOffBuild)
        {
            score -= 12d;
            reasons.Add("off-build upgrade caution -12.0");
        }

        if (synergy.Score != 0)
        {
            double synergyUpgradeScore = synergy.Score * 0.75d;
            score += synergyUpgradeScore;
            string reason = synergy.Reasons.Count > 0
                ? string.Join(", ", synergy.Reasons.Take(3))
                : "deck synergy";
            reasons.Add($"synergy upgrade {(synergyUpgradeScore >= 0 ? "+" : string.Empty)}{synergyUpgradeScore:F1}: {reason}");
        }

        if (resolved.Type == CardType.Power || resolved.GetSelfStrengthAmount() > 0 || resolved.GetSelfDexterityAmount() > 0)
        {
            score += 3d;
            reasons.Add("scaling upgrade bias +3.0");
        }

        if (resolved.Rarity is "Curse" or "Status")
        {
            score -= 40d;
            reasons.Add("do not upgrade burden cards -40.0");
        }

        return new CardUpgradeEvaluationResult
        {
            Card = card,
            CardId = card.Id.Entry,
            Name = card.Title?.ToString() ?? card.Id.Entry,
            Score = score,
            BuildPreferenceScore = buildPreference.Score,
            SynergyScore = synergy.Score,
            IsOffBuild = buildPreference.IsOffBuild,
            Reasons = reasons
        };
    }

    private static double EvaluateUpgradeSpec(CardUpgradeSpec spec, List<string> reasons, AiEventTuning tuning)
    {
        double score = tuning.OutcomeWeights.UpgradeSpecBaseValue;
        if (spec.CostOverride.HasValue)
        {
            double bonus = Math.Max(0, 2 - spec.CostOverride.Value) * tuning.OutcomeWeights.UpgradeCostOverrideValuePerEnergy;
            score += bonus;
            reasons.Add($"costOverride->{spec.CostOverride.Value} +{bonus:F1}");
        }
        else if (spec.CostDelta < 0)
        {
            double bonus = Math.Abs(spec.CostDelta) * tuning.OutcomeWeights.UpgradeCostReductionValuePerEnergy;
            score += bonus;
            reasons.Add($"costReduction +{bonus:F1}");
        }

        int positiveEffectValue = spec.EffectAmountAdjustments.Values.Sum(static value => Math.Max(0, value));
        if (positiveEffectValue > 0)
        {
            double bonus = positiveEffectValue * tuning.OutcomeWeights.UpgradePositiveEffectValuePerPoint;
            score += bonus;
            reasons.Add($"positiveEffectDelta +{bonus:F1}");
        }

        if (spec.Retain == true)
        {
            score += tuning.OutcomeWeights.UpgradeRetainBonus;
            reasons.Add($"gains retain +{tuning.OutcomeWeights.UpgradeRetainBonus:F1}");
        }

        if (spec.Exhaust == false)
        {
            score += tuning.OutcomeWeights.UpgradeRemoveExhaustBonus;
            reasons.Add($"removes exhaust +{tuning.OutcomeWeights.UpgradeRemoveExhaustBonus:F1}");
        }

        if (spec.Ethereal == false)
        {
            score += tuning.OutcomeWeights.UpgradeRemoveEtherealBonus;
            reasons.Add($"removes ethereal +{tuning.OutcomeWeights.UpgradeRemoveEtherealBonus:F1}");
        }

        if (spec.ReplayCountOverride.HasValue && spec.ReplayCountOverride.Value > 1)
        {
            score += tuning.OutcomeWeights.UpgradeReplayIncreaseBonus;
            reasons.Add($"replay increase +{tuning.OutcomeWeights.UpgradeReplayIncreaseBonus:F1}");
        }

        reasons.Add($"upgradeSpecScore={score:F1}");
        return score;
    }
}

internal sealed class CardUpgradeDecision
{
    public required IReadOnlyList<CardUpgradeEvaluationResult> RankedResults { get; init; }

    public required IReadOnlyList<CardModel> SelectedCards { get; init; }
}

internal sealed class CardUpgradeEvaluationResult
{
    public required CardModel Card { get; init; }

    public required string CardId { get; init; }

    public required string Name { get; init; }

    public required double Score { get; init; }

    public required double BuildPreferenceScore { get; init; }

    public required double SynergyScore { get; init; }

    public required bool IsOffBuild { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];

    public string Describe()
    {
        return $"card={CardId} name={Name} score={Score:F1} build={BuildPreferenceScore:F1} synergy={SynergyScore:F1} offBuild={IsOffBuild} reasons=[{string.Join("; ", Reasons)}]";
    }
}
