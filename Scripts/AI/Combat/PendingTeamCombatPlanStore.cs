using System;
using System.Collections.Generic;
using System.Linq;

namespace AITeammate.Scripts;

internal static class PendingTeamCombatPlanStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PendingTeamDamagePlan> DamagePlansByKey = new(StringComparer.Ordinal);

    public static void RegisterDamagePlan(ulong actorId, string actionId, string targetId, int damage, int combatRound)
    {
        if (damage <= 0 || string.IsNullOrWhiteSpace(targetId) || !targetId.StartsWith("creature_", StringComparison.Ordinal))
        {
            return;
        }

        lock (Gate)
        {
            DamagePlansByKey[BuildKey(actorId, actionId)] = new PendingTeamDamagePlan
            {
                ActorId = actorId,
                ActionId = actionId,
                TargetId = targetId,
                Damage = damage,
                CombatRound = combatRound
            };
        }
    }

    public static void ClearPlan(ulong actorId, string actionId)
    {
        lock (Gate)
        {
            DamagePlansByKey.Remove(BuildKey(actorId, actionId));
        }
    }

    public static IReadOnlyDictionary<string, int> GetPendingDamageByTarget(ulong actorIdToExclude, int combatRound)
    {
        lock (Gate)
        {
            foreach (string staleKey in DamagePlansByKey
                         .Where(pair => pair.Value.CombatRound != combatRound)
                         .Select(static pair => pair.Key)
                         .ToList())
            {
                DamagePlansByKey.Remove(staleKey);
            }

            return DamagePlansByKey.Values
                .Where(plan => plan.ActorId != actorIdToExclude && plan.CombatRound == combatRound)
                .GroupBy(static plan => plan.TargetId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Sum(plan => plan.Damage), StringComparer.Ordinal);
        }
    }

    private static string BuildKey(ulong actorId, string actionId)
    {
        return $"{actorId}:{actionId}";
    }

    private sealed class PendingTeamDamagePlan
    {
        public required ulong ActorId { get; init; }

        public required string ActionId { get; init; }

        public required string TargetId { get; init; }

        public required int Damage { get; init; }

        public required int CombatRound { get; init; }
    }
}
