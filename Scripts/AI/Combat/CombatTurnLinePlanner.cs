using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace AITeammate.Scripts;

internal sealed class CombatTurnLinePlanner
{
    private const int MaxLineLength = 3;
    private const int BeamWidth = 6;
    private const int ExcessBlockTempoPenaltyPerPoint = 10;

    private readonly CombatActionScorer _scorer;

    public CombatTurnLinePlanner(CombatActionScorer scorer)
    {
        _scorer = scorer;
    }

    public CombatLinePlan? BuildBestPlan(DeterministicCombatContext context)
    {
        List<PlannableAction> actions = context.LegalActions
            .Select(action => BuildPlannableAction(context, action))
            .Where(action => !action.IsEndTurn &&
                             !(action.IsPotion && action.ImmediateScore.TotalScore <= 0) &&
                             !action.IsZeroEnergyXCost &&
                             !IsRedundantBlockOnlyAtEnergy(context, action, context.Energy, totalBlockGained: 0, damagePreventedByKills: 0, damagePreventedByWeak: 0))
            .ToList();
        if (actions.Count == 0)
        {
            return null;
        }

        List<LineNode> frontier = actions
            .OrderByDescending(action => ScoreInitialBeamCandidate(context, action, actions))
            .ThenBy(static action => action.Action.ActionId, StringComparer.Ordinal)
            .Take(BeamWidth)
            .Select(action => CreateInitialNode(context, action))
            .ToList();
        List<LineNode> bestNodes = frontier.ToList();

        for (int depth = 1; depth < MaxLineLength; depth++)
        {
            List<LineNode> nextFrontier = [];
            foreach (LineNode node in frontier)
            {
                if (node.StopExpanding)
                {
                    continue;
                }

                foreach (PlannableAction candidate in actions)
                {
                    if (!node.CanApply(context, candidate))
                    {
                        continue;
                    }

                    nextFrontier.Add(node.Apply(context, candidate));
                }
            }

            if (nextFrontier.Count == 0)
            {
                break;
            }

            frontier = nextFrontier
                .OrderByDescending(node => node.ComputeTerminalScore(context, actions))
                .ThenBy(node => string.Join("|", node.ActionIds), StringComparer.Ordinal)
                .Take(BeamWidth)
                .ToList();
            bestNodes.AddRange(frontier);
        }

        LineNode best = bestNodes
            .OrderByDescending(node => node.ComputeTerminalScore(context, actions))
            .ThenBy(node => string.Join("|", node.ActionIds), StringComparer.Ordinal)
            .First();

        return new CombatLinePlan
        {
            ActionIds = best.ActionIds.ToList(),
            Score = best.ComputeTerminalScore(context, actions),
            EstimatedDamageDealt = best.TotalDamageDealt,
            EstimatedDamageTaken = best.EstimatedDamageTaken(context),
            EstimatedBlockAfterEnemyTurn = best.EstimatedBlockAfterEnemyTurn(context)
        };
    }

    private PlannableAction BuildPlannableAction(DeterministicCombatContext context, AiLegalActionOption action)
    {
        CombatActionScore immediateScore = _scorer.Score(context, action);
        ResolvedCardView? card = ResolveCard(context, action);
        int damage = card.GetEstimatedDamage();
        int block = card.GetEstimatedBlock();
        int cardsDrawn = card.GetCardsDrawn();
        int energyGain = Math.Max(card.GetEnergyGain(), 0);
        bool isAllEnemiesDamage = IsAllEnemiesDamage(card);
        int vulnerable = card.GetEnemyVulnerableAmount();
        int weak = card.GetEnemyWeakAmount();
        int selfStrength = card.GetSelfStrengthAmount();
        int selfTemporaryStrength = card.GetSelfTemporaryStrengthAmount();
        int selfDexterity = card.GetSelfDexterityAmount();
        int selfTemporaryDexterity = card.GetSelfTemporaryDexterityAmount();
        bool isBlockOnlyDefense = IsBlockOnlyDefense(card);
        bool isHighVariance = cardsDrawn > 0;
        bool isPotion = string.Equals(action.ActionType, AiTeammateActionKind.UsePotion.ToString(), StringComparison.Ordinal);
        bool isOffensivePotion = IsOffensivePotion(action);
        bool appliesVulnerable = vulnerable > 0;
        CombatBuildRole buildRole = CombatBuildRoleEvaluator.Classify(context, card);
        bool isEngineSetup = CombatBuildRoleEvaluator.IsEngineSetup(context, card);
        bool isEnginePayoff = CombatBuildRoleEvaluator.IsEnginePayoff(context, card);
        bool isCoreBuildCard = CombatBuildRoleEvaluator.IsCoreBuildCard(context, card);
        bool isCoreBuildPower = CombatBuildRoleEvaluator.IsCoreBuildPower(context, card);
        bool isNecrobinderFreeSoulDraw = CombatBuildRoleEvaluator.IsNecrobinderFreeSoulDraw(context, card);
        bool isNecrobinderEarlySoulCard = CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card);
        bool isPriorityDrawBlockCard = CombatBuildRoleEvaluator.IsPriorityDrawBlockCard(card);
        bool isOstyGuardCard = CombatBuildRoleEvaluator.IsOstyGuardCard(card);
        bool isSilentSlyEngineCard = CombatBuildRoleEvaluator.IsSilentSlyEngineCard(context, card);
        bool isSilentShivSetupCard = CombatBuildRoleEvaluator.IsSilentShivSetupCard(card);
        bool isWeakStarterStrike = CombatBuildRoleEvaluator.IsWeakStarterStrike(card);

