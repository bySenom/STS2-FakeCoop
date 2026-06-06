using System;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal sealed class CombatActionScorer
{
    private const int RedundantBlockOnlyScore = -100000;

    public CombatActionScore Score(DeterministicCombatContext context, AiLegalActionOption action)
    {
        AiCharacterCombatTuning tuning = context.CombatConfig.Combat;
        AiCombatRiskProfile risk = tuning.RiskProfile;

        if (string.Equals(action.ActionType, AiTeammateActionKind.EndTurn.ToString(), StringComparison.Ordinal))
        {
            return new CombatActionScore
            {
                ActionId = action.ActionId,
                Category = CombatActionCategory.EndTurn,
                TotalScore = ScoreEndTurn(context)
            };
        }

        if (string.Equals(action.ActionType, AiTeammateActionKind.UsePotion.ToString(), StringComparison.Ordinal))
        {
            return new CombatActionScore
            {
                ActionId = action.ActionId,
                Category = CombatActionCategory.Potion,
                TotalScore = ScorePotion(context, action)
            };
        }

        ResolvedCardView? card = ResolveCard(context, action);
        if (card == null)
        {
            return new CombatActionScore
            {
                ActionId = action.ActionId,
                Category = CombatActionCategory.Utility,
                TotalScore = ScoreUtility(context, action)
            };
        }

        if (IsRedundantBlockOnly(context, card))
        {
            Log.Debug($"[AITeammate] Semantic score blocked redundant block-only actionId={action.ActionId} card={card.CardId} incoming={context.IncomingDamage} handDamage={context.HandEndTurnDamage} handHpLoss={context.HandEndTurnHpLoss} currentBlock={context.CurrentBlock}.");
            return new CombatActionScore
            {
                ActionId = action.ActionId,
                Category = CombatActionCategory.Block,
                TotalScore = RedundantBlockOnlyScore
            };
        }

        if (card.HasXCost && context.Energy <= 0)
        {
            Log.Debug($"[AITeammate] Semantic score blocked zero-energy X-cost actionId={action.ActionId} card={card.CardId}.");
            return new CombatActionScore
            {
                ActionId = action.ActionId,
                Category = CombatActionCategory.Utility,
                TotalScore = -100000
            };
        }

        int immediateDamageScore = ScoreImmediateDamage(context, action, card);
        int immediateDefenseScore = ScoreImmediateDefense(context, action, card);
        int enemyDebuffScore = ScoreEnemyDebuff(context, action, card);
        int selfBuffScore = ScoreSelfBuff(context, action, card);
        int resourceSetupScore = ScoreResourceSetup(context, action, card);
        int killPotentialScore = ScoreKillPotential(context, action, card);
        int buildCombatFitScore = ScoreBuildCombatFit(context, card);
        int totalScore = risk.ApplyAttackWeight(immediateDamageScore) +
                         risk.ApplyDefenseWeight(immediateDefenseScore) +
                         enemyDebuffScore +
                         selfBuffScore +
                         resourceSetupScore +
                         killPotentialScore +
                         ScoreEnergyEfficiency(context, action, card) +
                         buildCombatFitScore;

        CombatActionCategory category = Classify(card, immediateDamageScore, immediateDefenseScore, selfBuffScore, resourceSetupScore);
        Log.Debug(
            $"[AITeammate] Semantic score actionId={action.ActionId} category={category} damage={immediateDamageScore} defense={immediateDefenseScore} debuff={enemyDebuffScore} buff={selfBuffScore} setup={resourceSetupScore} kill={killPotentialScore} build={buildCombatFitScore} total={totalScore}");

        return new CombatActionScore
        {
            ActionId = action.ActionId,
            Category = category,
            TotalScore = totalScore
        };
    }

    private static CombatActionCategory Classify(
        ResolvedCardView card,
        int immediateDamageScore,
        int immediateDefenseScore,
        int selfBuffScore,
        int resourceSetupScore)
    {
        if (immediateDefenseScore >= Math.Max(immediateDamageScore, selfBuffScore) && card.HasEffect(EffectKind.GainBlock))
        {
            return CombatActionCategory.Block;
        }

        if (immediateDamageScore > 0 && (card.HasEffect(EffectKind.DealDamage) || card.Type == CardType.Attack))
        {
            return CombatActionCategory.Attack;
        }

        if (selfBuffScore >= resourceSetupScore && card.Type == CardType.Power)
        {
            return CombatActionCategory.PowerSetup;
        }

        if (resourceSetupScore > 0)
        {
            return CombatActionCategory.Utility;
        }

        return CombatActionCategory.Utility;
    }

    private static ResolvedCardView? ResolveCard(DeterministicCombatContext context, AiLegalActionOption action)
    {
        if (!string.IsNullOrEmpty(action.CardInstanceId) &&
            context.HandCardsByInstanceId.TryGetValue(action.CardInstanceId, out ResolvedCardView? liveCard))
        {
            return liveCard;
        }

        return null;
    }

    private static int ScoreImmediateDamage(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatCoreWeights core = context.CombatConfig.Combat.CoreWeights;
        AiCombatStatusWeights status = context.CombatConfig.Combat.StatusWeights;
        int damage = card.GetEstimatedDamage();
        if (damage <= 0)
        {
            return 0;
        }

        int score = damage * core.DirectDamageValuePerPoint;
        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock);
        if (uncoveredDamage > 0 && HasPlayableBlockAction(context))
        {
            score -= core.AttackWhileDefenseNeededPenalty;
        }

        if (!string.IsNullOrEmpty(action.TargetId) &&
            context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            int effectiveHp = enemy.CurrentHp + enemy.Block;
            score += Math.Max(0, core.TargetLowHealthBiasThreshold - effectiveHp) * core.TargetLowHealthBiasValuePerPoint;
            if (enemy.IsAttacking)
            {
                score += core.AttackingTargetBonus;
            }
        }

        score += GetActorPowerAmount(context, "STRENGTH") * Math.Max(1, GetDamageHits(card)) * status.StrengthPerHitValue;
        score += card.GetSelfTemporaryStrengthAmount() * status.SelfTemporaryStrengthValue;
        return score;
    }

    private static int ScoreImmediateDefense(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatStatusWeights status = context.CombatConfig.Combat.StatusWeights;
        AiCombatRiskProfile risk = context.CombatConfig.Combat.RiskProfile;
        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock);
        int block = card.GetEstimatedBlock();
        int weakAmount = card.GetEnemyWeakAmount();
        int temporaryDexterity = card.GetSelfTemporaryDexterityAmount();
        int dexterity = Math.Max(0, card.GetSelfDexterityAmount() - temporaryDexterity);
        int weakPrevention = EstimateWeakPrevention(context, action, weakAmount);
        int blockedDamage = Math.Min(block, uncoveredDamage);

        int score = 0;
        if (block > 0)
        {
            score += blockedDamage * risk.BlockedDamageValuePerPoint;
            int excessBlock = Math.Max(0, block - uncoveredDamage);
            score += context.ValuesExcessBlock
                ? excessBlock * risk.ExcessBlockValuePerPoint
                : -(excessBlock * Math.Max(2, risk.ExcessBlockValuePerPoint + 2));
            if (uncoveredDamage > 0 && block >= uncoveredDamage)
            {
                score += risk.FullBlockCoverageBonus;
            }
        }

        if (weakPrevention > 0)
        {
            score += weakPrevention * status.WeakImmediateDefenseValue;
        }

        if (temporaryDexterity > 0)
        {
            int nearTermBlockValue = HasAffordableBlockFollowUp(context, action)
                ? status.TemporaryDexterityWithFollowUpBlockValue
                : (uncoveredDamage > 0 ? status.TemporaryDexterityThreatenedBlockValue : status.TemporaryDexteritySafeBlockValue);
            score += temporaryDexterity * nearTermBlockValue;
        }

        if (dexterity > 0)
        {
            int futureBlockValue = HasPlayableBlockAction(context)
                ? status.PersistentDexterityWithBlockValue
                : status.PersistentDexterityWithoutBlockValue;
            score += dexterity * futureBlockValue;
        }

        if (context.CurrentHp <= Math.Max(12, context.TotalExpectedEndTurnLifeLoss))
        {
            score += risk.LowHealthEmergencyDefenseBonus;
        }

        return score;
    }

    private static int ScoreEnemyDebuff(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatStatusWeights status = context.CombatConfig.Combat.StatusWeights;
        int score = 0;
        int vulnerable = card.GetEnemyVulnerableAmount();
        int weak = card.GetEnemyWeakAmount();

        if (vulnerable > 0)
        {
            int followUpAttacks = CountAffordableAttackActions(context, action);
            score += vulnerable * (followUpAttacks > 0 ? status.VulnerableWithFollowUpValue : status.VulnerableWithoutFollowUpValue);
        }

        if (weak > 0)
        {
            score += EstimateWeakPrevention(context, action, weak) * status.WeakDebuffValue;
        }

        return score;
    }

    private static int ScoreSelfBuff(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatStatusWeights status = context.CombatConfig.Combat.StatusWeights;
        int temporaryStrength = card.GetSelfTemporaryStrengthAmount();
        int totalStrength = card.GetSelfStrengthAmount();
        int persistentStrength = Math.Max(0, totalStrength - temporaryStrength);
        int temporaryDexterity = card.GetSelfTemporaryDexterityAmount();
        int totalDexterity = card.GetSelfDexterityAmount();
        int persistentDexterity = Math.Max(0, totalDexterity - temporaryDexterity);

        int score = 0;
        if (temporaryStrength > 0)
        {
            score += temporaryStrength * Math.Max(
                status.TemporaryStrengthMinimumValue,
                CountAffordableAttackActions(context, action) * status.TemporaryStrengthPerAffordableAttackValue);
        }

        if (persistentStrength > 0)
        {
            score += persistentStrength * Math.Max(
                status.PersistentStrengthMinimumValue,
                CountAffordableAttackActions(context, action) * status.PersistentStrengthPerAffordableAttackValue);
        }

        if (temporaryDexterity > 0)
        {
            score += temporaryDexterity * Math.Max(
                status.TemporaryDexterityMinimumValue,
                CountAffordableBlockActions(context, action) * status.TemporaryDexterityPerAffordableBlockValue);
        }

        if (persistentDexterity > 0)
        {
            score += persistentDexterity * Math.Max(
                status.PersistentDexterityMinimumValue,
                CountAffordableBlockActions(context, action) * status.PersistentDexterityPerAffordableBlockValue);
        }

        return score;
    }

    private static int ScoreResourceSetup(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatResourceWeights resource = context.CombatConfig.Combat.ResourceWeights;
        int cardsDrawn = card.GetCardsDrawn();
        int energyGain = card.GetEnergyGain();
        int score = 0;

        if (cardsDrawn > 0)
        {
            bool hasSpendableFollowUp = CountAffordablePlayableActions(context, action, extraEnergy: energyGain) > 0;
            score += hasSpendableFollowUp
                ? cardsDrawn * resource.DrawValueWhenPlayable
                : -cardsDrawn * resource.DrawPenaltyWhenNotPlayable;
        }

        if (energyGain > 0)
        {
            score += energyGain * resource.EnergyGainValue;
        }

        return score;
    }

    private static int ScoreKillPotential(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatRiskProfile risk = context.CombatConfig.Combat.RiskProfile;
        if (string.IsNullOrEmpty(action.TargetId) ||
            !context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            return 0;
        }

        int estimatedDamage = card.GetEstimatedDamage();
        int effectiveEnemyHp = enemy.CurrentHp + enemy.Block;
        if (estimatedDamage >= effectiveEnemyHp)
        {
            return risk.LethalPriorityBonus + enemy.IncomingDamage * risk.LethalIncomingDamageValue;
        }

        return 0;
    }

    private static int ScoreUtility(DeterministicCombatContext context, AiLegalActionOption action)
    {
        AiCombatCoreWeights core = context.CombatConfig.Combat.CoreWeights;
        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock);
        int score = uncoveredDamage > 0 ? core.UtilityValueWhenThreatened : core.UtilityValueWhenSafe;
        score += ScoreEnergyEfficiency(context, action, null);
        return score;
    }

    private static int ScorePotion(DeterministicCombatContext context, AiLegalActionOption action)
    {
        AiPotionCombatUseWeights potionUse = context.CombatConfig.Potions.CombatUse;
        bool isOffensivePotion = IsOffensivePotion(action);
        bool isDefensivePotion = IsDefensivePotion(action);
        bool graveDanger = IsGraveDanger(context);
        bool canAmplifyAttacks = isOffensivePotion && CountNonPotionAttackActions(context) > 0;
        bool isHighValueTarget = IsHighValuePotionTarget(context, action);
        bool tacticalNeed = HasTacticalPotionNeed(
            context,
            isOffensivePotion,
            isDefensivePotion,
            graveDanger,
            canAmplifyAttacks,
            isHighValueTarget);

        int score = context.IsEliteOrBossCombat ? potionUse.EliteBossBaseScore : potionUse.NormalFightBaseScore;

        if (context.IsEliteOrBossCombat)
        {
            score += potionUse.EliteBossBonus;
        }

        if (graveDanger)
        {
            score += isDefensivePotion ? potionUse.GraveDangerDefensiveBonus : potionUse.GraveDangerOffensiveBonus;
        }

        if (isOffensivePotion)
        {
            if (context.IsEliteOrBossCombat && canAmplifyAttacks)
            {
                score += potionUse.EliteBossOffensiveFollowUpBonus;
            }
            else if (!context.IsEliteOrBossCombat && canAmplifyAttacks && isHighValueTarget)
            {
                score += potionUse.NormalFightOffensiveFollowUpBonus;
            }
        }

        if (!tacticalNeed)
        {
            score -= context.IsEliteOrBossCombat ? 70 : 120;
        }

        if (!string.IsNullOrEmpty(action.TargetId) &&
            context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            if (enemy.IsAttacking)
            {
                score += potionUse.AttackingTargetBonus;
            }

            if (enemy.CurrentHp + enemy.Block <= 18)
            {
                score -= potionUse.LowHealthTargetPenalty;
            }
        }

        return score;
    }

    private static bool HasTacticalPotionNeed(
        DeterministicCombatContext context,
        bool isOffensivePotion,
        bool isDefensivePotion,
        bool graveDanger,
        bool canAmplifyAttacks,
        bool isHighValueTarget)
    {
        if (graveDanger)
        {
            return true;
        }

        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
        bool underPressure = uncoveredDamage >= 8 || uncoveredDamage >= Math.Max(6, context.CurrentHp / 5);
        if (isDefensivePotion && underPressure)
        {
            return true;
        }

        if (isOffensivePotion && canAmplifyAttacks && isHighValueTarget)
        {
            return context.IsEliteOrBossCombat || underPressure;
        }

        return context.IsEliteOrBossCombat && (underPressure || isHighValueTarget);
    }

    private static int ScoreBuildCombatFit(DeterministicCombatContext context, ResolvedCardView card)
    {
        AiBuildProfileMatch? active = context.ActiveBuild;
        if (active == null || active.EvidenceCards <= 0)
        {
            return 0;
        }

        int score = 0;
        AiBuildArchetype profile = active.Profile;
        bool isCoreCard = AiBuildProfileAnalyzer.IsCoreCard(profile, card);
        bool isSupportCard = AiBuildProfileAnalyzer.IsSupportCard(profile, card);
        if (isCoreCard)
        {
            score += active.IsLocked ? 18 : 10;
        }
        else if (isSupportCard)
        {
            score += active.IsLocked ? 8 : 4;
        }
        else if (AiBuildProfileAnalyzer.IsAvoidCard(profile, card))
        {
            score -= active.IsLocked ? 18 : 8;
        }

        if (card.Type == CardType.Power && isCoreCard)
        {
            score += active.IsLocked ? 28 : 18;
            score += context.IsEliteOrBossCombat ? 10 : 5;
        }
        else if (active.IsLocked && card.Type == CardType.Power && isSupportCard)
        {
            score += context.IsEliteOrBossCombat ? 8 : 4;
        }

        return score;
    }

    private static int ScoreEndTurn(DeterministicCombatContext context)
    {
        AiCombatResourceWeights resource = context.CombatConfig.Combat.ResourceWeights;
        if (ShouldPreferEndTurnOverRemainingPotions(context))
        {
            return resource.EndTurnWhenSkippingPotionsBonus;
        }

        return context.LegalActions.Count > 1 ? -resource.EndTurnWhileOtherActionsExistPenalty : 0;
    }

    private static int ScoreEnergyEfficiency(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView? card)
    {
        if (card?.HasXCost == true)
        {
            return 0;
        }

        if (!action.EnergyCost.HasValue)
        {
            return 0;
        }

        return Math.Max(0, 4 - action.EnergyCost.Value) * context.CombatConfig.Combat.ResourceWeights.EnergyEfficiencyValue;
    }

    private static int GetActorPowerAmount(DeterministicCombatContext context, string powerId)
    {
        return context.ActorPowerAmounts.TryGetValue(powerId, out int amount) ? amount : 0;
    }

    private static int GetDamageHits(ResolvedCardView card)
    {
        return card.Effects
            .Where(static effect => effect.Kind == EffectKind.DealDamage)
            .Sum(static effect => Math.Max(effect.RepeatCount, 1));
    }

    private static int EstimateWeakPrevention(DeterministicCombatContext context, AiLegalActionOption action, int weakAmount)
    {
        if (weakAmount <= 0)
        {
            return 0;
        }

        if (!string.IsNullOrEmpty(action.TargetId) &&
            context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            return Math.Max(1, enemy.IncomingDamage / 4);
        }

        return Math.Max(1, context.IncomingDamage / 6);
    }

    private static bool HasPlayableBlockAction(DeterministicCombatContext context)
    {
        foreach (AiLegalActionOption action in context.LegalActions)
        {
            ResolvedCardView? card = ResolveCard(context, action);
            if (card?.HasEffect(EffectKind.GainBlock) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAffordableBlockFollowUp(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        return CountAffordableBlockActions(context, currentAction) > 0;
    }

    private static int CountAffordableAttackActions(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        int remainingEnergy = GetEnergyAfterAction(context, currentAction, extraEnergy: 0);
        return context.LegalActions.Count(candidate =>
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsAffordableAtEnergy(context, candidate, remainingEnergy))
            {
                return false;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            return card?.HasEffect(EffectKind.DealDamage) == true || card?.Type == CardType.Attack;
        });
    }

    private static int CountAffordableBlockActions(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        int remainingEnergy = GetEnergyAfterAction(context, currentAction, extraEnergy: 0);
        return context.LegalActions.Count(candidate =>
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsAffordableAtEnergy(context, candidate, remainingEnergy))
            {
                return false;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            return card?.HasEffect(EffectKind.GainBlock) == true;
        });
    }

    private static int CountAffordablePlayableActions(DeterministicCombatContext context, AiLegalActionOption currentAction, int extraEnergy)
    {
        int remainingEnergy = GetEnergyAfterAction(context, currentAction, extraEnergy);
        return context.LegalActions.Count(candidate =>
            !string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) &&
            !string.Equals(candidate.ActionType, AiTeammateActionKind.EndTurn.ToString(), StringComparison.Ordinal) &&
            IsAffordableAtEnergy(context, candidate, remainingEnergy));
    }

    private static int GetEnergyAfterAction(DeterministicCombatContext context, AiLegalActionOption action, int extraEnergy)
    {
        ResolvedCardView? card = ResolveCard(context, action);
        int spentEnergy = card?.HasXCost == true ? context.Energy : action.EnergyCost ?? 0;
        return Math.Max(0, context.Energy - spentEnergy + extraEnergy);
    }

    private static bool IsAffordableAtEnergy(DeterministicCombatContext context, AiLegalActionOption action, int energyRemaining)
    {
        ResolvedCardView? card = ResolveCard(context, action);
        return card?.HasXCost == true
            ? energyRemaining > 0
            : (action.EnergyCost ?? 0) <= energyRemaining;
    }

    private static int CountNonPotionAttackActions(DeterministicCombatContext context)
    {
        int count = 0;
        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionType, AiTeammateActionKind.UsePotion.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            if (card?.HasEffect(EffectKind.DealDamage) == true || card?.Type == CardType.Attack)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsOffensivePotion(AiLegalActionOption action)
    {
        string potionId = action.CardId ?? string.Empty;
        return potionId.Contains("BINDING", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("VULNERABLE", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("WEAK", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("POISON", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("FIRE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefensivePotion(AiLegalActionOption action)
    {
        string potionId = action.CardId ?? string.Empty;
        return potionId.Contains("BLOCK", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("ARMOR", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("DEXTERITY", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("WEAK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraveDanger(DeterministicCombatContext context)
    {
        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
        return uncoveredDamage >= Math.Max(10, context.CurrentHp / 3) || uncoveredDamage >= context.CurrentHp;
    }

    private static bool IsHighValuePotionTarget(DeterministicCombatContext context, AiLegalActionOption action)
    {
        if (string.IsNullOrEmpty(action.TargetId) ||
            !context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            return false;
        }

        return enemy.IsAttacking || enemy.CurrentHp + enemy.Block >= 24;
    }

    private static bool ShouldPreferEndTurnOverRemainingPotions(DeterministicCombatContext context)
    {
        return context.LegalActions
            .Where(action => string.Equals(action.ActionType, AiTeammateActionKind.UsePotion.ToString(), StringComparison.Ordinal))
            .All(action => ScorePotion(context, action) <= 0);
    }

    private static bool IsRedundantBlockOnly(DeterministicCombatContext context, ResolvedCardView card)
    {
        if (context.ValuesExcessBlock || Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) > 0)
        {
            return false;
        }

        return IsBlockOnlyDefense(card);
    }

    private static bool IsBlockOnlyDefense(ResolvedCardView card)
    {
        return card.GetEstimatedBlock() > 0 &&
               card.GetEstimatedDamage() <= 0 &&
               card.GetCardsDrawn() <= 0 &&
               card.GetEnergyGain() <= 0 &&
               card.GetEnemyWeakAmount() <= 0 &&
               card.GetEnemyVulnerableAmount() <= 0 &&
               card.GetSelfStrengthAmount() <= 0 &&
               card.GetSelfDexterityAmount() <= 0;
    }
}
