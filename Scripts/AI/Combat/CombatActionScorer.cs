using System;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal sealed class CombatActionScorer
{
    private const int RedundantBlockOnlyScore = -100000;
    private const int TeamReservedKillPenalty = 110;
    private const int OverkillPenaltyPerPoint = 3;

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
        int buildCombatFitScore = ScoreBuildCombatFit(context, action, card);
        int futureTurnValueScore = ScoreFutureTurnValue(context, action, card);
        CombatActionCategory category = Classify(card, immediateDamageScore, immediateDefenseScore, selfBuffScore, resourceSetupScore);
        int learnedAdjustmentScore = AiLearningService.GetCombatAdjustment(context, action, card, category);
        int totalScore = risk.ApplyAttackWeight(immediateDamageScore) +
                         risk.ApplyDefenseWeight(immediateDefenseScore) +
                         enemyDebuffScore +
                         selfBuffScore +
                         resourceSetupScore +
                         killPotentialScore +
                         futureTurnValueScore +
                         ScoreEnergyEfficiency(context, action, card) +
                         buildCombatFitScore +
                         learnedAdjustmentScore;

        Log.Debug(
            $"[AITeammate] Semantic score actionId={action.ActionId} category={category} damage={immediateDamageScore} defense={immediateDefenseScore} debuff={enemyDebuffScore} buff={selfBuffScore} setup={resourceSetupScore} kill={killPotentialScore} future={futureTurnValueScore} build={buildCombatFitScore} learned={learnedAdjustmentScore} total={totalScore}");

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

        bool isAllEnemiesDamage = IsAllEnemiesDamage(card);
        int usefulDamage = isAllEnemiesDamage ? EstimateAllEnemiesUsefulDamage(context, damage) : damage;
        int score = usefulDamage * core.DirectDamageValuePerPoint;
        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock);
        if (uncoveredDamage > 0 && HasPlayableBlockAction(context))
        {
            score -= core.AttackWhileDefenseNeededPenalty;
        }

        if (isAllEnemiesDamage)
        {
            score += ScoreAllEnemiesTargetPressure(context, damage);
            return score + GetActorPowerAmount(context, "STRENGTH") * Math.Max(1, GetDamageHits(card)) * status.StrengthPerHitValue;
        }

        if (!string.IsNullOrEmpty(action.TargetId) &&
            context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            int effectiveHp = GetTeamAdjustedEnemyHp(context, action.TargetId, enemy);
            if (effectiveHp <= 0)
            {
                score -= TeamReservedKillPenalty + damage * OverkillPenaltyPerPoint;
            }
            else
            {
                score += Math.Max(0, core.TargetLowHealthBiasThreshold - effectiveHp) * core.TargetLowHealthBiasValuePerPoint;
                if (context.EnemiesById.Count > 1 && damage > effectiveHp)
                {
                    score -= (damage - effectiveHp) * OverkillPenaltyPerPoint;
                }
            }

            if (enemy.IsAttacking)
            {
                score += core.AttackingTargetBonus;
            }

            score += Math.Min(enemy.ThreatScore, 45);
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

            if (uncoveredDamage > 0)
            {
                score += Math.Min(blockedDamage, 8) * 4;
            }

            if (uncoveredDamage > 0 && CombatBuildRoleEvaluator.IsPriorityDrawBlockCard(card))
            {
                score += 18 + (card.GetCardsDrawn() * 6);
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
            int targetMultiplier = IsAllEnemiesDebuff(card, "Vulnerable") ? Math.Max(1, context.EnemiesById.Count) : 1;
            score += vulnerable * targetMultiplier * (followUpAttacks > 0 ? status.VulnerableWithFollowUpValue : status.VulnerableWithoutFollowUpValue);
        }

        if (weak > 0)
        {
            int weakPrevention = IsAllEnemiesDebuff(card, "Weak")
                ? context.EnemiesById.Values.Sum(enemy => Math.Max(1, enemy.IncomingDamage / 4))
                : EstimateWeakPrevention(context, action, weak);
            score += weakPrevention * status.WeakDebuffValue;
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

        if (CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card))
        {
            score += hasSpendableFollowUp ? 80 : 10;
        }

        if (CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card))
        {
            score += hasSpendableFollowUp ? 72 : 8;
        }
        }

        if (CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card))
        {
            score += 64;
            if (context.Energy > 0)
            {
                score += 24;
            }
        }

        if (energyGain > 0)
        {
            score += energyGain * resource.EnergyGainValue;
        }

        if (CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card))
        {
            score += context.Energy > 0 ? 56 : 28;
            if (HasUnplayedAffordableNonSlyAction(context, action))
            {
                score += 18;
            }
        }

        if (CombatBuildRoleEvaluator.IsEngineSetup(context, card))
        {
            score += context.ActiveBuild?.IsLocked == true ? 54 : 38;
            if (context.Energy >= action.EnergyCost.GetValueOrDefault(card.EffectiveCost))
            {
                score += 12;
            }
        }

        if (CombatBuildRoleEvaluator.IsOstyGuardCard(card) && context.ActiveBuild?.Profile.BuildId == "osty")
        {
            score += context.ActiveBuild.IsLocked ? 44 : 30;
        }

        if (CombatBuildRoleEvaluator.Classify(context, card) == CombatBuildRole.Setup)
        {
            score += context.ActiveBuild?.IsLocked == true ? 18 : 10;
        }

        return score;
    }

    private static int ScoreKillPotential(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        AiCombatRiskProfile risk = context.CombatConfig.Combat.RiskProfile;
        int estimatedDamage = card.GetEstimatedDamage();
        if (IsAllEnemiesDamage(card))
        {
            return context.EnemiesById.Sum(pair =>
            {
                int effectiveEnemyHp = GetTeamAdjustedEnemyHp(context, pair.Key, pair.Value);
                return effectiveEnemyHp > 0 && estimatedDamage >= effectiveEnemyHp
                    ? risk.LethalPriorityBonus + pair.Value.IncomingDamage * risk.LethalIncomingDamageValue
                    : 0;
            });
        }

        if (string.IsNullOrEmpty(action.TargetId) ||
            !context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            return 0;
        }

        int effectiveEnemyHp = GetTeamAdjustedEnemyHp(context, action.TargetId, enemy);
        if (effectiveEnemyHp <= 0)
        {
            return -TeamReservedKillPenalty;
        }

        if (estimatedDamage >= effectiveEnemyHp)
        {
            return risk.LethalPriorityBonus + enemy.IncomingDamage * risk.LethalIncomingDamageValue;
        }

        return 0;
    }

    private static int GetTeamAdjustedEnemyHp(DeterministicCombatContext context, string targetId, DeterministicEnemyState enemy)
    {
        return CombatTeamDamageReservationPolicy.GetEffectiveEnemyHp(context, targetId, enemy);
    }

    private static bool IsAllEnemiesDamage(ResolvedCardView card)
    {
        return card.Targeting == TargetType.AllEnemies ||
               card.Effects.Any(static effect => effect.Kind == EffectKind.DealDamage && effect.TargetScope == TargetScope.AllEnemies);
    }

    private static bool IsAllEnemiesDebuff(ResolvedCardView card, string powerId)
    {
        return card.Effects.Any(effect => effect.Kind == EffectKind.ApplyPower &&
                                          effect.TargetScope == TargetScope.AllEnemies &&
                                          string.Equals(effect.AppliedPowerId, powerId, StringComparison.Ordinal));
    }

    private static int EstimateAllEnemiesUsefulDamage(DeterministicCombatContext context, int damagePerEnemy)
    {
        return context.EnemiesById.Sum(pair =>
        {
            int effectiveHp = GetTeamAdjustedEnemyHp(context, pair.Key, pair.Value);
            return Math.Min(damagePerEnemy, effectiveHp);
        });
    }

    private static int ScoreAllEnemiesTargetPressure(DeterministicCombatContext context, int damagePerEnemy)
    {
        if (context.EnemiesById.Count <= 1)
        {
            return 0;
        }

        int livingTargets = context.EnemiesById.Count(pair => GetTeamAdjustedEnemyHp(context, pair.Key, pair.Value) > 0);
        int attackingTargets = context.EnemiesById.Values.Count(static enemy => enemy.IsAttacking);
        int threatenedTargets = context.EnemiesById.Values.Count(static enemy => enemy.ThreatScore >= 18);
        int likelyKills = context.EnemiesById.Count(pair =>
        {
            int effectiveHp = GetTeamAdjustedEnemyHp(context, pair.Key, pair.Value);
            return effectiveHp > 0 && damagePerEnemy >= effectiveHp;
        });

        return Math.Max(0, livingTargets - 1) * 14 +
               attackingTargets * 8 +
               threatenedTargets * 6 +
               likelyKills * 18;
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
        bool isGenericPotion = !isOffensivePotion && !isDefensivePotion;
        bool graveDanger = IsGraveDanger(context);
        int uncoveredDamage = EstimateUncoveredEndTurnDamage(context);
        bool underPressure = uncoveredDamage >= 8 || uncoveredDamage >= Math.Max(6, context.CurrentHp / 5);
        bool severePressure = uncoveredDamage >= 14 || uncoveredDamage >= Math.Max(8, context.CurrentHp / 3);
        bool lowHp = context.CurrentHp <= Math.Max(18, context.Actor.Creature.MaxHp / 3);
        bool wounded = context.CurrentHp <= Math.Max(24, context.Actor.Creature.MaxHp / 2);
        bool canAmplifyAttacks = isOffensivePotion && CountNonPotionAttackActions(context) > 0;
        bool isHighValueTarget = IsHighValuePotionTarget(context, action);
        bool tacticalNeed = HasTacticalPotionNeed(
            context,
            isOffensivePotion,
            isDefensivePotion,
            isGenericPotion,
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
            if (isGenericPotion)
            {
                score += 150;
            }
        }
        else if (isDefensivePotion && severePressure)
        {
            score += context.IsEliteOrBossCombat ? 140 : 185;
        }
        else if (isDefensivePotion && underPressure)
        {
            score += context.IsEliteOrBossCombat ? 95 : 120;
        }
        else if (isOffensivePotion && severePressure && canAmplifyAttacks)
        {
            score += context.IsEliteOrBossCombat ? 85 : 55;
        }
        else if (isGenericPotion && severePressure)
        {
            score += context.IsEliteOrBossCombat ? 90 : 70;
        }
        else if (isGenericPotion && underPressure && context.CurrentHp <= Math.Max(18, context.Actor.Creature.MaxHp / 3))
        {
            score += 55;
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

        if (tacticalNeed && context.IsEliteOrBossCombat)
        {
            score += 35;
        }

        if (tacticalNeed && wounded && (underPressure || context.IsEliteOrBossCombat))
        {
            score += 45;
        }

        if (severePressure && wounded)
        {
            score += 50;
        }

        if (lowHp && (isGenericPotion || isDefensivePotion))
        {
            score += 35;
        }

        if (!tacticalNeed)
        {
            score -= context.IsEliteOrBossCombat ? 45 : 85;
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
        bool isGenericPotion,
        bool graveDanger,
        bool canAmplifyAttacks,
        bool isHighValueTarget)
    {
        if (graveDanger)
        {
            return true;
        }

        int uncoveredDamage = EstimateUncoveredEndTurnDamage(context);
        bool underPressure = uncoveredDamage >= 8 || uncoveredDamage >= Math.Max(6, context.CurrentHp / 5);
        if (isDefensivePotion && underPressure)
        {
            return true;
        }

        if (isGenericPotion && underPressure && context.CurrentHp <= Math.Max(18, context.Actor.Creature.MaxHp / 3))
        {
            return true;
        }

        if (isOffensivePotion && canAmplifyAttacks && isHighValueTarget)
        {
            return context.IsEliteOrBossCombat || underPressure;
        }

        return context.IsEliteOrBossCombat && (underPressure || isHighValueTarget);
    }

    private static int ScoreBuildCombatFit(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        if (CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card))
        {
            int earlySoulScore = 92;
            if (context.Energy > 0)
            {
                earlySoulScore += 28;
            }

            if (HasUnplayedAffordableNonSoulAction(context, action))
            {
                earlySoulScore += 24;
            }

            return earlySoulScore;
        }

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

        if (CombatBuildRoleEvaluator.IsEngineSetup(context, card))
        {
            score += active.IsLocked ? 34 : 24;
            if (context.IsEliteOrBossCombat)
            {
                score += 8;
            }
        }

        if (CombatBuildRoleEvaluator.IsOstyGuardCard(card) && active.Profile.BuildId == "osty")
        {
            score += active.IsLocked ? 34 : 24;
            if (context.IncomingDamage > 0)
            {
                score += 10;
            }
        }

        if (CombatBuildRoleEvaluator.IsNecrobinderFreeSoulDraw(context, card))
        {
            score += active.IsLocked ? 32 : 22;
        }

        if (CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card))
        {
            score += active.IsLocked ? 42 : 28;
            if (context.Energy > 0)
            {
                score += 18;
            }
        }

        if (profile.BuildId == "poison" && CombatBuildRoleEvaluator.IsSilentPoisonSetupCard(card))
        {
            score += active.IsLocked ? 30 : 18;
            if (context.IsEliteOrBossCombat)
            {
                score += 14;
            }
        }

        if (profile.BuildId is "shiv" or "envenom" && CombatBuildRoleEvaluator.IsSilentShivSetupCard(card))
        {
            score += active.IsLocked ? 34 : 22;
        }

        if (CombatBuildRoleEvaluator.IsPriorityDrawBlockCard(card) && EstimateUncoveredEndTurnDamage(context) > 0)
        {
            score += 18;
        }

        if (CombatBuildRoleEvaluator.IsOrbSetupBuild(context) &&
            CombatBuildRoleEvaluator.IsWeakStarterStrike(card) &&
            HasUnplayedAffordableEngineSetup(context, action))
        {
            score -= active.IsLocked ? 42 : 30;
        }

        if (CombatBuildRoleEvaluator.IsWeakStarterStrike(card) &&
            ShouldPreferAffordableBuildSetupOverStarterStrike(context, action, card))
        {
            score -= active.IsLocked ? 64 : 42;
        }

        if (CombatBuildRoleEvaluator.IsWeakStarterStrike(card) &&
            HasAffordableHigherBuildDamageAction(context, action, card.GetEstimatedDamage()))
        {
            score -= active.IsLocked ? 48 : 34;
        }

        return score;
    }

    private static int ScoreFutureTurnValue(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        int score = 0;
        score += ScorePoisonFutureValue(context, action, card);
        score += ScorePersistentEngineFutureValue(context, card);
        score += ScoreNecrobinderFutureValue(context, action, card);
        score += ScoreSilentFutureValue(context, action, card);
        score += ScoreDefectFutureValue(context, action, card);
        return score;
    }

    private static int ScorePoisonFutureValue(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        int poisonAmount = card.GetEnemyPoisonAmount();
        bool isPoisonBuild = context.ActiveBuild?.Profile.BuildId == "poison";
        bool isPoisonNamedCard = HasCardToken(card, "POISON", "NOXIOUS", "CATALYST", "BOUNCING", "TOXIN", "VENOM");
        if (poisonAmount <= 0 && !isPoisonNamedCard)
        {
            return 0;
        }

        int horizon = GetFutureHorizon(context);
        int score = 0;
        foreach (DeterministicEnemyState enemy in GetFutureValueTargets(context, action, card))
        {
            int currentPoison = GetEnemyPowerAmount(enemy, "POISON");
            int effectiveHp = GetTeamAdjustedEnemyHp(context, enemy.Id, enemy);
            if (poisonAmount > 0)
            {
                int futureDamage = EstimatePoisonDamage(currentPoison + poisonAmount, horizon) -
                                   EstimatePoisonDamage(currentPoison, horizon);
                int usefulFutureDamage = Math.Min(futureDamage, Math.Max(0, effectiveHp));
                score += usefulFutureDamage * 4;
                score += poisonAmount * (isPoisonBuild ? 8 : 5);
            }

            if (currentPoison > 0 && HasCardToken(card, "CATALYST", "BURST", "BOUNCING"))
            {
                int payoffDamage = Math.Min(EstimatePoisonDamage(currentPoison, horizon), Math.Max(0, effectiveHp));
                score += payoffDamage * (isPoisonBuild ? 3 : 2);
                score += currentPoison >= 6 ? 22 : 10;
            }
        }

        if (isPoisonBuild && score > 0)
        {
            score += context.IsEliteOrBossCombat ? 28 : 16;
        }

        if (HasCardToken(card, "CORPSEEXPLOSION"))
        {
            int maxHpPool = context.EnemiesById.Values
                .Sum(static e => e.Creature.MaxHp);
            int currentHpPool = context.EnemiesById.Values
                .Sum(static e => e.CurrentHp);
            int explosionValue = Math.Min(maxHpPool, currentHpPool);
            score += explosionValue * (isPoisonBuild ? 3 : 2);
            score += context.IsEliteOrBossCombat ? 30 : 18;
        }

        return score;
    }

    private static int ScoreSilentFutureValue(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        if (!string.Equals(context.CombatConfig.CharacterId, "silent", StringComparison.OrdinalIgnoreCase) &&
            context.ActiveBuild?.Profile.CharacterId != "silent")
        {
            return 0;
        }

        int score = 0;
        string buildId = context.ActiveBuild?.Profile.BuildId ?? string.Empty;
        if (CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card))
        {
            int followUps = CountAffordablePlayableActions(context, action, extraEnergy: card.GetEnergyGain());
            score += buildId is "sly" or "grand_finale" ? 62 : 34;
            score += card.GetCardsDrawn() * 14;
            score += Math.Max(0, card.GetEnergyGain()) * 18;
            score += Math.Min(followUps, 3) * 10;
            if (context.Energy > 0)
            {
                score += 18;
            }
        }

        if (buildId == "poison" && HasCardToken(card, "ACCELERANT", "CATALYST", "BURST"))
        {
            int totalPoison = context.EnemiesById.Values.Sum(static enemy => GetEnemyPowerAmount(enemy, "POISON"));
            if (totalPoison > 0)
            {
                score += Math.Min(totalPoison, 18) * 8;
                score += context.IsEliteOrBossCombat ? 30 : 16;
            }
            else if (HasCardToken(card, "ACCELERANT"))
            {
                score -= 18;
            }
        }

        if (buildId is "shiv" or "envenom" && CombatBuildRoleEvaluator.IsSilentShivSetupCard(card))
        {
            int shivPayoffs = context.HandCardsByInstanceId.Values.Count(CombatBuildRoleEvaluator.IsSilentShivPayoffCard);
            score += 42 + shivPayoffs * 16;
        }

        if (CombatBuildRoleEvaluator.IsWeakStarterStrike(card) &&
            HasUnplayedAffordableSilentEngineAction(context, action))
        {
            score -= buildId is "sly" or "poison" or "shiv" ? 58 : 34;
        }

        if (card.AppliesPower("Caltrops"))
        {
            int enemyAttackCount = context.EnemiesById.Values.Count(static e => e.IsAttacking);
            int thornsDamage = card.IsUpgraded ? 5 : 3;
            score += Math.Min(enemyAttackCount * thornsDamage * 6, 80);
            score += context.IsEliteOrBossCombat ? 25 : 12;
        }

        if (card.AppliesPower("Intangible"))
        {
            int intangibleStacks = card.IsUpgraded ? 4 : 2;
            int incomingDmg = context.IncomingDamage;
            score += Math.Min(incomingDmg * 4, 120);
            score += intangibleStacks * 6;
            score += context.IsEliteOrBossCombat ? 30 : 16;
        }

        if (card.AppliesPower("Shiv"))
        {
            int shivPayoffs = context.HandCardsByInstanceId.Values.Count(CombatBuildRoleEvaluator.IsSilentShivPayoffCard);
            score += 20 + shivPayoffs * 12;
            if (HasCardToken(card, "BLADEDANCE"))
            {
                int shivCount = card.IsUpgraded ? 6 : 4;
                score += shivCount * 6 * 6;
            }
            else if (HasCardToken(card, "CLOAKANDDAGGER"))
            {
                int shivCount = card.IsUpgraded ? 3 : 2;
                score += shivCount * 6 * 6;
            }
            if (buildId is "shiv" or "envenom")
            {
                score += 22;
            }
        }

        if (card.AppliesPower("SlyPayoff"))
        {
            int slyEngines = context.HandCardsByInstanceId.Values
                .Count(static c => c.AppliesPower("Sly"));
            score += slyEngines * 16;
            score += buildId is "sly" or "grand_finale" ? 24 : 12;
        }

        if (HasCardToken(card, "BANE"))
        {
            int targetPoison = 0;
            if (!string.IsNullOrEmpty(action.TargetId) &&
                context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? baneTarget))
            {
                targetPoison = GetEnemyPowerAmount(baneTarget, "POISON");
            }
            else
            {
                targetPoison = context.EnemiesById.Values.Max(static e => GetEnemyPowerAmount(e, "POISON"));
            }

            if (targetPoison > 0)
            {
                score += card.GetEstimatedDamage() * 6;
            }

            score += targetPoison > 0 ? 12 : 0;
        }

        if (HasCardToken(card, "AFTERIMAGE"))
        {
            int cardsPerTurn = 3;
            int shivGenerators = context.HandCardsByInstanceId.Values
                .Count(CombatBuildRoleEvaluator.IsSilentShivPayoffCard);
            cardsPerTurn += shivGenerators * 2;
            int slyEngines = context.HandCardsByInstanceId.Values
                .Count(static c => c.AppliesPower("Sly"));
            cardsPerTurn += slyEngines;
            int blockPerCard = card.IsUpgraded ? 2 : 1;
            int horizon = GetFutureHorizon(context);
            score += Math.Min(cardsPerTurn * blockPerCard * horizon * 4, 100);
            score += context.IsEliteOrBossCombat ? 20 : 10;
        }

        if (HasCardToken(card, "NIGHTMARE"))
        {
            int copies = card.IsUpgraded ? 2 : 1;
            string selfInstanceId = action.CardInstanceId ?? string.Empty;
            int bestCost = context.HandCardsByInstanceId.Values
                .Where(c => c.CardId != card.CardId)
                .Select(static c => c.EffectiveCost)
                .DefaultIfEmpty(0)
                .Max();
            score += 40 + bestCost * 18;
            score += copies > 1 ? 28 : 0;
            score += context.IsEliteOrBossCombat ? 30 : 16;
        }

        if (HasCardToken(card, "BURST"))
        {
            int highValueSkills = context.HandCardsByInstanceId.Values
                .Count(c => c.CardId != card.CardId &&
                            c.Type == CardType.Skill &&
                            (c.AppliesPower("Caltrops") ||
                             c.AppliesPower("Intangible") ||
                             c.AppliesPower("Sly") ||
                             c.GetEstimatedBlock() >= 10 ||
                             c.GetEnemyPoisonAmount() >= 5));
            if (highValueSkills > 0)
            {
                score += highValueSkills * 20;
                score += context.IsEliteOrBossCombat ? 20 : 10;
            }
        }

        if (HasCardToken(card, "GRANDFINALE"))
        {
            bool canFire = context.DrawPileCards?.Count == 0;
            if (canFire)
            {
                score += 80;
                score += context.IsEliteOrBossCombat ? 40 : 20;
            }
            else
            {
                int drawSize = context.DrawPileCards?.Count ?? 10;
                score += drawSize <= 3 ? 30 : drawSize <= 6 ? 12 : 0;
                if (drawSize > 6)
                {
                    score -= 24;
                }
            }
        }

        return score;
    }

    private static int ScoreDefectFutureValue(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        if (!string.Equals(context.CombatConfig.CharacterId, "defect", StringComparison.OrdinalIgnoreCase) &&
            context.ActiveBuild?.Profile.CharacterId != "defect")
        {
            return 0;
        }

        int score = 0;
        string buildId = context.ActiveBuild?.Profile.BuildId ?? string.Empty;
        bool isOrbBuild = buildId is "lightning" or "frost" or "dark_orb" or "creative_ai";
        int orbSlots = context.OrbSlots;
        int focus = context.FocusLevel;

        if (card.AppliesPower("LightningOrb") || card.AppliesPower("FrostOrb") || card.AppliesPower("DarkOrb"))
        {
            int lightningDmg = orbSlots * (3 + focus);
            int frostBlock = orbSlots * (2 + focus);
            int horizon = GetFutureHorizon(context);
            score += Math.Min(lightningDmg * 5 * horizon, 120);
            score += Math.Min(frostBlock * 4 * horizon, 80);

            if (card.AppliesPower("DarkOrb"))
            {
                int darkBase = 6 + focus;
                int scaledDmg = darkBase * 4 * horizon;
                score += Math.Min(scaledDmg, 90);
                score += isOrbBuild ? 16 : 8;
            }

            if (HasCardToken(card, "ELECTRODYNAMICS"))
            {
                int enemyCount = context.EnemiesById.Count;
                if (enemyCount > 1)
                {
                    int aoeBonus = lightningDmg * (enemyCount - 1) * 3 * GetFutureHorizon(context);
                    score += Math.Min(aoeBonus, 80);
                    score += context.IsEliteOrBossCombat ? 24 : 12;
                }
            }

            score += isOrbBuild ? 20 : 8;
        }

        if (card.AppliesPower("Focus"))
        {
            int focusGain = card.GetAppliedPowerAmount("Focus");
            if (focusGain > 0)
            {
                int projectedDmg = orbSlots * 3 * focusGain;
                int projectedBlock = orbSlots * 2 * focusGain;
                score += Math.Min(projectedDmg * 4, 80);
                score += Math.Min(projectedBlock * 4, 60);
                score += isOrbBuild ? 28 : 14;
            }
        }

        if (card.AppliesPower("OrbSlot"))
        {
            int newSlots = 2;
            int addedDmg = newSlots * (3 + focus);
            int addedBlock = newSlots * (2 + focus);
            score += Math.Min(addedDmg * 5, 70);
            score += Math.Min(addedBlock * 5, 50);
            score += context.IsEliteOrBossCombat ? 20 : 10;
        }

        if (card.AppliesPower("OrbEvoke"))
        {
            score += orbSlots * 12;
            score += isOrbBuild ? 24 : 12;
        }

        if (HasCardToken(card, "ALLFORONE", "SCRAPE"))
        {
            int zeroCostInHand = context.HandCardsByInstanceId.Values
                .Count(c => c.CardId != card.CardId && c.EffectiveCost == 0);
            int zeroCostInDiscard = context.DiscardPileCards?.Count(static c => c.EffectiveCost == 0) ?? 0;
            score += Math.Min(zeroCostInHand + zeroCostInDiscard, 8) * 14;
            score += buildId == "claw" ? 22 : 10;
        }

        if (HasCardToken(card, "ECHOFORM"))
        {
            int cardsPerTurn = 3 + orbSlots;
            int horizon = GetFutureHorizon(context);
            score += Math.Min(cardsPerTurn * horizon * 8, 100);
            score += isOrbBuild ? 30 : 16;
            score += context.IsEliteOrBossCombat ? 24 : 12;
        }

        if (buildId == "claw" && HasCardToken(card, "CLAW"))
        {
            int clawCount = context.HandCardsByInstanceId.Values
                .Count(c => HasCardToken(c, "CLAW"));
            score += 20 + clawCount * 18;
        }

        return score;
    }

    private static int ScorePersistentEngineFutureValue(DeterministicCombatContext context, ResolvedCardView card)
    {
        AiBuildProfileMatch? active = context.ActiveBuild;
        int score = 0;
        if (card.Type == CardType.Power)
        {
            bool isCore = active != null && AiBuildProfileAnalyzer.IsCoreCard(active.Profile, card);
            bool isSupport = active != null && AiBuildProfileAnalyzer.IsSupportCard(active.Profile, card);
            if (isCore || IsLongHorizonPower(card))
            {
                score += active?.IsLocked == true ? 58 : 42;
                score += context.IsEliteOrBossCombat ? 24 : 10;
            }
            else if (isSupport)
            {
                score += active?.IsLocked == true ? 28 : 16;
            }
        }

        if (CombatBuildRoleEvaluator.IsEngineSetup(context, card))
        {
            score += active?.IsLocked == true ? 38 : 26;
            score += context.IsEliteOrBossCombat ? 12 : 4;
        }

        return score;
    }

    private static int ScoreNecrobinderFutureValue(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        if (!string.Equals(context.CombatConfig.CharacterId, "necrobinder", StringComparison.OrdinalIgnoreCase) &&
            context.ActiveBuild?.Profile.CharacterId != "necrobinder")
        {
            return 0;
        }

        int score = 0;
        int damage = card.GetEstimatedDamage();
        if (damage >= 12 && (card.Targeting == TargetType.Osty || HasCardToken(card, "OSTY", "SOUL", "DEATH", "UNLEASH", "REAPER", "SCYTHE")))
        {
            score += Math.Min(damage, 40) * 4;
            score += context.ActiveBuild?.IsLocked == true ? 24 : 14;
        }

        if (CombatBuildRoleEvaluator.IsOstyGuardCard(card))
        {
            score += context.ActiveBuild?.IsLocked == true ? 34 : 22;
        }

        if (CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card))
        {
            score += 76;
            if (context.Energy > 0)
            {
                score += 28;
            }
        }

        if (card.AppliesPower("SummonOsty"))
        {
            score += context.ActiveBuild?.IsLocked == true ? 48 : 32;
            score += context.IsEliteOrBossCombat ? 18 : 8;
        }

        if (card.AppliesPower("SummonAlly"))
        {
            score += context.ActiveBuild?.IsLocked == true ? 34 : 22;
        }

        if (card.AppliesPower("Countdown"))
        {
            score += context.ActiveBuild?.IsLocked == true ? 22 : 14;
        }

        if (card.AppliesPower("Sacrifice"))
        {
            bool hasOsty = context.HandCardsByInstanceId.Values.Any(static c => c.AppliesPower("SummonOsty") || c.AppliesPower("SummonAlly"));
            if (hasOsty)
            {
                score += 36;
            }

            score += context.ActiveBuild?.IsLocked == true ? 20 : 10;
        }

        if (card.AppliesPower("ReaperForm"))
        {
            score += context.ActiveBuild?.IsLocked == true ? 52 : 36;
            score += context.IsEliteOrBossCombat ? 24 : 12;
        }

        if (HasCardToken(card, "SOULSTORM"))
        {
            int soulPowerCount = GetActorPowerAmountAnyCase(context,
                "SOUL", "Soul", "Souls", "SOULS", "SoulCount", "SOULCOUNT");
            if (soulPowerCount <= 0)
            {
                int soulCardsInHand = context.HandCardsByInstanceId.Values
                    .Count(static c => c.AppliesPower("Soul"));
                int soulCardsInDeck = context.DeckCards.Count(static c => c.AppliesPower("Soul"));
                soulPowerCount = Math.Min(soulCardsInHand + soulCardsInDeck / 2, 12);
            }

            int extraDamage = soulPowerCount * (card.IsUpgraded ? 3 : 2);
            score += extraDamage * 6;
            score += context.ActiveBuild?.IsLocked == true ? 26 : 14;
            score += context.IsEliteOrBossCombat ? 14 : 6;
        }

        if (HasCardToken(card, "DEATHMARCH"))
        {
            int totalDrawEstimate = context.HandCardsByInstanceId.Values
                .Sum(static c => c.GetCardsDrawn());
            int extraDamage = totalDrawEstimate * (card.IsUpgraded ? 4 : 3);
            score += extraDamage * 6;
            score += context.ActiveBuild?.IsLocked == true ? 22 : 12;
        }

        if (HasCardToken(card, "SCYTHE"))
        {
            int scalingDamage = card.IsUpgraded ? 4 : 3;
            score += scalingDamage * 6;
            score += card.Exhaust ? 22 : 0;
            score += context.ActiveBuild?.IsLocked == true ? 20 : 10;
        }

        if (HasCardToken(card, "ERADICATE"))
        {
            int energyToSpend = Math.Max(context.Energy, 1);
            score += energyToSpend * 14;
            score += card.Retain ? 12 : 0;
            score += context.ActiveBuild?.IsLocked == true ? 18 : 8;
        }

        if (card.AppliesPower("Lethality"))
        {
            int handAttackDamage = context.HandCardsByInstanceId.Values
                .Where(static c => c.Type == CardType.Attack)
                .Sum(static c => c.GetEstimatedDamage());
            score += Math.Min(handAttackDamage, 30) * 4;
            score += context.ActiveBuild?.IsLocked == true ? 28 : 18;
        }

        if (card.AppliesPower("OstySacrifice"))
        {
            bool hasOstySummon = context.HandCardsByInstanceId.Values
                .Any(static c => c.AppliesPower("SummonOsty") || c.AppliesPower("SummonAlly"));
            if (hasOstySummon || context.ActiveBuild?.Profile.BuildId == "osty")
            {
                score += 48;
            }

            score += context.ActiveBuild?.IsLocked == true ? 30 : 18;
        }

        if (HasCardToken(card, "DANSEMACABRE"))
        {
            int highCostCards = context.HandCardsByInstanceId.Values
                .Count(static c => c.EffectiveCost >= 2);
            score += highCostCards * 10;
            score += context.ActiveBuild?.IsLocked == true ? 18 : 10;
        }

        if (CombatBuildRoleEvaluator.IsWeakStarterStrike(card) &&
            HasAffordableHigherBuildDamageAction(context, action, damage))
        {
            score -= 52;
        }

        return score;
    }

    private static IEnumerable<DeterministicEnemyState> GetFutureValueTargets(DeterministicCombatContext context, AiLegalActionOption action, ResolvedCardView card)
    {
        if (IsAllEnemiesDebuff(card, "Poison") || card.Targeting == TargetType.AllEnemies)
        {
            return context.EnemiesById.Values;
        }

        if (!string.IsNullOrEmpty(action.TargetId) &&
            context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy))
        {
            return [enemy];
        }

        return context.EnemiesById.Values;
    }

    private static int EstimatePoisonDamage(int poison, int turns)
    {
        int damage = 0;
        for (int turn = 0; turn < turns && poison - turn > 0; turn++)
        {
            damage += poison - turn;
        }

        return damage;
    }

    private static int GetFutureHorizon(DeterministicCombatContext context)
    {
        if (context.IsBossCombat)
        {
            return 5;
        }

        if (context.IsEliteCombat)
        {
            return 4;
        }

        int enemyHp = context.EnemiesById.Values.Sum(static enemy => enemy.CurrentHp + enemy.Block);
        return enemyHp >= 55 ? 4 : 3;
    }

    private static int GetEnemyPowerAmount(DeterministicEnemyState enemy, string token)
    {
        string normalizedToken = AiBuildProfileAnalyzer.Normalize(token);
        foreach (KeyValuePair<string, int> pair in enemy.PowerAmounts)
        {
            if (AiBuildProfileAnalyzer.Normalize(pair.Key).Contains(normalizedToken, StringComparison.Ordinal))
            {
                return Math.Max(pair.Value, 0);
            }
        }

        return 0;
    }

    private static bool IsLongHorizonPower(ResolvedCardView card)
    {
        return HasCardToken(
            card,
            "DEMONFORM",
            "ECHOFORM",
            "WRAITHFORM",
            "CREATIVEAI",
            "BARRICADE",
            "NOXIOUS",
            "DEFRA",
            "CAPACITOR",
            "VOIDFORM",
            "REAPERFORM",
            "RUPTURE",
            "INFLAME");
    }

    private static bool HasUnplayedAffordableEngineSetup(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) ||
                !IsAffordableAtEnergy(context, candidate, context.Energy))
            {
                continue;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            if (CombatBuildRoleEvaluator.IsEngineSetup(context, card))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldPreferAffordableBuildSetupOverStarterStrike(
        DeterministicCombatContext context,
        AiLegalActionOption currentAction,
        ResolvedCardView currentCard)
    {
        if (context.ActiveBuild == null ||
            !CombatBuildRoleEvaluator.IsWeakStarterStrike(currentCard) ||
            CanActionLikelyKill(context, currentAction, currentCard) ||
            HasSurvivalEmergency(context))
        {
            return false;
        }

        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) ||
                !IsAffordableAtEnergy(context, candidate, context.Energy))
            {
                continue;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            if (card == null || CombatBuildRoleEvaluator.IsWeakStarterStrike(card))
            {
                continue;
            }

            CombatBuildRole role = CombatBuildRoleEvaluator.Classify(context, card);
            if (role == CombatBuildRole.Setup ||
                CombatBuildRoleEvaluator.IsEngineSetup(context, card) ||
                CombatBuildRoleEvaluator.IsCoreBuildPower(context, card) ||
                CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card) ||
                CombatBuildRoleEvaluator.IsOstyGuardCard(card))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnplayedAffordableNonSoulAction(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) ||
                !IsAffordableAtEnergy(context, candidate, context.Energy))
            {
                continue;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            if (card != null && !CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnplayedAffordableNonSlyAction(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) ||
                !IsAffordableAtEnergy(context, candidate, context.Energy))
            {
                continue;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            if (card != null && !CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnplayedAffordableSilentEngineAction(DeterministicCombatContext context, AiLegalActionOption currentAction)
    {
        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) ||
                !IsAffordableAtEnergy(context, candidate, context.Energy))
            {
                continue;
            }

            ResolvedCardView? card = ResolveCard(context, candidate);
            if (CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card) ||
                CombatBuildRoleEvaluator.IsSilentPoisonSetupCard(card) ||
                CombatBuildRoleEvaluator.IsSilentShivSetupCard(card))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAffordableHigherBuildDamageAction(DeterministicCombatContext context, AiLegalActionOption currentAction, int currentDamage)
    {
        foreach (AiLegalActionOption candidate in context.LegalActions)
        {
            if (string.Equals(candidate.ActionId, currentAction.ActionId, StringComparison.Ordinal) ||
                !IsAffordableAtEnergy(context, candidate, context.Energy))
            {
                continue;
            }

            ResolvedCardView? candidateCard = ResolveCard(context, candidate);
            if (candidateCard == null || CombatBuildRoleEvaluator.IsWeakStarterStrike(candidateCard))
            {
                continue;
            }

            int candidateDamage = candidateCard.GetEstimatedDamage();
            if (candidateDamage >= Math.Max(12, currentDamage + 8) &&
                IsBuildRelevantDamageCard(context, candidateCard))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanActionLikelyKill(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        ResolvedCardView card)
    {
        int damage = card.GetEstimatedDamage();
        if (damage <= 0)
        {
            return false;
        }

        if (IsAllEnemiesDamage(card))
        {
            return context.EnemiesById.Any(pair =>
                damage >= GetTeamAdjustedEnemyHp(context, pair.Key, pair.Value));
        }

        return !string.IsNullOrEmpty(action.TargetId) &&
               context.EnemiesById.TryGetValue(action.TargetId, out DeterministicEnemyState? enemy) &&
               damage >= GetTeamAdjustedEnemyHp(context, action.TargetId, enemy);
    }

    private static bool HasSurvivalEmergency(DeterministicCombatContext context)
    {
        int uncoveredDamage = EstimateUncoveredEndTurnDamage(context);
        return uncoveredDamage >= context.CurrentHp ||
               uncoveredDamage >= Math.Max(12, context.CurrentHp / 3);
    }

    private static bool IsBuildRelevantDamageCard(DeterministicCombatContext context, ResolvedCardView card)
    {
        if (context.ActiveBuild == null)
        {
            return false;
        }

        return CombatBuildRoleEvaluator.IsCoreBuildCard(context, card) ||
               CombatBuildRoleEvaluator.IsEnginePayoff(context, card) ||
               HasCardToken(card, context.ActiveBuild.Profile.CoreCards.Concat(context.ActiveBuild.Profile.SupportCards).ToArray()) ||
               (context.ActiveBuild.Profile.CharacterId == "necrobinder" && card.Targeting == TargetType.Osty);
    }

    private static bool HasCardToken(ResolvedCardView card, params string[] tokens)
    {
        string normalizedName = AiBuildProfileAnalyzer.Normalize(card.Name);
        string normalizedId = AiBuildProfileAnalyzer.Normalize(card.CardId);
        foreach (string token in tokens)
        {
            string normalizedToken = AiBuildProfileAnalyzer.Normalize(token);
            if (!string.IsNullOrEmpty(normalizedToken) &&
                (normalizedName.Contains(normalizedToken, StringComparison.Ordinal) ||
                 normalizedId.Contains(normalizedToken, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
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

    private static int GetActorPowerAmountAnyCase(DeterministicCombatContext context, params string[] possibleKeys)
    {
        foreach (string key in possibleKeys)
        {
            if (context.ActorPowerAmounts.TryGetValue(key, out int amount))
            {
                return amount;
            }
        }

        return 0;
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
               || potionId.Contains("FIRE", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("EXPLOSIVE", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("DEMISE", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("STRENGTH", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("FLEX", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("DUPLICATOR", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("ENERGY", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefensivePotion(AiLegalActionOption action)
    {
        string potionId = action.CardId ?? string.Empty;
        return potionId.Contains("BLOCK", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("ARMOR", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("DEXTERITY", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("WEAK", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("HEART_OF_IRON", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("IRON", StringComparison.OrdinalIgnoreCase)
               || potionId.Contains("CURE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraveDanger(DeterministicCombatContext context)
    {
        int uncoveredDamage = EstimateUncoveredEndTurnDamage(context);
        return uncoveredDamage >= Math.Max(10, context.CurrentHp / 3) || uncoveredDamage >= context.CurrentHp;
    }

    private static int EstimateUncoveredEndTurnDamage(DeterministicCombatContext context)
    {
        return Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
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
