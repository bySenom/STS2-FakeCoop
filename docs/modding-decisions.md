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

- Patch points: `RelicSelectCmd.FromChooseARelicScreen` and `TreasureRoomRelicSynchronizer.BeginRelicPicking`.
- Decision: replace first-relic selection with `AiRelicChoiceEvaluator`; for shared treasure rooms, evaluate every available relic for every auto-controlled player and assign unique picks that maximize total team score when enough relics are available.
- Reasoning: relic choice should account for active build profiles first, then broad deck synergies such as Exhaust plus Dead Branch, attack-spam plus Shuriken/Kunai, orb evidence plus Inserter/Data Disk, high-cost decks plus Snecko, and starter Strike density plus Strike Dummy.
- Host policy: normal host play remains manual. When host auto-mode is enabled, the host joins the coordinated treasure assignment and uses `PickRelicLocally` so the local relic UI can close/proceed correctly.
- Verification: trigger a relic choice screen for an AI teammate and confirm logs show `[AITeammate] Relic evaluation rank`; in treasure rooms confirm logs show `Treasure relic coordinated assignment`.

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
- Act transition policy: after auto-mode boss rewards finish, the host is auto-readied for the next-act transition so the existing AI teammate ready votes can complete the transition.
- Verification: press `F4` during a run and confirm logs show host auto-mode enabled/disabled; then finish combat and confirm host rewards auto-resolve with card evaluation logs, while AI teammates still resolve their own rewards deterministically.

## Build-Aware Combat Rotation

- Patch point: `CombatTurnLinePlanner`.
- Decision: classify active-build cards into setup, payoff, cycle, defense, finisher, or avoid roles, then score lines for setup-before-payoff sequencing.
- Reasoning: strong decks need to pilot their engine. Strength should set up before multi-hit payoff, cycle decks should draw/energy first, and locked builds should avoid wasting turns on off-build cards unless survival/lethal requires it.
- Verification: inspect combat line logs and semantic scores; build payoff cards should be delayed behind playable setup/cycle cards when the line planner can afford it.

## Future-Turn Combat Value

- Patch points: `CardResolver`, `CardCatalogBuilder`, `DeterministicCombatContextBuilder`, and `CombatActionScorer.Score`.
- Decision: add a separate future-turn score component to semantic combat scoring and log it as `future=...`.
- Reasoning: some winning plays are not current-turn max damage. Poison stacks, persistent forms, orb/star setup, and Osty engine cards can win later turns even if a starter attack looks better immediately.
- Poison policy: visible enemy poison stacks are read into combat context. Poison cards estimate useful poison damage over a 3-5 turn horizon, with longer horizons for elite/boss/high-HP fights.
- Engine policy: long-term powers and active-build setup receive future value on top of immediate/build fit value.
- Osty policy: card text fallback detects patterns like `deals X damage`, so ally/Osty damage cards are less likely to be undervalued when the game does not expose a normal damage dynamic variable.
- Starter-strike policy: if an affordable higher-damage build card exists, basic starter `Strike` receives an additional penalty.
- Verification: in debug logs, future-oriented cards should show nonzero `future=...` and should beat starter attacks unless survival, lethal, or target constraints override them.

## Zero-Energy X-Cost Guard

- Patch points: `AiTeammateDummyController.DiscoverCombatActions`, `CombatActionScorer.Score`, and `CombatTurnLinePlanner`.
- Decision: do not expose X-cost cards as playable actions when the actor has 0 energy, and treat X-cost cards as spending all remaining energy in combat line planning.
- Reasoning: X-cost cards can appear legal at 0 energy, but playing them then wastes the card. The scorer and planner also need to avoid counting them as free follow-up actions.
- Verification: enter combat with an X-cost card, spend all energy first, and confirm the bot ends/chooses another action instead of playing the X-cost card at 0.

## Regent and Defect Engine Rotation

- Patch points: `CombatBuildRoleEvaluator` and `CombatTurnLinePlanner`.
- Decision: classify Defect orb-fill/cardslot cards as engine setup only for orb builds (`lightning`, `frost`, `dark_orb`, `creative_ai`), and classify Regent star-building cards as engine setup for Regent builds.
- Reasoning: orb and star decks need to build their resource engine before spending payoff cards. Claw should not be forced into orb setup just because it is Defect.
- Starter-strike policy: when an affordable orb setup card is in hand, basic `Strike` receives an extra off-engine penalty so it becomes a last resort instead of competing with `Zap`, `Ball Lightning`, `Capacitor`, or similar setup.
- Verification: in combat line logs, orb/star setup cards should appear before engine payoff cards when they are affordable and survival/lethal does not override it.

## Necrobinder Early Engine Priority

