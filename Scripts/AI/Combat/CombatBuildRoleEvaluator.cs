using System;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace AITeammate.Scripts;

internal static class CombatBuildRoleEvaluator
{
    public static CombatBuildRole Classify(DeterministicCombatContext context, ResolvedCardView? card)
    {
        if (card == null || context.ActiveBuild == null)
        {
            return CombatBuildRole.None;
        }

        AiBuildArchetype profile = context.ActiveBuild.Profile;
        if (AiBuildProfileAnalyzer.IsAvoidCard(profile, card))
        {
            return CombatBuildRole.Avoid;
        }

        string buildId = profile.BuildId;
        if (IsEngineSetup(context, card))
        {
            return CombatBuildRole.Setup;
        }

        if (IsDefenseBuild(buildId) && (card.GetEstimatedBlock() > 0 || card.GetEnemyWeakAmount() > 0))
        {
            return CombatBuildRole.Defense;
        }

        if (IsSetupCard(buildId, card))
        {
            return CombatBuildRole.Setup;
        }

        if (IsPayoffCard(buildId, card))
        {
            return CombatBuildRole.Payoff;
        }

        if (IsCycleCard(card))
        {
            return CombatBuildRole.Cycle;
        }

        if (card.GetEstimatedDamage() >= 18 || card.GetEnemyVulnerableAmount() > 0)
        {
            return CombatBuildRole.Finisher;
        }

        if (card.GetEstimatedBlock() > 0 || card.GetEnemyWeakAmount() > 0)
        {
            return CombatBuildRole.Defense;
        }

        return CombatBuildRole.None;
    }

    public static bool IsEngineSetup(DeterministicCombatContext context, ResolvedCardView? card)
    {
        if (card == null || context.ActiveBuild == null)
        {
            return false;
        }

        string buildId = context.ActiveBuild.Profile.BuildId;
        if (IsOrbSetupBuild(buildId) && IsOrbSetupCard(card))
        {
            return true;
        }

        return IsStarSetupBuild(buildId) && IsStarSetupCard(card);
    }

    public static bool IsEnginePayoff(DeterministicCombatContext context, ResolvedCardView? card)
    {
        if (card == null || context.ActiveBuild == null)
        {
            return false;
        }

        string buildId = context.ActiveBuild.Profile.BuildId;
        if (IsOrbSetupBuild(buildId) && IsOrbPayoffCard(card))
        {
            return true;
        }

        return IsStarSetupBuild(buildId) && IsStarPayoffCard(card);
    }

    public static bool IsCoreBuildCard(DeterministicCombatContext context, ResolvedCardView? card)
    {
        return card != null &&
               context.ActiveBuild != null &&
               AiBuildProfileAnalyzer.IsCoreCard(context.ActiveBuild.Profile, card);
    }

    public static bool IsCoreBuildPower(DeterministicCombatContext context, ResolvedCardView? card)
    {
        return IsCoreBuildCard(context, card) && card!.Type == CardType.Power;
    }

    private static bool IsSetupCard(string buildId, ResolvedCardView card)
    {
        if (card.Type == CardType.Power ||
            card.GetSelfStrengthAmount() > 0 ||
            card.GetSelfDexterityAmount() > 0 ||
            card.GetEnemyVulnerableAmount() > 0)
        {
            return true;
        }

        return buildId switch
        {
            "poison" => HasToken(card, "NOXIOUS", "POISON", "BOUNCING"),
            "sly" or "grand_finale" or "claw" => IsCycleCard(card),
            "frost" or "lightning" or "dark_orb" or "creative_ai" => HasToken(card, "DEFRA", "CAPACITOR", "ECHO", "STORM", "MACHINE") || IsOrbSetupCard(card),
            "osty" or "soul" or "doom" or "reaper" => HasToken(card, "INVOKE", "BORROWED", "DIRGE", "REANIMATE", "HAUNT", "CAPTURE", "COUNTDOWN", "REAPERFORM"),
            "forge" or "star_burst" or "void_form" or "bombardment" => HasToken(card, "CONQUEROR", "SEEKING", "VOID") || IsStarSetupCard(card),
            "barricade" or "exhaust" or "bloodletting" => HasToken(card, "BARRICADE", "CORRUPTION", "DARKEMBRACE", "FEELNOPAIN", "RUPTURE"),
            _ => false
        };
    }

