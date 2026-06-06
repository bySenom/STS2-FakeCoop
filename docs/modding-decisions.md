# Modding Decisions

## Combat Target Enumeration

- Patch point: `AiTeammateDummyController.DiscoverCombatActions`.
- Decision: expose every valid card target as a separate legal action instead of stopping after the first playable target.
- Reasoning: the existing combat scorer already evaluates target details such as low HP, incoming damage, and lethal potential, but it needs separate legal actions to compare targets.
- Rejected approach: changing the scorer first. The smaller, safer improvement is to feed it better action candidates without changing scoring weights.
- Verification: test a multi-enemy combat and check that the AI can choose non-first targets when scoring favors them.

## Combat Pile Selection Support

- Patch point: `CardSelectCmd.FromCombatPile`.
- Decision: route AI combat-pile prompts through the existing deterministic card selector.
- Reasoning: cards such as `NeowsFury` play normally, then ask the player to choose cards from a combat pile. Without this patch, AI players can stall on that follow-up selection.
- Verification: play `NeowsFury` as an AI teammate while discard pile cards are available and confirm the selection resolves automatically.
