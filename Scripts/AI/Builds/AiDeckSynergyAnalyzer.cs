using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace AITeammate.Scripts;

internal sealed class AiDeckSynergyAnalyzer
{
    private const double MaxPositiveScore = 34d;
    private const double MaxNegativeScore = -18d;

    public AiDeckSynergyResult Evaluate(ResolvedCardView candidate, CardEvaluationContext context)
    {
        SynergySnapshot deck = SynergySnapshot.From(context.DeckCards);
        SynergyTags candidateTags = SynergyTags.From(candidate);
        double score = 0d;
        List<string> reasons = [];

        AddGenericDensityScore(candidateTags, deck, reasons, ref score);
        AddPoisonScore(candidate, candidateTags, deck, reasons, ref score);
        AddShivScore(candidateTags, deck, reasons, ref score);
        AddDiscardScore(candidateTags, deck, reasons, ref score);
        AddOrbScore(candidateTags, deck, reasons, ref score);
        AddStrengthScore(candidateTags, deck, reasons, ref score);
        AddBlockScore(candidateTags, deck, reasons, ref score);
        AddExhaustScore(candidateTags, deck, reasons, ref score);
        AddNecrobinderScore(candidateTags, deck, reasons, ref score);
        AddRegentScore(candidateTags, deck, reasons, ref score);
        AddResourceScore(candidateTags, deck, reasons, ref score);

        score = Math.Clamp(score, MaxNegativeScore, MaxPositiveScore);
        return new AiDeckSynergyResult
        {
            Score = score,
            Reasons = reasons
        };
    }

    private static void AddGenericDensityScore(SynergyTags candidate, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        foreach (string tag in candidate.Tags)
        {
            int count = deck.Count(tag);
            if (count <= 0 || !SynergyTags.IsEngineTag(tag))
            {
                continue;
            }

            double bonus = Math.Min(4d, count * 0.9d);
            score += bonus;
            reasons.Add($"{tag} density +{bonus:F1}");
        }
    }

    private static void AddPoisonScore(
        ResolvedCardView candidate,
        SynergyTags tags,
        SynergySnapshot deck,
        List<string> reasons,
        ref double score)
    {
        if (tags.Has("poison_setup") && deck.Count("poison_payoff") > 0)
        {
            double bonus = 7d + Math.Min(5d, deck.Count("poison_payoff") * 2d);
            score += bonus;
            reasons.Add($"enables poison payoff +{bonus:F1}");
        }

        if (tags.Has("poison_payoff"))
        {
            int poisonSetup = deck.Count("poison_setup");
            if (poisonSetup >= 2)
            {
                double bonus = 10d + Math.Min(8d, poisonSetup * 2d);
                score += bonus;
                reasons.Add($"poison payoff online +{bonus:F1}");
            }
            else if (poisonSetup == 0 && IsHardPoisonPayoff(candidate))
            {
                score -= 8d;
                reasons.Add("poison payoff without poison -8.0");
            }
        }
    }

