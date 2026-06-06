using System.Collections.Generic;

namespace AITeammate.Scripts;

internal enum AiBuildTier
{
    S,
    A,
    B
}

internal sealed class AiBuildArchetype
{
    public required string CharacterId { get; init; }

    public required string BuildId { get; init; }

    public required string DisplayName { get; init; }

    public required AiBuildTier Tier { get; init; }

    public required IReadOnlyList<string> CoreCards { get; init; }

    public IReadOnlyList<string> SupportCards { get; init; } = [];

    public IReadOnlyList<string> AvoidCards { get; init; } = [];

    public IReadOnlyList<string> KeyRelics { get; init; } = [];

    public IReadOnlyList<string> GoodRelics { get; init; } = [];

    public IReadOnlyList<string> AvoidRelics { get; init; } = [];

    public int DesiredMinDeckSize { get; init; } = 20;

    public int DesiredMaxDeckSize { get; init; } = 25;

    public int LockCoreCardCount { get; init; } = 2;

    public int LockEvidenceScore { get; init; } = 28;
}
