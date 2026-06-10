using System.Collections.Generic;

namespace AITeammate.Scripts;

internal sealed class CardChoiceDecision
{
    public required IReadOnlyList<CardEvaluationResult> RankedResults { get; init; }

    public required double SkipThreshold { get; init; }

    public required bool ShouldTakeCard { get; init; }

    public string ActiveBuildId { get; init; } = "none";

    public bool ActiveBuildLocked { get; init; }

    public string? SkipReason { get; init; }

    public CardEvaluationResult? BestEvaluation => RankedResults.Count > 0 ? RankedResults[0] : null;

    public string Describe()
    {
        string best = BestEvaluation?.Describe() ?? "none";
        string skipReason = string.IsNullOrWhiteSpace(SkipReason) ? string.Empty : $" skipReason={SkipReason}";
        return $"shouldTake={ShouldTakeCard} build={ActiveBuildId}{(ActiveBuildLocked ? ":locked" : string.Empty)} threshold={SkipThreshold:F1}{skipReason} best={best}";
    }
}
