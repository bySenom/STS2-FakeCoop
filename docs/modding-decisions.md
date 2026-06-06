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

## Build-Aware Card Rewards

- Patch point: `CardChoiceEvaluator.EvaluateCard`.
- Decision: add an `AiBuildPreferenceEvaluator` layer on top of the existing intrinsic/deck-needs card scoring, backed by richer `AiBuildArchetype` profiles.
- Reasoning: the bot should farm toward a coherent archetype instead of only taking generally strong cards. The profiles use the current build list from `https://slaythespire-2.com/builds` and choose the best-fitting build from current deck evidence.
- S-tier policy: S-tier profiles get stronger opener/tiebreaker bonuses, but the bot only treats a build as locked once the deck has enough core/evidence. Before a lock, A/B-tier cards can still be selected if no S-tier direction is coming together.
- Need policy: missing core cards get a stronger bonus, while non-core cards are penalized once the active profile deck is already above its desired size.
- Rejected approach: hard-committing each character to a fixed build at run start. The build page itself recommends adapting to offered cards, so this version gives early core cards a signal bonus and allows pivots when a competing build becomes better supported.
- Skip policy: once the deck has build evidence, optional reward/shop/choose-screen cards marked off-build must clear a higher effective threshold before the bot will take them.
- Verification: start an AI teammate run, check card reward or shop-card logs, and confirm top-three card evaluation reasons can include `build +...` or skip reasons like `off_build`.

## Relic Choice Heuristics

- Patch point: `RelicSelectCmd.FromChooseARelicScreen`.
- Decision: replace first-relic selection with `AiRelicChoiceEvaluator`.
- Reasoning: relic choice should account for active build profiles first, then broad deck synergies such as Exhaust plus Dead Branch, attack-spam plus Shuriken/Kunai, orb evidence plus Inserter/Data Disk, high-cost decks plus Snecko, and starter Strike density plus Strike Dummy.
- Verification: trigger a relic choice screen for an AI teammate and confirm logs show `[AITeammate] Relic evaluation rank`.

## Rest Site Upgrade Support

- Patch points: `AiTeammateDummyController.DiscoverRestSiteActions`, `CardSelectCmd.FromDeckForUpgrade`, and `CardSelectCmd.FromHandForUpgrade`.
- Decision: prefer rest-site upgrades when the AI is healthy enough, and select upgrade targets with `CardUpgradeEvaluator`.
- Reasoning: always healing wastes campfires, and first-card upgrade selection can upgrade bad or off-build cards. The upgrade scorer now combines upgrade deltas, build relevance, scaling bias, and off-build penalties.
- Heal policy: missing a small amount of HP is not enough to heal. Healing is preferred only at meaningful low HP or when at least 24 HP is missing; otherwise the bot prefers upgrade or another non-heal option.
- Verification: reach a rest site with an AI teammate above low-health thresholds and confirm logs show `[AITeammate][RestSite] Selected option ... upgrade` followed by `[AITeammate] Upgrade evaluation rank`.

## Build-Aware Removal and Combat

- Patch points: `ShopPlanner.SelectBestRemovalCandidate` and `CombatActionScorer.Score`.
- Decision: protect active-profile core/support cards from removal, prioritize removing active-profile avoid/off-build cards, and add small in-combat bonuses for playing active-profile core/support cards.
- Reasoning: strong decks are not only about picking good cards. They must also remove dilution and pilot their engine cards when drawn. Combat bonuses are intentionally smaller than survival/damage scores so the bot still blocks lethal damage.
- Verification: visit a shop with removal available and check removal reasons for `core keep bias`, `support keep bias`, or `off-build removal bias`; in combat, debug semantic scores include `build=...`.

## Host Auto-Mode

- Patch point: `NRun._Process` via `AiTeammatePeerInputPatches`.
- Decision: `F4` toggles a host auto-mode controller that ticks the host player through the same `AiTeammateDummyController` decision path.
- Reasoning: the user can hand control to the bot temporarily without changing party setup or adding a fake player. Host auto-mode is intended for active automation such as combat, not for hiding human reward UI.
- Reward UI policy: while host auto-mode is enabled, the host also resolves room-end rewards deterministically. Gold/relic/potion rewards are collected automatically, and card rewards use the same build-aware evaluator as AI teammates, including skip decisions for weak or off-build offers.
- Modded reward policy: reward screens with larger card pools, such as five-card choices, should route through the same build-aware choose-screen/simple-grid handling instead of requiring manual host clicks.
- Verification: press `F4` during a run and confirm logs show host auto-mode enabled/disabled; then finish combat and confirm host rewards auto-resolve with card evaluation logs, while AI teammates still resolve their own rewards deterministically.