    private static void AddShivScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("shiv_generator") && deck.Count("shiv_payoff") > 0)
        {
            double bonus = 9d + Math.Min(6d, deck.Count("shiv_payoff") * 2d);
            score += bonus;
            reasons.Add($"feeds shiv payoff +{bonus:F1}");
        }

        if (tags.Has("shiv_payoff"))
        {
            int generators = deck.Count("shiv_generator");
            double bonus = generators > 0 ? 8d + Math.Min(8d, generators * 2d) : 4d;
            score += bonus;
            reasons.Add($"shiv scaling +{bonus:F1}");
        }
    }

    private static void AddDiscardScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("discard") && (deck.Count("sly") > 0 || deck.Count("discard_payoff") > 0))
        {
            double bonus = 8d + Math.Min(8d, deck.Count("sly") * 1.5d + deck.Count("discard_payoff") * 2d);
            score += bonus;
            reasons.Add($"discard engine +{bonus:F1}");
        }

        if ((tags.Has("sly") || tags.Has("discard_payoff")) && deck.Count("discard") > 0)
        {
            double bonus = 7d + Math.Min(8d, deck.Count("discard") * 1.6d);
            score += bonus;
            reasons.Add($"discard payoff support +{bonus:F1}");
        }
    }

    private static void AddOrbScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("orb_setup") && (deck.Count("focus") > 0 || deck.Count("orb_slot") > 0 || deck.Count("orb_payoff") > 0))
        {
            double bonus = 8d + Math.Min(8d, deck.Count("focus") * 2d + deck.Count("orb_slot") * 2d + deck.Count("orb_payoff"));
            score += bonus;
            reasons.Add($"fills orb engine +{bonus:F1}");
        }

        if ((tags.Has("focus") || tags.Has("orb_slot")) && deck.Count("orb_setup") > 0)
        {
            double bonus = 9d + Math.Min(8d, deck.Count("orb_setup") * 1.5d);
            score += bonus;
            reasons.Add($"amplifies orbs +{bonus:F1}");
        }

        if (tags.Has("orb_payoff") && deck.Count("orb_setup") == 0)
        {
            score -= 5d;
            reasons.Add("orb payoff without orb setup -5.0");
        }
    }

    private static void AddStrengthScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("strength") && (deck.Count("multi_hit") > 0 || deck.Count("attack") >= 5))
        {
            double bonus = 8d + Math.Min(8d, deck.Count("multi_hit") * 2d + deck.Count("attack") * 0.6d);
            score += bonus;
            reasons.Add($"strength payoff deck +{bonus:F1}");
        }

        if (tags.Has("multi_hit") && deck.Count("strength") > 0)
        {
            double bonus = 8d + Math.Min(8d, deck.Count("strength") * 2.5d);
            score += bonus;
            reasons.Add($"multi-hit strength payoff +{bonus:F1}");
        }
    }

    private static void AddBlockScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("block_payoff") && deck.Count("block") >= 4)
        {
            double bonus = 9d + Math.Min(8d, deck.Count("block") * 1.2d);
            score += bonus;
            reasons.Add($"block payoff online +{bonus:F1}");
        }

        if (tags.Has("block") && deck.Count("block_payoff") > 0)
        {
            double bonus = 6d + Math.Min(7d, deck.Count("block_payoff") * 2d);
            score += bonus;
            reasons.Add($"feeds block payoff +{bonus:F1}");
        }
    }

    private static void AddExhaustScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("exhaust") && deck.Count("exhaust_payoff") > 0)
        {
            double bonus = 7d + Math.Min(8d, deck.Count("exhaust_payoff") * 2d);
            score += bonus;
            reasons.Add($"feeds exhaust payoff +{bonus:F1}");
        }

        if (tags.Has("exhaust_payoff") && deck.Count("exhaust") > 0)
        {
            double bonus = 8d + Math.Min(8d, deck.Count("exhaust") * 1.6d);
            score += bonus;
            reasons.Add($"exhaust payoff online +{bonus:F1}");
        }
    }

    private static void AddNecrobinderScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("osty_support") && deck.Count("osty") > 0)
        {
            double bonus = 8d + Math.Min(6d, deck.Count("osty") * 2d);
            score += bonus;
            reasons.Add($"supports Osty engine +{bonus:F1}");
        }

        if (tags.Has("soul") && (deck.Count("soul_payoff") > 0 || deck.Count("draw") > 1))
        {
            double bonus = 8d + Math.Min(8d, deck.Count("soul_payoff") * 2d + deck.Count("draw"));
            score += bonus;
            reasons.Add($"soul engine +{bonus:F1}");
        }
    }

    private static void AddRegentScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("star_setup") && deck.Count("star_payoff") > 0)
        {
            double bonus = 8d + Math.Min(8d, deck.Count("star_payoff") * 2d);
            score += bonus;
            reasons.Add($"enables star payoff +{bonus:F1}");
        }

        if (tags.Has("star_payoff") && deck.Count("star_setup") > 0)
        {
            double bonus = 8d + Math.Min(8d, deck.Count("star_setup") * 2d);
            score += bonus;
            reasons.Add($"star payoff online +{bonus:F1}");
        }
    }

    private static void AddResourceScore(SynergyTags tags, SynergySnapshot deck, List<string> reasons, ref double score)
    {
        if (tags.Has("draw") && (deck.Count("energy") > 0 || deck.Count("zero_cost") >= 3))
        {
            double bonus = 5d + Math.Min(7d, deck.Count("energy") * 2d + deck.Count("zero_cost"));
            score += bonus;
            reasons.Add($"draw resource engine +{bonus:F1}");
        }

        if (tags.Has("energy") && deck.Count("draw") > 0)
        {
            double bonus = 5d + Math.Min(7d, deck.Count("draw") * 1.5d);
            score += bonus;
            reasons.Add($"energy with draw +{bonus:F1}");
        }
    }

    private static bool IsHardPoisonPayoff(ResolvedCardView card)
    {
        return SynergyTags.HasToken(card, "CATALYST", "ACCELERANT");
    }
}