        if (isOffensivePotion && vulnerable <= 0)
        {
            vulnerable = 1;
            appliesVulnerable = true;
        }

        return new PlannableAction
        {
            Action = action,
            ImmediateScore = immediateScore,
            EnergyCost = action.EnergyCost ?? 0,
            Damage = damage,
            DamageHits = Math.Max(GetDamageHits(card), 1),
            IsAllEnemiesDamage = isAllEnemiesDamage,
            Block = block,
            CardsDrawn = cardsDrawn,
            EnergyGain = energyGain,
            Vulnerable = vulnerable,
            Weak = weak,
            SelfStrength = Math.Max(0, selfStrength - selfTemporaryStrength),
            SelfTemporaryStrength = selfTemporaryStrength,
            SelfDexterity = Math.Max(0, selfDexterity - selfTemporaryDexterity),
            SelfTemporaryDexterity = selfTemporaryDexterity,
            IsHighVariance = isHighVariance,
            IsEndTurn = string.Equals(action.ActionType, AiTeammateActionKind.EndTurn.ToString(), StringComparison.Ordinal),
            IsPotion = isPotion,
            IsOffensivePotion = isOffensivePotion,
            IsXCost = card?.HasXCost == true,
            IsZeroEnergyXCost = card?.HasXCost == true && context.Energy <= 0,
            AppliesVulnerable = appliesVulnerable,
            IsSetup = immediateScore.Category is CombatActionCategory.PowerSetup or CombatActionCategory.Utility || buildRole == CombatBuildRole.Setup,
            IsEngineSetup = isEngineSetup,
            IsEnginePayoff = isEnginePayoff,
            IsCoreBuildCard = isCoreBuildCard,
            IsCoreBuildPower = isCoreBuildPower,
            IsNecrobinderFreeSoulDraw = isNecrobinderFreeSoulDraw,
            IsNecrobinderEarlySoulCard = isNecrobinderEarlySoulCard,
            IsPriorityDrawBlockCard = isPriorityDrawBlockCard,
            IsOstyGuardCard = isOstyGuardCard,
            IsSilentSlyEngineCard = isSilentSlyEngineCard,
            IsSilentShivSetupCard = isSilentShivSetupCard,
            IsBlockOnlyDefense = isBlockOnlyDefense,
            IsWeakStarterStrike = isWeakStarterStrike,
            BuildRole = buildRole,
            ConsumptionKey = BuildConsumptionKey(action)
        };
    }

    private static int ScoreInitialBeamCandidate(
        DeterministicCombatContext context,
        PlannableAction action,
        IReadOnlyList<PlannableAction> actions)
    {
        int score = action.ImmediateScore.TotalScore;
        score += ScoreInitialPotionBeamBonus(context, action);
        bool hasAffordableSetup = actions.Any(candidate =>
            !ReferenceEquals(candidate, action) &&
            !candidate.IsEndTurn &&
            (candidate.IsXCost ? context.Energy > 0 : candidate.EnergyCost <= context.Energy) &&
            (candidate.IsEngineSetup ||
             candidate.IsCoreBuildPower ||
             candidate.IsNecrobinderEarlySoulCard ||
             candidate.IsSilentSlyEngineCard ||
             candidate.IsOstyGuardCard ||
             candidate.BuildRole == CombatBuildRole.Setup ||
             candidate.CardsDrawn >= 2));

        if (action.IsEngineSetup || action.IsCoreBuildPower)
        {
            score += 90;
        }

        if (action.IsNecrobinderEarlySoulCard || action.IsSilentSlyEngineCard)
        {
            score += 85;
        }

        if (action.IsOstyGuardCard || action.BuildRole == CombatBuildRole.Setup)
        {
            score += 55;
        }

        if (action.CardsDrawn > 0 && context.Energy > action.EnergyCost)
        {
            score += action.CardsDrawn * 28;
        }

        if (action.IsPriorityDrawBlockCard)
        {
            score += 30;
        }

        if (action.IsWeakStarterStrike && hasAffordableSetup)
        {
            score -= context.ActiveBuild?.IsLocked == true ? 80 : 55;
        }

        return score;
    }

    private static int ScoreInitialPotionBeamBonus(DeterministicCombatContext context, PlannableAction action)
    {
        if (!action.IsPotion)
        {
            return 0;
        }

        int uncoveredDamage = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock);
        bool severePressure = uncoveredDamage >= 14 || uncoveredDamage >= Math.Max(8, context.CurrentHp / 3);
        bool lowHp = context.CurrentHp <= Math.Max(18, context.Actor.Creature.MaxHp / 3);
        int score = 45;

        if (context.IsEliteOrBossCombat)
        {
            score += 45;
        }

        if (severePressure)
        {
            score += 55;
        }
        else if (uncoveredDamage >= 8)
        {
            score += 35;
        }

        if (lowHp)
        {
            score += 45;
        }

        if (action.IsOffensivePotion && (context.IsEliteOrBossCombat || severePressure))
        {
            score += 20;
        }

        return score;
    }

    private static LineNode CreateInitialNode(DeterministicCombatContext context, PlannableAction action)
    {
        LineNode node = new(context.Energy);
        return node.Apply(context, action);
    }

    private static ResolvedCardView? ResolveCard(DeterministicCombatContext context, AiLegalActionOption action)
    {
        if (!string.IsNullOrEmpty(action.CardInstanceId) &&
            context.HandCardsByInstanceId.TryGetValue(action.CardInstanceId, out ResolvedCardView? card))
        {
            return card;
        }

        return null;
    }

    private static int GetDamageHits(ResolvedCardView? card)
    {
        if (card == null)
        {
            return 1;
        }

        return card.Effects
            .Where(static effect => effect.Kind == EffectKind.DealDamage)
            .Sum(static effect => Math.Max(effect.RepeatCount, 1));
    }

    private static bool IsAllEnemiesDamage(ResolvedCardView? card)
    {
        return card?.Targeting == TargetType.AllEnemies ||
               card?.Effects.Any(static effect => effect.Kind == EffectKind.DealDamage && effect.TargetScope == TargetScope.AllEnemies) == true;
    }

    private static string BuildConsumptionKey(AiLegalActionOption action)
    {
        if (!string.IsNullOrEmpty(action.CardInstanceId))
        {
            return $"card:{action.CardInstanceId}";
        }

        if (string.Equals(action.ActionType, AiTeammateActionKind.UsePotion.ToString(), StringComparison.Ordinal))
        {
            return $"potion:{action.ActionId}";
        }

        return $"action:{action.ActionId}";
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

    private static bool IsZeroEnergyXCost(DeterministicCombatContext context, AiLegalActionOption action)
    {
        ResolvedCardView? card = ResolveCard(context, action);
        return card?.HasXCost == true && context.Energy <= 0;
    }

    private static bool IsAffordableAtEnergy(DeterministicCombatContext context, AiLegalActionOption action, int energyRemaining)
    {
        ResolvedCardView? card = ResolveCard(context, action);
        return card?.HasXCost == true
            ? energyRemaining > 0
            : (action.EnergyCost ?? 0) <= energyRemaining;
    }

    private static bool IsRedundantBlockOnlyAtEnergy(
        DeterministicCombatContext context,
        PlannableAction action,
        int energyRemaining,
        int totalBlockGained,
        int damagePreventedByKills,
        int damagePreventedByWeak)
    {
        if (context.ValuesExcessBlock || !action.IsBlockOnlyDefense || !IsAffordableActionAtEnergy(action, energyRemaining))
        {
            return false;
        }

        int incomingDamage = GetBlockableIncomingDamage(context, damagePreventedByKills, damagePreventedByWeak);
        int uncoveredDamage = Math.Max(0, incomingDamage - context.CurrentBlock - totalBlockGained);
        return uncoveredDamage <= 0;
    }

    private static bool IsRedundantBlockOnlyOptionAtEnergy(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        int energyRemaining,
        int totalBlockGained,
        int damagePreventedByKills,
        int damagePreventedByWeak)
    {
        ResolvedCardView? card = ResolveCard(context, action);
        if (context.ValuesExcessBlock || !IsBlockOnlyDefense(card) || !IsAffordableAtEnergy(context, action, energyRemaining))
        {
            return false;
        }

        int incomingDamage = GetBlockableIncomingDamage(context, damagePreventedByKills, damagePreventedByWeak);
        int uncoveredDamage = Math.Max(0, incomingDamage - context.CurrentBlock - totalBlockGained);
        return uncoveredDamage <= 0;
    }

    private static int GetBlockableIncomingDamage(DeterministicCombatContext context, int damagePreventedByKills, int damagePreventedByWeak)
    {
        return Math.Max(0, context.IncomingDamage - damagePreventedByKills - damagePreventedByWeak) + context.HandEndTurnDamage;
    }

    private static int GetExpectedLifeLoss(
        DeterministicCombatContext context,
        int totalBlockGained,
        int damagePreventedByKills,
        int damagePreventedByWeak)
    {
        int blockableIncoming = GetBlockableIncomingDamage(context, damagePreventedByKills, damagePreventedByWeak);
        int availableBlock = context.CurrentBlock + totalBlockGained;
        return Math.Max(0, blockableIncoming - availableBlock) + context.HandEndTurnHpLoss;
    }


    private static bool IsAffordableActionAtEnergy(PlannableAction action, int energyRemaining)
    {
        return action.IsXCost ? energyRemaining > 0 : action.EnergyCost <= energyRemaining;
    }

    private static bool IsBlockOnlyDefense(ResolvedCardView? card)
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

    private sealed class PlannableAction
    {
        public required AiLegalActionOption Action { get; init; }

        public required CombatActionScore ImmediateScore { get; init; }

        public required string ConsumptionKey { get; init; }

        public int EnergyCost { get; init; }

        public int Damage { get; init; }

        public int DamageHits { get; init; }

        public bool IsAllEnemiesDamage { get; init; }

        public int Block { get; init; }

        public int CardsDrawn { get; init; }

        public int EnergyGain { get; init; }

        public int Vulnerable { get; init; }

        public int Weak { get; init; }

        public int SelfStrength { get; init; }

        public int SelfTemporaryStrength { get; init; }

        public int SelfDexterity { get; init; }

        public int SelfTemporaryDexterity { get; init; }

        public bool IsHighVariance { get; init; }

        public bool IsEndTurn { get; init; }

        public bool IsPotion { get; init; }

        public bool IsOffensivePotion { get; init; }

        public bool IsXCost { get; init; }

        public bool IsZeroEnergyXCost { get; init; }

        public bool AppliesVulnerable { get; init; }

        public bool IsSetup { get; init; }

        public bool IsEngineSetup { get; init; }

        public bool IsEnginePayoff { get; init; }

        public bool IsCoreBuildCard { get; init; }

        public bool IsCoreBuildPower { get; init; }

        public bool IsNecrobinderFreeSoulDraw { get; init; }

        public bool IsNecrobinderEarlySoulCard { get; init; }

        public bool IsPriorityDrawBlockCard { get; init; }

        public bool IsOstyGuardCard { get; init; }

        public bool IsSilentSlyEngineCard { get; init; }

        public bool IsSilentShivSetupCard { get; init; }

        public bool IsBlockOnlyDefense { get; init; }

        public bool IsWeakStarterStrike { get; init; }

        public CombatBuildRole BuildRole { get; init; }
    }

    private sealed class LineNode
    {
        private readonly HashSet<string> _consumedKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _damageByTargetId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _deadEnemyIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _vulnerableTargets = new(StringComparer.Ordinal);
        private readonly HashSet<string> _weakenedTargets = new(StringComparer.Ordinal);

        public LineNode(int energyRemaining)
        {
            EnergyRemaining = energyRemaining;
        }

        private LineNode(LineNode other)
        {
            EnergyRemaining = other.EnergyRemaining;
            ActionIds = other.ActionIds.ToList();
            _consumedKeys = new HashSet<string>(other._consumedKeys, StringComparer.Ordinal);
            _damageByTargetId = new Dictionary<string, int>(other._damageByTargetId, StringComparer.Ordinal);
            _deadEnemyIds = new HashSet<string>(other._deadEnemyIds, StringComparer.Ordinal);
            _vulnerableTargets = new HashSet<string>(other._vulnerableTargets, StringComparer.Ordinal);
            _weakenedTargets = new HashSet<string>(other._weakenedTargets, StringComparer.Ordinal);
            BaseScore = other.BaseScore;
            TotalDamageDealt = other.TotalDamageDealt;
            TotalBlockGained = other.TotalBlockGained;
            SetupScore = other.SetupScore;
            DamagePreventedByKills = other.DamagePreventedByKills;
            DamagePreventedByWeak = other.DamagePreventedByWeak;
            StrengthGained = other.StrengthGained;
            TemporaryStrengthGained = other.TemporaryStrengthGained;
            DexterityGained = other.DexterityGained;
            TemporaryDexterityGained = other.TemporaryDexterityGained;
            CardsDrawn = other.CardsDrawn;
            EnergyGenerated = other.EnergyGenerated;
            StopExpanding = other.StopExpanding;
        }

        public int EnergyRemaining { get; private set; }

        public List<string> ActionIds { get; } = [];

        public int BaseScore { get; private set; }

        public int TotalDamageDealt { get; private set; }

        public int TotalBlockGained { get; private set; }

        public int SetupScore { get; private set; }

        public int DamagePreventedByKills { get; private set; }

        public int DamagePreventedByWeak { get; private set; }

        public int StrengthGained { get; private set; }

        public int TemporaryStrengthGained { get; private set; }

        public int DexterityGained { get; private set; }

        public int TemporaryDexterityGained { get; private set; }

        public int CardsDrawn { get; private set; }

        public int EnergyGenerated { get; private set; }

        public bool StopExpanding { get; private set; }

        public bool CanApply(DeterministicCombatContext context, PlannableAction action)
        {
            if (_consumedKeys.Contains(action.ConsumptionKey))
            {
                return false;
            }

            if (action.IsXCost && EnergyRemaining <= 0)
            {
                return false;
            }

            if (!action.IsXCost && action.EnergyCost > EnergyRemaining)
            {
                return false;
            }

            if (action.IsZeroEnergyXCost)
            {
                return false;
            }

            if (IsRedundantBlockOnlyAtEnergy(context, action, EnergyRemaining, TotalBlockGained, DamagePreventedByKills, DamagePreventedByWeak))
            {
                return false;
            }

            return true;
        }

        public LineNode Apply(DeterministicCombatContext context, PlannableAction action)
        {
            AiCombatStatusWeights status = context.CombatConfig.Combat.StatusWeights;
            AiCombatResourceWeights resource = context.CombatConfig.Combat.ResourceWeights;
            int spentEnergy = action.IsXCost ? EnergyRemaining : action.EnergyCost;
            LineNode next = new(this)
            {
                EnergyRemaining = Math.Max(0, EnergyRemaining - spentEnergy + action.EnergyGain)
            };
            next.ActionIds.Add(action.Action.ActionId);
            next._consumedKeys.Add(action.ConsumptionKey);
            next.BaseScore += action.ImmediateScore.TotalScore;
            next.BaseScore += next.ScoreBuildRotation(context, action);
            next.BaseScore += next.ScorePotionTiming(context, action);
            next.EnergyGenerated += action.EnergyGain;
            next.CardsDrawn += action.CardsDrawn;

            int effectiveBlock = action.Block + next.DexterityGained + next.TemporaryDexterityGained;
            if (effectiveBlock > 0)
            {
                int incomingBeforeBlock = GetBlockableIncomingDamage(context, DamagePreventedByKills, DamagePreventedByWeak);
                int uncoveredBeforeBlock = Math.Max(0, incomingBeforeBlock - context.CurrentBlock - TotalBlockGained);
                int excessBlock = Math.Max(0, effectiveBlock - uncoveredBeforeBlock);
                next.TotalBlockGained += effectiveBlock;
                if (!context.ValuesExcessBlock && excessBlock > 0)
                {
                    next.SetupScore -= excessBlock * ExcessBlockTempoPenaltyPerPoint;
                }
            }

            if (!string.IsNullOrEmpty(action.Action.TargetId))
            {
                if (action.AppliesVulnerable)
                {
                    next._vulnerableTargets.Add(action.Action.TargetId);
                }

                if (action.Weak > 0 && context.EnemiesById.TryGetValue(action.Action.TargetId, out DeterministicEnemyState? weakenedEnemy))
                {
                    next._weakenedTargets.Add(action.Action.TargetId);
                    next.DamagePreventedByWeak += Math.Max(1, weakenedEnemy.IncomingDamage / 4);
                }
            }

            if (action.Damage > 0 && action.IsAllEnemiesDamage)
            {
                foreach (KeyValuePair<string, DeterministicEnemyState> enemyEntry in context.EnemiesById)
                {
                    if (next._deadEnemyIds.Contains(enemyEntry.Key))
                    {
                        continue;
                    }

                    int dealtDamage = action.Damage + (next.StrengthGained + next.TemporaryStrengthGained) * action.DamageHits;
                    if (next._vulnerableTargets.Contains(enemyEntry.Key))
                    {
                        dealtDamage += (int)Math.Ceiling(dealtDamage * 0.5m);
                    }

                    int effectiveEnemyHp = GetTeamAdjustedEnemyHp(context, enemyEntry.Key, enemyEntry.Value);
                    int damageBefore = next._damageByTargetId.GetValueOrDefault(enemyEntry.Key);
                    int remainingEnemyHp = Math.Max(0, effectiveEnemyHp - damageBefore);
                    next.TotalDamageDealt += Math.Min(dealtDamage, remainingEnemyHp);
                    next._damageByTargetId[enemyEntry.Key] = damageBefore + dealtDamage;
                    if (next._damageByTargetId[enemyEntry.Key] >= effectiveEnemyHp && effectiveEnemyHp > 0)
                    {
                        next._deadEnemyIds.Add(enemyEntry.Key);
                        next.DamagePreventedByKills += enemyEntry.Value.IncomingDamage;
                    }
                }
            }
            else if (action.Damage > 0 && !string.IsNullOrEmpty(action.Action.TargetId))
            {
                int dealtDamage = action.Damage + (next.StrengthGained + next.TemporaryStrengthGained) * action.DamageHits;
                if (next._vulnerableTargets.Contains(action.Action.TargetId))
                {
                    dealtDamage += (int)Math.Ceiling(dealtDamage * 0.5m);
                }

                if (!next._deadEnemyIds.Contains(action.Action.TargetId) &&
                    context.EnemiesById.TryGetValue(action.Action.TargetId, out DeterministicEnemyState? enemy))
                {
                    int effectiveEnemyHp = GetTeamAdjustedEnemyHp(context, action.Action.TargetId, enemy);
                    int damageBefore = next._damageByTargetId.GetValueOrDefault(action.Action.TargetId);
                    int remainingEnemyHp = Math.Max(0, effectiveEnemyHp - damageBefore);
                    next.TotalDamageDealt += Math.Min(dealtDamage, remainingEnemyHp);
                    next._damageByTargetId[action.Action.TargetId] = damageBefore + dealtDamage;
                    if (next._damageByTargetId[action.Action.TargetId] >= effectiveEnemyHp && effectiveEnemyHp > 0)
                    {
                        next._deadEnemyIds.Add(action.Action.TargetId);
                        next.DamagePreventedByKills += enemy.IncomingDamage;
                    }
                }
                else
                {
                    next.TotalDamageDealt += dealtDamage;
                    next._damageByTargetId[action.Action.TargetId] = next._damageByTargetId.GetValueOrDefault(action.Action.TargetId) + dealtDamage;
                }
            }

            next.StrengthGained += action.SelfStrength;
            next.TemporaryStrengthGained += action.SelfTemporaryStrength;
            next.DexterityGained += action.SelfDexterity;
            next.TemporaryDexterityGained += action.SelfTemporaryDexterity;

            if (action.IsSetup)
            {
                next.SetupScore += resource.SetupActionBonus;
            }

            if (action.SelfStrength > 0 || action.SelfTemporaryStrength > 0)
            {
                next.SetupScore += CountAffordableUnconsumedActions(next, context, requireDamage: true) * (action.SelfStrength * status.SetupPersistentStrengthValue + action.SelfTemporaryStrength * status.SetupTemporaryStrengthValue);
            }

            if (action.SelfDexterity > 0 || action.SelfTemporaryDexterity > 0)
            {
                next.SetupScore += CountAffordableUnconsumedActions(next, context, requireBlock: true) * (action.SelfDexterity * status.SetupPersistentDexterityValue + action.SelfTemporaryDexterity * status.SetupTemporaryDexterityValue);
            }

            if (action.CardsDrawn > 0)
            {
                int futurePlayableActions = CountAffordableUnconsumedActions(next, context);
                if (next.EnergyRemaining > 0 && futurePlayableActions > 0)
                {
                    next.SetupScore += action.CardsDrawn * resource.SetupDrawValueWhenPlayable;
                }
                else
                {
                    next.SetupScore -= action.CardsDrawn * resource.SetupDrawPenaltyWhenNotPlayable;
                }

                if (action.IsNecrobinderEarlySoulCard)
                {
                    next.SetupScore += ActionIds.Count <= 1 ? 90 : 18;
                }

                if (action.IsPriorityDrawBlockCard)
                {
                    next.SetupScore += ActionIds.Count <= 1 ? 20 : 4;
                }

                if (action.IsSilentSlyEngineCard)
                {
                    next.SetupScore += ActionIds.Count <= 1 ? 62 : 16;
                    if (next.EnergyRemaining > 0)
                    {
                        next.SetupScore += ActionIds.Count <= 1 ? 24 : 6;
                    }
                }
            }

            if (action.EnergyGain > 0)
            {
                next.SetupScore += action.EnergyGain * resource.SetupEnergyGainValue;
            }

            next.StopExpanding = action.IsHighVariance || next.ActionIds.Count >= MaxLineLength;
            return next;
        }

        private int ScoreBuildRotation(DeterministicCombatContext context, PlannableAction action)
        {
            if (action.IsNecrobinderEarlySoulCard)
            {
                bool soulIsFirstAction = ActionIds.Count <= 1;
                int soulScore = soulIsFirstAction ? 140 : 24;
                if (EnergyRemaining > 0)
                {
                    soulScore += soulIsFirstAction ? 36 : 8;
                }

                return soulScore;
            }

            if (action.IsSilentSlyEngineCard)
            {
                bool slyIsFirstAction = ActionIds.Count <= 1;
                int slyScore = slyIsFirstAction ? 112 : 22;
                if (EnergyRemaining > 0)
                {
                    slyScore += slyIsFirstAction ? 34 : 8;
                }

                return slyScore;
            }

            if (context.ActiveBuild == null || action.BuildRole == CombatBuildRole.None)
            {
                return 0;
            }

            int score = 0;
            bool hasSetup = SetupScore > 0 || StrengthGained > 0 || DexterityGained > 0 || CardsDrawn > 0 || EnergyGenerated > 0;
            bool isFirstAction = ActionIds.Count <= 1;
            switch (action.BuildRole)
            {
                case CombatBuildRole.Setup:
                    score += isFirstAction ? 18 : 8;
                    if (action.IsCoreBuildCard)
                    {
                        score += isFirstAction ? 12 : 6;
                    }

                    if (action.IsCoreBuildPower)
                    {
                        score += isFirstAction ? 36 : 20;
                    }

                    if (action.IsEngineSetup)
                    {
                        score += isFirstAction ? 18 : 10;
                    }

                    if (action.IsOstyGuardCard)
                    {
                        score += isFirstAction ? 26 : 10;
                    }

                    if (action.IsSilentShivSetupCard)
                    {
                        score += isFirstAction ? 30 : 12;
                    }

                    break;
                case CombatBuildRole.Cycle:
                    score += isFirstAction ? 10 : 4;
                    if (action.IsNecrobinderFreeSoulDraw)
                    {
                        score += isFirstAction ? 28 : 8;
                    }

                    if (action.IsPriorityDrawBlockCard)
                    {
                        score += isFirstAction ? 18 : 4;
                    }

                    break;
                case CombatBuildRole.Payoff:
                    score += hasSetup ? 18 : -8;
                    if (action.IsEnginePayoff && !hasSetup)
                    {
                        score -= 18;
                    }

                    score += (StrengthGained + TemporaryStrengthGained) * Math.Max(action.DamageHits, 1) * 4;
                    break;
                case CombatBuildRole.Finisher:
                    score += TotalDamageDealt > 0 || hasSetup ? 10 : 2;
                    break;
                case CombatBuildRole.Defense:
                    int uncoveredDamage = Math.Max(0, GetBlockableIncomingDamage(context, DamagePreventedByKills, DamagePreventedByWeak) - context.CurrentBlock - TotalBlockGained);
                    score += uncoveredDamage > 0 ? 12 : 2;
                    break;
                case CombatBuildRole.Avoid:
                    score -= context.ActiveBuild.IsLocked ? 28 : 12;
                    break;
            }

            if (action.BuildRole is CombatBuildRole.Payoff or CombatBuildRole.Finisher &&
                context.ActiveBuild.Profile.BuildId is "strength" or "shiv" or "claw" or "strike" &&
                StrengthGained + TemporaryStrengthGained <= 0 &&
                isFirstAction)
            {
                score -= 10;
            }

            return score;
        }

        private int ScorePotionTiming(DeterministicCombatContext context, PlannableAction action)
        {
            if (!action.IsPotion)
            {
                return 0;
            }

            int incoming = GetBlockableIncomingDamage(context, DamagePreventedByKills, DamagePreventedByWeak);
            int uncoveredDamage = Math.Max(0, incoming - context.CurrentBlock - TotalBlockGained);
            bool severePressure = uncoveredDamage >= 14 || uncoveredDamage >= Math.Max(8, context.CurrentHp / 3);
            bool lowHp = context.CurrentHp <= Math.Max(18, context.Actor.Creature.MaxHp / 3);
            bool isFirstAction = ActionIds.Count <= 1;
            int score = isFirstAction ? 70 : -10;

            if (context.IsEliteOrBossCombat)
            {
                score += isFirstAction ? 35 : 10;
            }

            if (severePressure)
            {
                score += isFirstAction ? 45 : 18;
            }
            else if (uncoveredDamage >= 8)
            {
                score += isFirstAction ? 25 : 8;
            }

            if (lowHp)
            {
                score += 30;
            }

            if (action.IsOffensivePotion)
            {
                score += isFirstAction ? 24 : -6;
            }

            return score;
        }

        private static int GetTeamAdjustedEnemyHp(DeterministicCombatContext context, string targetId, DeterministicEnemyState enemy)
        {
            return CombatTeamDamageReservationPolicy.GetEffectiveEnemyHp(context, targetId, enemy);
        }

        private int CountAffordableUnconsumedActions(LineNode node, DeterministicCombatContext context, bool requireDamage = false, bool requireBlock = false)
        {
            return context.LegalActions.Count(action =>
            {
                if (string.IsNullOrEmpty(action.ActionId) ||
                    node._consumedKeys.Contains(BuildConsumptionKey(action)) ||
                    string.Equals(action.ActionType, AiTeammateActionKind.EndTurn.ToString(), StringComparison.Ordinal) ||
                    !IsAffordableAtEnergy(context, action, node.EnergyRemaining) ||
                    IsRedundantBlockOnlyOptionAtEnergy(context, action, node.EnergyRemaining, node.TotalBlockGained, node.DamagePreventedByKills, node.DamagePreventedByWeak))
                {
                    return false;
                }

                ResolvedCardView? card = ResolveCard(context, action);
                if (requireDamage)
                {
                    return card?.HasEffect(EffectKind.DealDamage) == true;
                }

                if (requireBlock)
                {
                    return card?.HasEffect(EffectKind.GainBlock) == true;
                }

                return true;
            });
        }

        public int EstimatedDamageTaken(DeterministicCombatContext context)
        {
            return GetExpectedLifeLoss(context, TotalBlockGained, DamagePreventedByKills, DamagePreventedByWeak);
        }

        public int EstimatedBlockAfterEnemyTurn(DeterministicCombatContext context)
        {
            int incomingDamage = GetBlockableIncomingDamage(context, DamagePreventedByKills, DamagePreventedByWeak);
            int leftoverBlock = Math.Max(0, context.CurrentBlock + TotalBlockGained - incomingDamage);
            return context.HasBlockRetention ? leftoverBlock : 0;
        }

        public int ComputeTerminalScore(DeterministicCombatContext context, IReadOnlyList<PlannableAction> actions)
        {
            AiCharacterCombatTuning tuning = context.CombatConfig.Combat;
            AiCombatCoreWeights core = tuning.CoreWeights;
            AiCombatStatusWeights status = tuning.StatusWeights;
            AiCombatResourceWeights resource = tuning.ResourceWeights;
            AiCombatRiskProfile risk = tuning.RiskProfile;
            int incomingDamage = GetBlockableIncomingDamage(context, DamagePreventedByKills, DamagePreventedByWeak);
            int availableBlock = context.CurrentBlock + TotalBlockGained;
            int damageTaken = Math.Max(0, incomingDamage - availableBlock) + context.HandEndTurnHpLoss;
            int preventedByBlock = Math.Min(incomingDamage, availableBlock);
            int leftoverBlock = EstimatedBlockAfterEnemyTurn(context);
            int remainingAffordableActions = actions.Count(action =>
                !_consumedKeys.Contains(action.ConsumptionKey) &&
                !action.IsEndTurn &&
                IsAffordableActionAtEnergy(action, EnergyRemaining) &&
                !IsRedundantBlockOnlyAtEnergy(context, action, EnergyRemaining, TotalBlockGained, DamagePreventedByKills, DamagePreventedByWeak));

            int score = BaseScore;
            score += risk.ApplySurvivalWeight(preventedByBlock * risk.PreventedDamageValuePerPoint);
            score -= risk.ApplySurvivalWeight(damageTaken * risk.DamageTakenPenaltyPerPoint);
            score += DamagePreventedByKills * risk.KillPreventionValuePerPoint;
            score += DamagePreventedByWeak * risk.WeakPreventionValuePerPoint;
            score += risk.ApplyAttackWeight(TotalDamageDealt * core.LineDamageValuePerPoint);
            score += SetupScore;
            score += ScoreUnplayedBuildRotationPenalty(actions);
            score += risk.ApplyDefenseWeight(leftoverBlock * core.LeftoverBlockValuePerPoint);
            score += _deadEnemyIds.Count * risk.DeadEnemyReward;
            score += StrengthGained * status.LinePersistentStrengthValue;
            score += TemporaryStrengthGained * status.LineTemporaryStrengthValue;
            score += DexterityGained * status.LinePersistentDexterityValue;
            score += TemporaryDexterityGained * (incomingDamage > 0 ? status.LineTemporaryDexterityThreatenedValue : status.LineTemporaryDexteritySafeValue);
            score += EnergyGenerated * resource.LineEnergyGeneratedValue;
            score += CardsDrawn * (remainingAffordableActions > 0 ? resource.LineCardsDrawnValueWhenUsable : -resource.LineCardsDrawnPenaltyWhenNotUsable);
            score -= EnergyRemaining * resource.RemainingEnergyPenalty;
            score -= remainingAffordableActions * resource.RemainingAffordableActionsPenalty;

            if (damageTaken == 0 && preventedByBlock > 0)
            {
                score += risk.PerfectDefenseBonus;
            }

            if (damageTaken > 0 && TotalBlockGained == 0 && DamagePreventedByWeak == 0)
            {
                score -= risk.ExposedDamageWithoutDefensePenalty;
            }

            return score;
        }

        private int ScoreUnplayedBuildRotationPenalty(IReadOnlyList<PlannableAction> actions)
        {
            bool hasUnplayedSetup = actions.Any(action =>
                !_consumedKeys.Contains(action.ConsumptionKey) &&
                action.BuildRole == CombatBuildRole.Setup &&
                (action.IsXCost ? EnergyRemaining > 0 : action.EnergyCost <= EnergyRemaining));
            bool hasUnplayedEngineSetup = actions.Any(action =>
                !_consumedKeys.Contains(action.ConsumptionKey) &&
                action.IsEngineSetup &&
                (action.IsXCost ? EnergyRemaining > 0 : action.EnergyCost <= EnergyRemaining));
            bool hasUnplayedCoreBuildPower = actions.Any(action =>
                !_consumedKeys.Contains(action.ConsumptionKey) &&
                action.IsCoreBuildPower &&
                (action.IsXCost ? EnergyRemaining > 0 : action.EnergyCost <= EnergyRemaining));
            bool playedPayoff = ActionIds.Any(actionId =>
                actions.Any(action => string.Equals(action.Action.ActionId, actionId, StringComparison.Ordinal) &&
                                      action.BuildRole is CombatBuildRole.Payoff or CombatBuildRole.Finisher));
            bool playedNonCoreBuildAction = ActionIds.Any(actionId =>
                actions.Any(action => string.Equals(action.Action.ActionId, actionId, StringComparison.Ordinal) &&
                                      !action.IsCoreBuildPower &&
                                      action.BuildRole is CombatBuildRole.Setup or CombatBuildRole.Cycle or CombatBuildRole.Payoff or CombatBuildRole.Finisher));
            bool playedEnginePayoff = ActionIds.Any(actionId =>
                actions.Any(action => string.Equals(action.Action.ActionId, actionId, StringComparison.Ordinal) &&
                                      action.IsEnginePayoff));
            bool playedWeakStarterStrike = ActionIds.Any(actionId =>
                actions.Any(action => string.Equals(action.Action.ActionId, actionId, StringComparison.Ordinal) &&
                                      action.IsWeakStarterStrike));
            int score = hasUnplayedSetup && playedPayoff ? -24 : 0;
            if (hasUnplayedSetup && playedWeakStarterStrike)
            {
                score -= 48;
            }

            if (hasUnplayedCoreBuildPower && playedNonCoreBuildAction)
            {
                score -= 42;
            }

            if (hasUnplayedEngineSetup && playedEnginePayoff)
            {
                score -= 36;
            }

            return score;
        }
    }
}
