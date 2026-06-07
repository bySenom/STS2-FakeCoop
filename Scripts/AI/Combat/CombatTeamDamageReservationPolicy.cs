using System;

namespace AITeammate.Scripts;

internal static class CombatTeamDamageReservationPolicy
{
    public static int GetEffectiveEnemyHp(DeterministicCombatContext context, string targetId, DeterministicEnemyState enemy)
    {
        int rawHp = Math.Max(0, enemy.CurrentHp + enemy.Block);
        int pendingTeamDamage = GetEffectivePendingDamage(context, targetId, rawHp);
        return Math.Max(0, rawHp - pendingTeamDamage);
    }

    private static int GetEffectivePendingDamage(DeterministicCombatContext context, string targetId, int rawHp)
    {
        if (rawHp <= 0 ||
            !context.PendingTeamDamageByEnemyId.TryGetValue(targetId, out int pendingTeamDamage) ||
            pendingTeamDamage <= 0)
        {
            return 0;
        }

        // In single-target fights, especially bosses, reserved damage made later bots
        // think there was no useful damage left. There is no better target to swap to there.
        if (context.EnemiesById.Count <= 1)
        {
            return 0;
        }

        if (context.IsEliteOrBossCombat && rawHp >= 40)
        {
            return Math.Min(pendingTeamDamage, rawHp / 2);
        }

        return Math.Min(pendingTeamDamage, rawHp);
    }
}