internal sealed class AiDeckSynergyResult
{
    public required double Score { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];
}

internal sealed class SynergySnapshot
{
    private readonly IReadOnlyDictionary<string, int> _counts;

    private SynergySnapshot(IReadOnlyDictionary<string, int> counts)
    {
        _counts = counts;
    }

    public int Count(string tag)
    {
        return _counts.TryGetValue(tag, out int count) ? count : 0;
    }

    public static SynergySnapshot From(IReadOnlyList<ResolvedCardView> cards)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (ResolvedCardView card in cards)
        {
            foreach (string tag in SynergyTags.From(card).Tags)
            {
                counts[tag] = counts.TryGetValue(tag, out int count) ? count + 1 : 1;
            }
        }

        return new SynergySnapshot(counts);
    }
}

internal sealed class SynergyTags
{
    private static readonly IReadOnlySet<string> EngineTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "poison_setup",
        "poison_payoff",
        "shiv_generator",
        "shiv_payoff",
        "discard",
        "sly",
        "discard_payoff",
        "orb_setup",
        "orb_payoff",
        "focus",
        "orb_slot",
        "strength",
        "multi_hit",
        "block_payoff",
        "exhaust",
        "exhaust_payoff",
        "osty",
        "osty_support",
        "soul",
        "soul_payoff",
        "star_setup",
        "star_payoff"
    };

    private SynergyTags(IReadOnlySet<string> tags)
    {
        Tags = tags;
    }

    public IReadOnlySet<string> Tags { get; }

    public bool Has(string tag)
    {
        return Tags.Contains(tag);
    }

    public static bool IsEngineTag(string tag)
    {
        return EngineTags.Contains(tag);
    }

    public static SynergyTags From(ResolvedCardView card)
    {
        HashSet<string> tags = new(StringComparer.Ordinal);

        if (card.Type == CardType.Attack)
        {
            tags.Add("attack");
        }

        if (card.EffectiveCost == 0)
        {
            tags.Add("zero_cost");
        }

        if (card.GetEstimatedBlock() > 0)
        {
            tags.Add("block");
        }

        if (card.GetCardsDrawn() > 0 || HasToken(card, "DRAW", "ACROBATICS", "BACKFLIP", "PREPARED", "COOLHEADED", "SHRUGITOFF", "SOUL"))
        {
            tags.Add("draw");
        }

        if (card.GetEnergyGain() > 0 || HasToken(card, "ENERGY", "TACTICIAN", "CONCENTRATE", "TURBO", "DOUBLEENERGY"))
        {
            tags.Add("energy");
        }

        if (card.Exhaust || HasToken(card, "EXHAUST", "CORRUPTION", "TRUEGRIT", "FIEND"))
        {
            tags.Add("exhaust");
        }

        if (card.ReplayCount > 1 || HasToken(card, "TWINSTRIKE", "POMMELSTRIKE", "SWORD BOOMERANG", "RIDDLEWITHHOLES", "GUNKUP"))
        {
            tags.Add("multi_hit");
        }

        if (card.GetSelfStrengthAmount() > 0 || HasToken(card, "STRENGTH", "INFLAME", "DEMONFORM", "SPOTWEAKNESS", "FLEX"))
        {
            tags.Add("strength");
        }

        if (HasToken(card, "POISON", "NOXIOUS", "DEADLYPOISON", "BOUNCINGFLASK", "SNAKEBITE"))
        {
            tags.Add("poison_setup");
        }

        if (HasToken(card, "CATALYST", "ACCELERANT", "BURST"))
        {
            tags.Add("poison_payoff");
        }

        if (HasToken(card, "SHIV", "BLADEDANCE", "CLOAKANDDAGGER", "FANOFKNIVES"))
        {
            tags.Add("shiv_generator");
        }

        if (HasToken(card, "ACCURACY", "ENVENOM"))
        {
            tags.Add("shiv_payoff");
        }

        if (HasToken(card, "DISCARD", "ACROBATICS", "PREPARED", "CALCULATEDGAMBLE", "CONCENTRATE", "MASTERPLANNER"))
        {
            tags.Add("discard");
        }

        if (HasToken(card, "SLY", "REFLEX", "TACTICIAN"))
        {
            tags.Add("sly");
            tags.Add("discard_payoff");
        }

        if (HasToken(card, "ZAP", "BALLLIGHTNING", "LIGHTNING", "FROST", "GLACIER", "COOLHEADED", "DARKNESS", "ORB"))
        {
            tags.Add("orb_setup");
        }

        if (HasToken(card, "DUALCAST", "MULTICAST", "RECURSION"))
        {
            tags.Add("orb_payoff");
        }

        if (HasToken(card, "FOCUS", "DEFRAGMENT", "BIASEDCOGNITION", "DATA"))
        {
            tags.Add("focus");
        }

        if (HasToken(card, "CAPACITOR", "INSERTER"))
        {
            tags.Add("orb_slot");
        }

        if (HasToken(card, "BODYSLAM", "BARRICADE", "ENTRENCH"))
        {
            tags.Add("block_payoff");
        }

        if (HasToken(card, "FEELNOPAIN", "DARKEMBRACE", "DEADBRANCH", "CHARON"))
        {
            tags.Add("exhaust_payoff");
        }

        if (HasToken(card, "OSTY", "UNLEASH", "REAPERFORM"))
        {
            tags.Add("osty");
        }

        if (HasToken(card, "BODYGUARD", "RIGHTHANDHAND", "GRAVEWARDEN"))
        {
            tags.Add("osty_support");
        }

        if (HasToken(card, "SOUL", "BORROWEDTIME", "COUNTDOWN", "INVOKE"))
        {
            tags.Add("soul");
        }

        if (HasToken(card, "DRAINPOWER", "REAPER", "DEATHMARCH"))
        {
            tags.Add("soul_payoff");
        }

        if (HasToken(card, "GUIDINGSTAR", "FALLINGSTAR", "STARDUST", "VENERATE", "GLOW", "CONVERGENCE"))
        {
            tags.Add("star_setup");
        }

        if (HasToken(card, "GAMMABLAST", "PHOTONCUT", "METEORSHOWER", "BIGBANG", "BOMBARDMENT", "SEVENSTARS"))
        {
            tags.Add("star_payoff");
        }

        return new SynergyTags(tags);
    }

    public static bool HasToken(ResolvedCardView card, params string[] tokens)
    {
        string haystack = Normalize(card.CardId) + "|" +
                          Normalize(card.Name) + "|" +
                          Normalize(card.Description) + "|" +
                          string.Join("|", card.Keywords.Select(Normalize)) + "|" +
                          string.Join("|", card.Tags.Select(Normalize));
        return tokens.Any(token => haystack.Contains(Normalize(token), StringComparison.Ordinal));
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}
