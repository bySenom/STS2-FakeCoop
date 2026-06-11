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
        if (IsNecrobinderEarlySoulCard(context, card) || IsSilentSlyEngineCard(context, card) || IsEngineSetup(context, card))
        {
            return CombatBuildRole.Setup;
        }

        if (buildId == "osty" && IsOstyGuardCard(card))
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
        if (IsSilentEngineBuild(buildId) && IsSilentSlyEngineCard(context, card))
        {
            return true;
        }

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
        if (IsSilentEngineBuild(buildId) && IsSilentSlyPayoffCard(card))
        {
            return true;
        }

        if (IsOrbSetupBuild(buildId) && IsOrbPayoffCard(card))
        {
            return true;
        }

        return IsStarSetupBuild(buildId) && IsStarPayoffCard(card);
    }

    public static bool IsOrbSetupBuild(DeterministicCombatContext context)
    {
        return context.ActiveBuild != null && IsOrbSetupBuild(context.ActiveBuild.Profile.BuildId);
    }

    public static bool IsStarSetupBuild(DeterministicCombatContext context)
    {
        return context.ActiveBuild != null && IsStarSetupBuild(context.ActiveBuild.Profile.BuildId);
    }

    public static bool IsOstyGuardCard(ResolvedCardView? card)
    {
        return card != null && HasToken(card, "BODYGUARD", "GUARDIAN", "PROTECT");
    }

    public static bool IsNecrobinderFreeSoulDraw(DeterministicCombatContext context, ResolvedCardView? card)
    {
        return IsNecrobinderEarlySoulCard(context, card) && card!.GetCardsDrawn() >= 1;
    }

    public static bool IsSilentSlyEngineCard(DeterministicCombatContext context, ResolvedCardView? card)
    {
        if (card == null || !IsSilentContext(context))
        {
            return false;
        }

        bool activeSlyBuild = context.ActiveBuild?.Profile.BuildId is "sly" or "grand_finale";
        bool knownSlyCard = HasToken(
            card,
            "SLY",
            "MASTERPLANNER",
            "ACROBATICS",
            "PREPARED",
            "REFLEX",
            "TACTICIAN",
            "CALCULATEDGAMBLE",
            "CONCENTRATE",
            "TOOLSOFTHETRADE",
            "TOOLS");
        bool usefulEngineText = card.GetCardsDrawn() > 0 ||
                                card.GetEnergyGain() > 0 ||
                                HasToken(card, "DISCARD", "SNEAKY", "GAMBLE");
        return knownSlyCard || (activeSlyBuild && usefulEngineText);
    }

    public static bool IsSilentSlyPayoffCard(ResolvedCardView? card)
    {
        return card != null && HasToken(card, "GRANDFINALE", "SNEAKY", "EVISCERATE");
    }

    public static bool IsSilentPoisonSetupCard(ResolvedCardView? card)
    {
        return card != null && HasToken(card, "NOXIOUS", "DEADLYPOISON", "ACCELERANT", "BOUNCING", "POISON", "CATALYST");
    }

    public static bool IsSilentShivSetupCard(ResolvedCardView? card)
    {
        return card != null && HasToken(card, "ACCURACY", "AFTERIMAGE", "INFINITEBLADES", "ENVENOM");
    }

    public static bool IsSilentShivPayoffCard(ResolvedCardView? card)
    {
        return card != null && HasToken(card, "BLADEDANCE", "CLOAKANDDAGGER", "SHIV", "FINISHER", "FANOFKNIVES");
    }

    public static bool IsNecrobinderEarlySoulCard(DeterministicCombatContext context, ResolvedCardView? card)
    {
        if (card == null)
        {
            return false;
        }

        if (context.ActiveBuild?.Profile.CharacterId != "necrobinder")
        {
            string characterId = context.CombatConfig.CharacterId;
            if (!string.Equals(characterId, "necrobinder", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (card.EffectiveCost > 0)
        {
            return false;
        }

        bool isKnownSoulEngine = HasToken(
            card,
            "SOUL",
            "BORROWED",
            "RIGHTHAND",
            "RIGHTHANDHAND",
            "HAUNT",
            "CAPTURE",
            "SPIRIT",
            "DIRGE",
            "INVOKE");
        bool hasUsefulTempo = card.GetCardsDrawn() > 0 || card.GetEnergyGain() > 0 || card.Exhaust;
        bool activeNecroBuild = context.ActiveBuild?.Profile.BuildId is "soul" or "osty" or "doom" or "reaper";
        return isKnownSoulEngine || (activeNecroBuild && hasUsefulTempo);
    }

    public static bool IsPriorityDrawBlockCard(ResolvedCardView? card)
    {
        return card != null &&
               card.GetCardsDrawn() > 0 &&
               (card.GetEstimatedBlock() > 0 || card.GetEnergyGain() > 0 || card.EffectiveCost <= 0);
    }

    public static bool IsWeakStarterStrike(ResolvedCardView? card)
    {
        if (card == null)
        {
            return false;
        }

        return card.Rarity == "Basic" &&
               card.GetEstimatedDamage() > 0 &&
               HasToken(card, "STRIKE") &&
               !HasToken(card, "POMMEL", "TWIN", "PERFECTED", "FOCUSED");
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
            "poison" => IsSilentPoisonSetupCard(card),
            "sly" or "grand_finale" => IsSilentSlyEngineCardForBuild(card),
            "shiv" or "envenom" => IsSilentShivSetupCard(card),
            "claw" => IsCycleCard(card),
            "frost" or "lightning" or "dark_orb" or "creative_ai" => HasToken(card, "DEFRA", "CAPACITOR", "ECHO", "STORM", "MACHINE", "LOOP", "HEATSINK") || IsOrbSetupCard(card),
            "osty" or "soul" or "doom" or "reaper" => IsOstyGuardCard(card) || HasToken(card, "INVOKE", "BORROWED", "DIRGE", "REANIMATE", "HAUNT", "CAPTURE", "COUNTDOWN", "REAPERFORM", "SOUL"),
            "forge" or "star_burst" or "void_form" or "bombardment" => HasToken(card, "CONQUEROR", "SEEKING", "VOID") || IsStarSetupCard(card),
            "strength" => HasToken(card, "LIMITBREAK", "SPOTWEAKNESS"),
            "strike" => HasToken(card, "PERFECTEDSTRIKE"),
            "self_wound" => HasToken(card, "RUPTURE", "BLOODLET"),
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
            "exhaust" => HasToken(card, "FIENDFIRE", "SECONDWIND", "BURNING"),
            "bloodletting" or "self_wound" => HasToken(card, "REAPER", "HEMOKINESIS", "COMBUST", "RUPTURE"),
            "strike" => HasToken(card, "STRIKE", "PERFECTED"),
            "sly" or "grand_finale" => IsSilentSlyPayoffCard(card),
            "shiv" or "envenom" => IsSilentShivPayoffCard(card),
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

    private static bool IsSilentEngineBuild(string buildId)
    {
        return buildId is "sly" or "grand_finale";
    }

    private static bool IsSilentContext(DeterministicCombatContext context)
    {
        return string.Equals(context.CombatConfig.CharacterId, "silent", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(context.ActiveBuild?.Profile.CharacterId, "silent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSilentSlyEngineCardForBuild(ResolvedCardView card)
    {
        return HasToken(
                   card,
                   "SLY",
                   "MASTERPLANNER",
                   "ACROBATICS",
                   "PREPARED",
                   "REFLEX",
                   "TACTICIAN",
                   "CALCULATEDGAMBLE",
                   "CONCENTRATE",
                   "TOOLSOFTHETRADE",
                   "TOOLS",
                   "DISCARD") ||
               IsCycleCard(card);
    }

    public static bool IsOrbSetupCard(ResolvedCardView card)
    {
        return HasToken(
            card,
            "ZAP",
            "BALLLIGHTNING",
            "BALL",
            "LIGHTNING",
            "COLDSNAP",
            "COLD",
            "COOLHEADED",
            "GLACIER",
            "DARKNESS",
            "DARK",
            "CHILL",
            "DOOMANDGLOOM",
            "ELECTRODYNAMICS",
            "STATICDISCHARGE",
            "TEMPEST",
            "STORM",
            "CAPACITOR",
            "CHAOS",
            "RECURSION");
    }

    private static bool IsOrbPayoffCard(ResolvedCardView card)
    {
        return HasToken(card, "DUALCAST", "MULTICAST", "RECURSION", "CONSUME", "CONSUMING");
    }

    public static bool IsStarSetupCard(ResolvedCardView card)
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

    public static bool IsStarPayoffCard(ResolvedCardView card)
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
