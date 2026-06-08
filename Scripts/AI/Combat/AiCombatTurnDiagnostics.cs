using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;

namespace AITeammate.Scripts;

internal static class AiCombatTurnDiagnostics
{
    public static IReadOnlyList<string> BuildNotes(
        DeterministicCombatContext context,
        AiLegalActionOption chosenAction,
        ResolvedCardView? chosenCard,
        CombatActionScore chosenScore,
        IReadOnlyList<CombatActionScore> rankedScores,
        CombatLinePlan? plan)
    {
        try
        {
            CombatTurnDiagnosticSnapshot snapshot = BuildSnapshot(context, chosenAction, chosenCard, chosenScore, rankedScores, plan);
            return snapshot.Notes;
        }
        catch (Exception ex)
        {
            return [$"diagnosis_failed={ex.Message}"];
        }
    }

    public static void LogDecision(
        DeterministicCombatContext context,
        AiLegalActionOption chosenAction,
        ResolvedCardView? chosenCard,
        CombatActionScore chosenScore,
        IReadOnlyList<CombatActionScore> rankedScores,
        CombatLinePlan? plan)
    {
        try
        {
            CombatTurnDiagnosticSnapshot snapshot = BuildSnapshot(context, chosenAction, chosenCard, chosenScore, rankedScores, plan);
            string notes = snapshot.Notes.Count > 0 ? string.Join(", ", snapshot.Notes) : "ok";
            string line = plan != null ? string.Join(">", plan.ActionIds) : chosenAction.ActionId;
            Log.Info($"[AITeammate][Diagnosis] Combat turn player={context.Actor.NetId} build={context.ActiveBuild?.Profile.BuildId ?? "none"} chosen={chosenAction.ActionId} card={chosenCard?.CardId ?? chosenAction.CardId ?? "none"} rank={snapshot.ChosenRank} score={chosenScore.TotalScore} bestSingle={snapshot.BestSingleActionId}:{snapshot.BestSingleScore} line=[{line}] lineScore={plan?.Score.ToString() ?? "none"} energy={context.Energy}->{snapshot.EstimatedEnergyAfterLine} incoming={context.TotalExpectedEndTurnLifeLoss} estTaken={plan?.EstimatedDamageTaken.ToString() ?? "unknown"} maxDamage={snapshot.BestDamageActionId}:{snapshot.BestDamage} bestBlock={snapshot.BestBlockActionId}:{snapshot.BestBlock} notes=[{notes}]");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Diagnosis] Failed to diagnose combat turn player={context.Actor.NetId}: {ex.Message}");
        }
    }

    private static CombatTurnDiagnosticSnapshot BuildSnapshot(
        DeterministicCombatContext context,
        AiLegalActionOption chosenAction,
        ResolvedCardView? chosenCard,
        CombatActionScore chosenScore,
        IReadOnlyList<CombatActionScore> rankedScores,
        CombatLinePlan? plan)
    {
        Dictionary<string, CombatActionScore> scoresByActionId = rankedScores
            .GroupBy(static score => score.ActionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        List<ActionSnapshot> actions = context.LegalActions
            .Select(action => BuildActionSnapshot(context, action, scoresByActionId))
            .ToList();
        HashSet<string> plannedActionIds = plan?.ActionIds.ToHashSet(StringComparer.Ordinal) ?? [];
        ActionSnapshot? chosen = actions.FirstOrDefault(action => string.Equals(action.ActionId, chosenAction.ActionId, StringComparison.Ordinal));
        ActionSnapshot? bestSingle = actions
            .Where(static action => !action.IsEndTurn)
            .OrderByDescending(static action => action.Score)
            .ThenBy(static action => action.ActionId, StringComparer.Ordinal)
            .FirstOrDefault();
        ActionSnapshot? bestDamage = actions
            .Where(static action => action.IsAffordable && action.Damage > 0)
            .OrderByDescending(static action => action.Damage)
            .ThenByDescending(static action => action.Score)
            .ThenBy(static action => action.ActionId, StringComparer.Ordinal)
            .FirstOrDefault();
        ActionSnapshot? bestBlock = actions
            .Where(static action => action.IsAffordable && action.Block > 0)
            .OrderByDescending(static action => action.Block)
            .ThenByDescending(static action => action.Score)
            .ThenBy(static action => action.ActionId, StringComparer.Ordinal)
            .FirstOrDefault();
        ActionSnapshot? bestPotion = actions
            .Where(static action => action.IsPotion)
            .OrderByDescending(static action => action.Score)
            .ThenBy(static action => action.ActionId, StringComparer.Ordinal)
            .FirstOrDefault();
        int chosenRank = rankedScores
            .Select((score, index) => new { score.ActionId, Rank = index + 1 })
            .FirstOrDefault(candidate => string.Equals(candidate.ActionId, chosenAction.ActionId, StringComparison.Ordinal))
            ?.Rank ?? 0;
        int estimatedEnergyAfterLine = EstimateEnergyAfterLine(context, actions, plan, chosenAction);
        List<string> notes = [];

        if ((chosen?.IsEndTurn == true || chosenScore.Category == CombatActionCategory.EndTurn) && context.Energy > 0)
        {
            IReadOnlyList<ActionSnapshot> affordableNonEnd = actions
                .Where(static action => !action.IsEndTurn && action.IsAffordable)
                .OrderByDescending(static action => action.Score)
                .Take(3)
                .ToList();
            if (affordableNonEnd.Count > 0)
            {
                notes.Add($"end_turn_with_energy={context.Energy};affordable={string.Join("|", affordableNonEnd.Select(static action => $"{action.ActionId}:{action.Score}"))}");
            }
        }

        if (bestSingle != null && bestSingle.Score >= chosenScore.TotalScore + 35 && !plannedActionIds.Contains(bestSingle.ActionId))
        {
            notes.Add($"single_score_gap best={bestSingle.ActionId}:{bestSingle.Score} chosen={chosenAction.ActionId}:{chosenScore.TotalScore}");
        }

        if (bestDamage != null &&
            chosen != null &&
            bestDamage.Damage >= chosen.Damage + 10 &&
            !plannedActionIds.Contains(bestDamage.ActionId) &&
            !IsStrongSetupOrDefense(chosen, context))
        {
            notes.Add($"damage_left best={bestDamage.ActionId}:{bestDamage.Damage} chosenDamage={chosen.Damage}");
        }

        int uncoveredBeforeActions = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
        int estimatedTaken = plan?.EstimatedDamageTaken ?? uncoveredBeforeActions;
        if (estimatedTaken > 0 && bestBlock != null && !plannedActionIds.Contains(bestBlock.ActionId))
        {
            notes.Add($"block_left estTaken={estimatedTaken} bestBlock={bestBlock.ActionId}:{bestBlock.Block}");
        }

        if (chosen != null &&
            chosen.Block > 0 &&
            !context.ValuesExcessBlock &&
            context.TotalBlockableIncomingDamage <= context.CurrentBlock &&
            chosen.Damage <= 0 &&
            chosen.CardsDrawn <= 0 &&
            chosen.EnergyGain <= 0)
        {
            notes.Add($"overblock_candidate block={chosen.Block} incomingCovered={context.CurrentBlock}/{context.TotalBlockableIncomingDamage}");
        }

        AddUnplayedCoreAndEngineNotes(context, actions, plannedActionIds, notes);
        AddPotionNotes(context, bestPotion, plannedActionIds, notes);
        AddDrawTimingNotes(context, actions, plannedActionIds, chosen, notes);

        if (estimatedEnergyAfterLine > 0)
        {
            IReadOnlyList<ActionSnapshot> remainingAffordable = actions
                .Where(action => !plannedActionIds.Contains(action.ActionId) &&
                                 !action.IsEndTurn &&
                                 IsAffordableAtEnergy(action, estimatedEnergyAfterLine) &&
                                 action.Score > 0)
                .OrderByDescending(static action => action.Score)
                .Take(3)
                .ToList();
            if (remainingAffordable.Count > 0)
            {
                notes.Add($"energy_left={estimatedEnergyAfterLine};remaining={string.Join("|", remainingAffordable.Select(static action => $"{action.ActionId}:{action.Score}"))}");
            }
        }

        return new CombatTurnDiagnosticSnapshot
        {
            Notes = notes,
            ChosenRank = chosenRank,
            EstimatedEnergyAfterLine = estimatedEnergyAfterLine,
            BestSingleActionId = bestSingle?.ActionId ?? "none",
            BestSingleScore = bestSingle?.Score ?? 0,
            BestDamageActionId = bestDamage?.ActionId ?? "none",
            BestDamage = bestDamage?.Damage ?? 0,
            BestBlockActionId = bestBlock?.ActionId ?? "none",
            BestBlock = bestBlock?.Block ?? 0
        };
    }

    private static void AddUnplayedCoreAndEngineNotes(
        DeterministicCombatContext context,
        IReadOnlyList<ActionSnapshot> actions,
        HashSet<string> plannedActionIds,
        List<string> notes)
    {
        ActionSnapshot? unplayedCorePower = actions
            .Where(action => action.IsAffordable &&
                             action.IsCoreBuildPower &&
                             !plannedActionIds.Contains(action.ActionId))
            .OrderByDescending(static action => action.Score)
            .FirstOrDefault();
        if (unplayedCorePower != null && (context.IsEliteOrBossCombat || context.Energy >= unplayedCorePower.EnergyCost))
        {
            notes.Add($"core_power_left={unplayedCorePower.ActionId}:{unplayedCorePower.CardId}");
        }

        ActionSnapshot? unplayedEngineSetup = actions
            .Where(action => action.IsAffordable &&
                             action.IsEngineSetup &&
                             !plannedActionIds.Contains(action.ActionId))
            .OrderByDescending(static action => action.Score)
            .FirstOrDefault();
        if (unplayedEngineSetup != null)
        {
            notes.Add($"engine_setup_left={unplayedEngineSetup.ActionId}:{unplayedEngineSetup.CardId}");
        }
    }

    private static void AddPotionNotes(
        DeterministicCombatContext context,
        ActionSnapshot? bestPotion,
        HashSet<string> plannedActionIds,
        List<string> notes)
    {
        if (bestPotion == null || plannedActionIds.Contains(bestPotion.ActionId))
        {
            return;
        }

        int uncovered = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
        bool dangerous = context.CurrentHp <= Math.Max(12, uncovered + 4) || uncovered >= 18 || context.IsEliteOrBossCombat;
        if (dangerous && bestPotion.Score >= 20)
        {
            notes.Add($"potion_left danger={dangerous} potion={bestPotion.ActionId}:{bestPotion.Score}");
        }
    }

    private static void AddDrawTimingNotes(
        DeterministicCombatContext context,
        IReadOnlyList<ActionSnapshot> actions,
        HashSet<string> plannedActionIds,
        ActionSnapshot? chosen,
        List<string> notes)
    {
        if (chosen?.CardsDrawn > 0 && context.Energy <= 0)
        {
            notes.Add($"late_draw_no_energy={chosen.ActionId}");
        }

        ActionSnapshot? earlyDraw = actions
            .Where(action => action.IsAffordable &&
                             action.CardsDrawn > 0 &&
                             action.EnergyCost <= Math.Max(0, context.Energy - 1) &&
                             !plannedActionIds.Contains(action.ActionId))
            .OrderByDescending(static action => action.Score)
            .FirstOrDefault();
        if (earlyDraw != null)
        {
            notes.Add($"draw_left_while_energy_available={earlyDraw.ActionId}:draw{earlyDraw.CardsDrawn}");
        }
    }

    private static ActionSnapshot BuildActionSnapshot(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        IReadOnlyDictionary<string, CombatActionScore> scoresByActionId)
    {
        ResolvedCardView? card = ResolveCard(context, action);
        bool isEndTurn = string.Equals(action.ActionType, AiTeammateActionKind.EndTurn.ToString(), StringComparison.Ordinal);
        bool isPotion = string.Equals(action.ActionType, AiTeammateActionKind.UsePotion.ToString(), StringComparison.Ordinal);
        int energyCost = action.EnergyCost ?? 0;
        bool isXCost = card?.HasXCost == true;
        return new ActionSnapshot
        {
            ActionId = action.ActionId,
            ActionType = action.ActionType,
            CardId = card?.CardId ?? action.CardId ?? "none",
            EnergyCost = energyCost,
            EnergyGain = card.GetEnergyGain(),
            Damage = card.GetEstimatedDamage(),
            Block = card.GetEstimatedBlock(),
            CardsDrawn = card.GetCardsDrawn(),
            IsAffordable = isEndTurn || IsAffordableAtEnergy(energyCost, isXCost, context.Energy),
            IsEndTurn = isEndTurn,
            IsPotion = isPotion,
            IsStrongSetup = card != null && CombatBuildRoleEvaluator.Classify(context, card) is CombatBuildRole.Setup or CombatBuildRole.Cycle,
            IsCoreBuildPower = CombatBuildRoleEvaluator.IsCoreBuildPower(context, card),
            IsEngineSetup = CombatBuildRoleEvaluator.IsEngineSetup(context, card),
            Score = scoresByActionId.TryGetValue(action.ActionId, out CombatActionScore? score) ? score.TotalScore : 0,
            IsXCost = isXCost
        };
    }

    private static int EstimateEnergyAfterLine(
        DeterministicCombatContext context,
        IReadOnlyList<ActionSnapshot> actions,
        CombatLinePlan? plan,
        AiLegalActionOption chosenAction)
    {
        IReadOnlyList<string> actionIds = plan?.ActionIds ?? [chosenAction.ActionId];
        int energy = context.Energy;
        foreach (string actionId in actionIds)
        {
            ActionSnapshot? action = actions.FirstOrDefault(candidate => string.Equals(candidate.ActionId, actionId, StringComparison.Ordinal));
            if (action == null || action.IsPotion || action.IsEndTurn)
            {
                continue;
            }

            energy = action.IsXCost
                ? Math.Max(0, action.EnergyGain)
                : Math.Max(0, energy - action.EnergyCost + action.EnergyGain);
        }

        return energy;
    }

    private static bool IsStrongSetupOrDefense(ActionSnapshot action, DeterministicCombatContext context)
    {
        if (action.IsCoreBuildPower || action.IsEngineSetup || action.IsStrongSetup)
        {
            return true;
        }

        int uncovered = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
        return uncovered > 0 && (action.Block > 0 || action.CardId.Contains("WEAK", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAffordableAtEnergy(ActionSnapshot action, int energy)
    {
        return IsAffordableAtEnergy(action.EnergyCost, action.IsXCost, energy);
    }

    private static bool IsAffordableAtEnergy(int energyCost, bool isXCost, int energy)
    {
        return isXCost ? energy > 0 : energyCost <= energy;
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

    private sealed class ActionSnapshot
    {
        public required string ActionId { get; init; }
        public required string ActionType { get; init; }
        public required string CardId { get; init; }
        public int EnergyCost { get; init; }
        public int EnergyGain { get; init; }
        public int Damage { get; init; }
        public int Block { get; init; }
        public int CardsDrawn { get; init; }
        public bool IsAffordable { get; init; }
        public bool IsEndTurn { get; init; }
        public bool IsPotion { get; init; }
        public bool IsStrongSetup { get; init; }
        public bool IsCoreBuildPower { get; init; }
        public bool IsEngineSetup { get; init; }
        public bool IsXCost { get; init; }
        public int Score { get; init; }
    }

    private sealed class CombatTurnDiagnosticSnapshot
    {
        public required IReadOnlyList<string> Notes { get; init; }
        public int ChosenRank { get; init; }
        public int EstimatedEnergyAfterLine { get; init; }
        public required string BestSingleActionId { get; init; }
        public int BestSingleScore { get; init; }
        public required string BestDamageActionId { get; init; }
        public int BestDamage { get; init; }
        public required string BestBlockActionId { get; init; }
        public int BestBlock { get; init; }
    }
}