## Build-Aware Combat Rotation

- Patch point: `CombatTurnLinePlanner`.
- Decision: classify active-build cards into setup, payoff, cycle, defense, finisher, or avoid roles, then score lines for setup-before-payoff sequencing.
- Reasoning: strong decks need to pilot their engine. Strength should set up before multi-hit payoff, cycle decks should draw/energy first, and locked builds should avoid wasting turns on off-build cards unless survival/lethal requires it.
- Verification: inspect combat line logs and semantic scores; build payoff cards should be delayed behind playable setup/cycle cards when the line planner can afford it.

## Zero-Energy X-Cost Guard

- Patch points: `AiTeammateDummyController.DiscoverCombatActions`, `CombatActionScorer.Score`, and `CombatTurnLinePlanner`.
- Decision: do not expose X-cost cards as playable actions when the actor has 0 energy, and treat X-cost cards as spending all remaining energy in combat line planning.
- Reasoning: X-cost cards can appear legal at 0 energy, but playing them then wastes the card. The scorer and planner also need to avoid counting them as free follow-up actions.
- Verification: enter combat with an X-cost card, spend all energy first, and confirm the bot ends/chooses another action instead of playing the X-cost card at 0.

## Regent and Defect Engine Rotation

- Patch points: `CombatBuildRoleEvaluator` and `CombatTurnLinePlanner`.
- Decision: classify Defect orb-fill cards as engine setup only for orb builds (`lightning`, `frost`, `dark_orb`, `creative_ai`), and classify Regent star-building cards as engine setup for Regent builds.
- Reasoning: orb and star decks need to build their resource engine before spending payoff cards. Claw should not be forced into orb setup just because it is Defect.
- Verification: in combat line logs, orb/star setup cards should appear before engine payoff cards when they are affordable and survival/lethal does not override it.

## Block Discipline

- Patch points: `CombatActionScorer.Score` and `CombatTurnLinePlanner`.
- Decision: pure block cards are treated as redundant once incoming damage is already covered, unless the actor can retain block or uses block as an offensive resource.
- Reasoning: playing extra block after full coverage wastes energy and actions that could be used for damage, scaling, or build setup. Body Slam/Barricade-style decks are an exception because extra block can become damage.
- Status-card policy: hand cards with `take X damage` at end of turn add blockable threat; cards with `lose X HP` add unavoidable life loss and should not cause overblocking.
- End-turn policy: when the combat backend chooses end turn despite remaining non-end actions, the delayed end-turn commit is no longer canceled just because those actions still exist.
- Verification: with 5 incoming damage and enough current/planned block already available, the bot should stop playing additional pure block cards and prefer useful damage/setup or end turn. In Barricade/Body Slam decks, extra block may still be played before payoff.

## Core Build Power Priority

- Patch points: `CombatBuildRoleEvaluator`, `CombatActionScorer.Score`, and `CombatTurnLinePlanner`.
- Decision: active-profile core powers get an additional combat-investment bonus and line-planning priority, especially as the first played card in a turn.
- Reasoning: cards such as `Demon Form`, `Echo Form`, `Barricade`, `Creative AI`, and `Void Form` cost a full turn of energy but stay active for the rest of combat. The AI should not treat them like inefficient expensive cards when they are core to the selected build.
- Safety policy: this is still score-based. Lethal damage, urgent survival, or immediate lethal opportunities can override the power priority.
- Verification: with 3 energy and a locked/evidenced build, a playable core power should be chosen before ordinary attacks, cycle, or payoff cards unless an emergency overrides it.

## Combat Potion Discipline

- Patch points: `CombatActionScorer.Score` and `CombatTurnLinePlanner`.
- Decision: combat potions require tactical need before they can compete with cards/end turn, and useful potions receive line-planning priority early in the turn.
- Reasoning: potions are limited resources. The bot should not spend them in normal fights or at the end of a turn just because they are legal actions.
- Need policy: grave danger, meaningful incoming damage, elite/boss pressure, or an offensive potion with useful follow-up can justify potion use. Low-value potions are excluded from combat line planning so end turn can win.
- Verification: in a safe normal fight, held potions should remain unused. In an elite/boss or dangerous turn, a chosen potion should appear early in the planned line before payoff cards.