    private static bool IsPayoffCard(string buildId, ResolvedCardView card)
    {
        if (card.GetEstimatedDamage() <= 0 && card.GetEstimatedBlock() <= 0)
        {
            return false;
        }

        return buildId switch
        {
            "strength" => HasToken(card, "HEAVYBLADE", "SWORD", "TWIN", "POMMEL", "WHIRLWIND", "BLUDGEON"),
            "barricade" => HasToken(card, "BODYSLAM"),
            "strike" => HasToken(card, "STRIKE", "PERFECTED"),
            "shiv" or "envenom" => HasToken(card, "BLADE", "SHIV", "FINISHER", "FANO"),
            "claw" => HasToken(card, "CLAW", "ALLFORONE", "SCRAPE"),
            "lightning" => HasToken(card, "ZAP", "BALL", "ELECTRODYNAMICS", "LIGHTNING"),
            "dark_orb" => HasToken(card, "DARKNESS", "MULTICAST", "DUALCAST", "CONSUMING"),
            "forge" => HasToken(card, "FALLING", "SWORD", "KNOCKOUT", "PHOTON", "SEEKING"),
            "star_burst" or "void_form" or "bombardment" => HasToken(card, "FALLING", "SEVEN", "BIGBANG", "METEOR", "GAMMA", "BOMBARDMENT"),
            "osty" or "soul" or "doom" or "reaper" => HasToken(card, "SOULSTORM", "DEATHMARCH", "SCYTHE", "ERADICATE", "UNLEASH"),
            _ => false
        };
    }

    private static bool IsOrbSetupBuild(string buildId)
    {
        return buildId is "lightning" or "frost" or "dark_orb" or "creative_ai";
    }

    private static bool IsStarSetupBuild(string buildId)
    {
        return buildId is "forge" or "star_burst" or "void_form" or "bombardment";
    }

    private static bool IsOrbSetupCard(ResolvedCardView card)
    {
        return HasToken(
            card,
            "ZAP",
            "BALLLIGHTNING",
            "COLDSNAP",
            "COOLHEADED",
            "GLACIER",
            "DARKNESS",
            "CHILL",
            "DOOMANDGLOOM",
            "ELECTRODYNAMICS",
            "STATICDISCHARGE",
            "TEMPEST",
            "STORM",
            "CAPACITOR");
    }

    private static bool IsOrbPayoffCard(ResolvedCardView card)
    {
        return HasToken(card, "DUALCAST", "MULTICAST", "RECURSION", "CONSUME", "CONSUMING");
    }

    private static bool IsStarSetupCard(ResolvedCardView card)
    {
        return HasToken(
            card,
            "GUIDINGSTAR",
            "FALLINGSTAR",
            "STARDUST",
            "SEVENSTARS",
            "GLOW",
            "CONVERGENCE",
            "VENERATE");
    }

    private static bool IsStarPayoffCard(ResolvedCardView card)
    {
        return HasToken(card, "BIGBANG", "BLACKHOLE", "METEOR", "BOMBARDMENT", "GAMMA", "PHOTON", "KNOCKOUT");
    }

    private static bool IsCycleCard(ResolvedCardView card)
    {
        return card.GetCardsDrawn() > 0 || card.GetEnergyGain() > 0 || card.EffectiveCost == 0;
    }

    private static bool IsDefenseBuild(string buildId)
    {
        return buildId is "barricade" or "frost" or "combo_execution";
    }

    private static bool HasToken(ResolvedCardView card, params string[] tokens)
    {
        string normalizedName = AiBuildProfileAnalyzer.Normalize(card.Name);
        string normalizedId = AiBuildProfileAnalyzer.Normalize(card.CardId);
        foreach (string token in tokens)
        {
            string normalizedToken = AiBuildProfileAnalyzer.Normalize(token);
            if (normalizedName.Contains(normalizedToken, StringComparison.Ordinal) ||
                normalizedId.Contains(normalizedToken, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