- Patch points: `AiBuildArchetypeCatalog`, `AiTeammateDummyController.DiscoverCombatActions`, `CombatBuildRoleEvaluator`, `CombatActionScorer.Score`, and `CombatTurnLinePlanner`.
- Decision: treat `Bodyguard` as Osty support/setup, and give known zero-cost Necrobinder soul-engine cards first-action priority.
- Reasoning: Osty decks need protective setup online before spending turns on weak attacks. Soul/draw cards are tempo-negative if used after energy is already gone, so they should fire early while drawn cards can still matter.
- Target policy: `TargetType.Osty` cards enumerate living allied creatures first instead of falling through to a null target only.
- Build-lock policy: early soul priority is based on Necrobinder character identity and known soul tokens, not only on the currently locked active build.
- Verification: with a Necrobinder hand containing zero-cost soul-engine cards, those cards should outrank starter attacks unless survival/lethal gives a better line.

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
- Need policy: grave danger, severe uncovered damage, elite/boss pressure, or an offensive potion with useful follow-up can justify potion use. Low-value potions are excluded from combat line planning so end turn can win.
- Verification: in a safe normal fight, held potions should remain unused. In an elite/boss or dangerous turn, a chosen potion should appear early in the planned line before payoff cards.

## Coop Target Overkill Discipline

- Patch points: `AiTeammateDummyController`, `DeterministicCombatContextBuilder`, `CombatActionScorer`, and `CombatTurnLinePlanner`.
- Decision: when an AI/auto-mode player queues targeted damage, temporarily reserve that damage by enemy target until the queued action settles.
- Reasoning: teammates act close together and can otherwise waste multiple attacks on an enemy that is already about to die. Reserving pending damage lets the next bot see the target as effectively lower HP or already covered.
- Scoring policy: targets already covered by pending teammate damage receive a strong penalty, and targeted overkill is penalized. Combat line damage is capped to remaining useful HP after pending teammate damage.
- Verification: in a multi-enemy fight, if one bot queues lethal damage on a low-HP enemy, later bots should prefer another enemy or non-attack action instead of piling more attacks into the same target.

## Enemy-Aware Targeting and AoE

- Patch points: `DeterministicCombatContextBuilder`, `CombatActionScorer`, and `CombatTurnLinePlanner`.
- Decision: enemy states now include a lightweight threat score from incoming damage and recognizable intent names, and all-enemy damage is scored/simulated across every living enemy.
- Reasoning: strong combat play needs to know which enemy matters, not only which enemy has low HP. AoE cards were also undervalued when their damage was counted as only one target.
- Targeting policy: single-target attacks get extra value against high-threat enemies, while AoE gets useful-damage, likely-kill, attacking-target, and multi-target bonuses.
- Verification: in a multi-enemy fight, AoE cards should score higher when multiple targets are alive, and single-target attacks should prefer high incoming/scaling/status enemies over harmless low-priority targets when lethal is not decisive.

## Combat Learning Experience Layer

- Patch points: `DeterministicCombatDecisionBackend`, `CombatActionScorer`, `RewardsCmd.OfferForRoomEnd`, and `RunManager.CleanUp`.
- Decision: add `AiLearningService` as a small experience layer around the existing heuristic combat system instead of replacing any scorer or line planner logic.
- Recording policy: each chosen combat action stores character, active build, deck archetype, enemy archetype, action role, act bucket, HP bucket, incoming-damage bucket, heuristic score, rank, and line estimates.
- Update policy: combat completion converts the decision record into a conservative reward signal using survival, estimated damage, estimated damage taken, HP lost after the decision, heuristic margin, and rank penalty.
- Learning policy: experience updates slowly with a low learning rate and decay. Learned score influence requires multiple matching samples, uses confidence from sample count and variance, and is capped to a small combat-score adjustment.
- Debug policy: every record/update/flush is logged, and semantic combat logs include `learned=...` so bad learning can be inspected without guessing.
- Storage policy: aggregate experience is stored as JSON in `config/ai-learning/experience.json`; per-run decision journals are stored in `config/ai-learning/runs/`.
- Rejected approach: training a separate model or rewriting rotations. The current mod remains heuristic-first, with learned experience only acting as a careful tiebreaker.
- Verification: after several combats, check that experience files exist, samples increase, and learned adjustments stay at `0` until enough matching context has been observed.

## Run Outcome Telemetry

- Patch points: `DeterministicCombatDecisionBackend`, deterministic reward/card/relic/upgrade helpers, rest-site execution, shop-step execution, `RewardsCmd.OfferForRoomEnd`, `RunManager.Abandon`, and `RunManager.CleanUp`.
- Decision: add `AiRunTelemetryService` as a debug/diagnosis layer that records what the bots did during a run without changing their choices.
- Reasoning: pushing toward high win rates needs evidence about where runs fail. The telemetry records combat decisions, card picks/skips, relics, upgrades, rest choices, shop removals/purchases, potion reward choices, final deck snapshots, HP, relics, potions, and likely issue flags.
- Attribution policy: current death/loss attribution is conservative and metric-based. It flags probable causes such as block shortage, scaling shortage, unused potions at low/dead HP, too many starter Strike plays, frequent end turns with energy, overblocking, large decks, and missing build core cards.
- Storage policy: per-run telemetry is stored as JSON in `config/ai-telemetry/runs/`; a compact `config/ai-telemetry/latest-summary.json` is overwritten for fast inspection after the last test run.
- Rejected approach: immediately tuning all weights toward a target win rate. The smaller step is to make failures visible first, then tune the highest-impact issue with in-game evidence.
- Verification: after a test run cleanup, inspect `latest-summary.json` and confirm each auto-controlled player has deck metrics and `probableIssues`.
