# Debugging Notes

## Build

- Local VS Code build task uses `-p:Sts2Dir=D:\SteamLibrary\steamapps\common\Slay the Spire 2`.
- A plain `dotnet build` uses the default `Sts2Dir` from `sts2AITeammate.csproj`; verify that path exists before relying on it.
- The post-build target copies the DLL, mod JSON, and `config/` files into `$(Sts2Dir)\mods\sts2AITeammate\`.

## Combat Targeting Check

- Start an AI teammate run with at least one AI teammate.
- Enter a combat with multiple hittable enemies.
- Confirm logs show multiple legal `play_card_*_target_*` actions for single-target attacks when more than one target is valid.
- Expected result: the combat scorer can choose between enemy targets instead of always using the first ordered target.

## Combat Pile Selection Check

- Test an AI teammate with `NeowsFury` and at least one card in the discard pile.
- Expected result: after the attack resolves, the AI automatically selects cards from the combat pile prompt instead of stalling on the selection screen.
- Relevant patch: `CardSelectCmd.FromCombatPile` in `AiTeammateCardSelectionPatches`.

## Build-Aware Card Reward Check

- Start an AI teammate run with any supported character.
- Enter a combat and finish it to trigger card rewards, or visit a shop with card offers.
- Check the log lines beginning with `[AITeammate] Card evaluation rank`.
- Expected result: S-tier core cards get strong early bonuses, but A/B-tier build cards can still be selected before any build is locked.
- Cards that are core/support to the active profile can show reasons like `build +...`, and weak off-build cards can be skipped with `skipReason=off_build`.
- Cards that connect existing deck mechanics should show `synergy=...` and reasons such as `poison payoff online`, `fills orb engine`, `discard engine`, `feeds block payoff`, or `enables star payoff`.
- Current card scope: card rewards and shop card offers.

## Relic Choice Check

- Trigger an AI relic choice screen.
- Check the log lines beginning with `[AITeammate] Relic evaluation rank`.
- Expected result: build-profile key relics get priority when the deck has profile evidence.
- Shop relic offers also receive build-profile key/good/avoid relic adjustments.
- Trigger a treasure room with shared relic choices.
- Expected result: logs show `Treasure relic team evaluation` and `Treasure relic coordinated assignment`; AI teammates should avoid taking the same relic index when enough relics are available.
- With host auto-mode enabled, the host is included in the coordinated treasure assignment.
- Expected result: after the host auto-picks a treasure relic, the relic selection UI should close and the run should continue; the host uses the local relic-pick path.

## Rest Site Upgrade Check

- Reach a rest site with an AI teammate.
- Expected result is an upgrade/smith option when available unless HP is truly low or at least 24 HP is missing.
- Small missing HP should not cause a heal.
- Check logs for `[AITeammate][RestSite] Options`, `[AITeammate][RestSite] Selected option`, and `[AITeammate] Upgrade evaluation rank`.
- Upgrade targets should favor build-relevant and high-synergy cards, and penalize off-build cards.

## Build-Aware Removal Check

- Visit a shop with card removal available.
- Check shop/removal logs for removal target reasons.
- Expected result: active build core/support cards should show keep bias, while off-build or avoid cards become stronger removal targets.

## Build-Aware Combat Check

- Enter combat with a deck that has active build evidence.
- Check debug semantic score logs for `build=...`.
- Expected result: active build core/support cards get small combat bonuses, but survival and lethal checks still dominate.

## Host Auto-Mode Check

- Start an AI teammate run.
- Press `F4` during the run.
- Expected result: logs show `[AITeammate][AutoMode] Host auto-mode enabled`.
- While enabled, the host player uses the same deterministic AI choices for combat.
- Room-end rewards should auto-resolve for the host while auto-mode is enabled.
- Host auto-mode rewards should open through the normal foreground reward UI first. Logs should show `[AITeammate][AutoMode] Letting foreground reward offer open`, then `Waiting for foreground reward UI`, then `Foreground reward selecting`.
- Gold should be collected automatically.
- Card rewards should log `[AITeammate] Card evaluation` and either pick the best build-fitting card or skip weak/off-build offers.
- Modded reward pools with more than three cards should be evaluated as one candidate list.
- AI teammates should still resolve their own rewards deterministically after the host reward flow completes.
- Press `F4` again to disable host auto-mode.
- A short `F4` press should log exactly one enabled/disabled transition. If logs show rapid repeated enable/disable lines, the hotkey debounce is not working.
- If the reward window does not open while auto-mode is enabled, confirm logs show `Deterministically resolving reward offer`, `GoldReward`, and either `Deterministic card reward picked` or `Deterministic card reward skipped`.
- The action queue wait should not spam `InvalidOperationException` when the front queued action is still `Executing`.
- After an Act 1/Act 2 boss is defeated in auto-mode, rewards should resolve and logs should show `[AITeammate][AutoMode] Auto-readying host for act transition`.
- Expected result: the run proceeds to the next act after AI teammates are auto-readied.

## Real Multiplayer Compatibility Check

- `sts2AITeammate.json` should keep `affects_gameplay` set to `false`.
- Expected result: a normal multiplayer lobby with another human player should not require the friend to install this mod just because the local player has host auto-mode available.
- Press `F4` during a normal multiplayer run with no AI teammate session.
- Expected result: logs show `[AITeammate][AutoMode] Host auto-mode enabled` with `mode=local`, and the local player starts using the bot controller.
- Expected result: after a local auto-mode combat action, logs can show `treating local multiplayer request as issued`; the controller should continue without needing repeated F4 toggles.
- Expected result: those request-only local actions include a short `graceMs=1000` synchronized-queue wait before replanning, so auto-mode should not burst several host requests before the host echo updates hand/energy.
- Expected result: the first auto-mode tick should not print a long stacktrace burst from card description localization; unsupported formatted strings should fall back to raw loc text.
- The AI teammate setup still creates local fake multiplayer runs and is not meant for a real human multiplayer lobby.
- Runtime learning and telemetry JSON files should be written under `%APPDATA%/SlayTheSpire2/sts2AITeammate/`, not under `mods/sts2AITeammate/config/`, because STS2 scans JSON files in `mods/` as possible mod manifests.
- If multiplayer still complains about missing mods, check the game's `mods/sts2AITeammate/sts2AITeammate.json` and remove stale `mods/sts2AITeammate/config/ai-learning` or `mods/sts2AITeammate/config/ai-telemetry` folders.

## Build-Aware Combat Rotation Check

- Enter combat with an active build profile and multiple playable cards.
- Expected result: setup/cycle cards are preferred before payoff cards when survival and lethal do not override the line.
- Good checks: Strength before multi-hit attacks, draw/energy before Claw/Sly/Grand Finale payoffs, Necrobinder engine cards before finishers.
- Check semantic score logs for `future=...`; nonzero values mean the future-turn layer is contributing to the action choice.
- Check logs beginning with `[AITeammate][Diagnosis] Combat turn`.
- Useful diagnosis notes include `damage_left`, `block_left`, `core_power_left`, `engine_setup_left`, `potion_left`, `draw_left_while_energy_available`, and `energy_left`.
- Expected result: ordinary strong turns should log `notes=[ok]` or only low-severity context; repeated diagnosis notes identify the next weight/rotation bug to fix.

## Future-Turn Value Check

- Silent Poison: stack poison on an enemy, then offer another poison card or a poison payoff such as `Catalyst`/`Burst`-style cards.
- Expected result: poison cards should gain `future=...` value from estimated damage over the next 3-5 turns, especially in elite/boss or high-HP fights.
- Defect/Regent/Forms: playable long-term powers and engine setup such as `Echo Form`, `Demon Form`, `Capacitor`, `Void Form`, or star/orb setup should gain future value beyond their immediate damage.
- Necrobinder/Osty: a card whose text says an ally/Osty deals damage should be recognized from text when normal damage variables are missing.
- Expected result: high-damage Osty/build cards should beat basic `Strike` unless Strike is lethal or survival/targeting makes it clearly better.

## X-Cost Zero-Energy Check

- Enter combat with a playable X-cost card.
- Spend all energy before the bot evaluates the card, or test a hand where only 0 energy remains.
- Expected result: the bot should not play the X-cost card at 0 energy.
- Check logs for `Skipped combat action for X-cost card` or `blocked zero-energy X-cost`.

## Regent and Defect Engine Rotation Check

- Defect: test `lightning`, `frost`, or `dark_orb` evidence with cards like `Zap`, `Ball Lightning`, `Glacier`, `Coolheaded`, `Darkness`, `Capacitor`, or `Storm`.
- Expected result: the line planner prefers filling/building orb setup before starter `Strike`, `Dualcast`, `Multi-Cast`, `Recursion`, or other orb payoff when no emergency overrides it.
- Regent: test star builds with `Guiding Star`, `Falling Star`, `Stardust`, `Seven Stars`, `Glow`, `Convergence`, or `Venerate`.
- Expected result: star setup is played before payoff cards such as `Gamma Blast`, `Photon Cut`, `Meteor Shower`, `Big Bang`, or `Bombardment` when affordable.

## Necrobinder Early Engine Check

- Test Osty/Soul/Doom/Reaper combat hands with `Bodyguard` and zero-cost soul/draw cards.
- Expected result: `Bodyguard` is treated as Osty setup/support and should beat low-impact attacks when affordable.
- Check logs for `Play ... ->` on an Osty/ally target. `TargetType.Osty` cards should not be reduced to only a `none` target.
- Expected result: zero-cost Necrobinder draw/soul cards should be played early enough that the drawn cards can still be used.
- Expected result: known zero-cost Necrobinder soul-engine cards should receive first-action priority even before an active build profile is locked.
- Test an Osty damage card with text like `Osty deals 24 damage`; logs should show a `damage=...` and/or `future=...` score high enough to beat starter `Strike`.
- For Ironclad draw-block cards such as `Shrug It Off`, expected result is similar: play before the end of the turn when draw can still become a playable action.

## Block Discipline Check

- Enter combat where enemies deal low incoming damage, such as 5.
- Give the bot at least two pure block cards and one useful damage/setup card.
- Expected result: after incoming damage is covered, the bot should not spend another action on pure block unless it has block retention or a Body Slam/Barricade-style plan.
- Check logs for `blocked redundant block-only` when a redundant block action is scored.
- If only redundant block/status actions remain, expected result is a delayed end turn commit instead of repeatedly canceling end turn.
- Add `Burn`, `Disintegration`, or similar `take X damage` status cards to hand and confirm the bot blocks that extra damage.
- Add `Beckon` or similar `lose X HP` status cards and confirm it is counted as life risk but not treated as blockable damage.

## Core Build Power Priority Check

- Enter combat with an active build profile and 3 energy.
- Good checks: Ironclad `Demon Form` in Strength, Defect `Echo Form` in Lightning/Creative AI, Ironclad `Barricade` in Body Slam/Barricade, or Regent `Void Form`.
- Expected result: the bot plays the affordable core power early, usually first, when there is no lethal/survival emergency.
- If the bot skips the power, compare enemy incoming damage, lethal opportunity, and combat line scores before changing weights.

## Combat Potion Discipline Check

- Enter a safe normal combat while holding a potion.
- Expected result: the bot should not spend the potion just because it is legal; end turn should beat low-value potion actions.
- Enter an elite/boss or dangerous turn with meaningful incoming damage.
- Expected result: defensive potions should become eligible again when uncovered damage is severe; offensive potions should be more likely in elite/boss fights or dangerous turns with attack follow-up.
- Expected result: if a potion is selected, it should appear early in the combat line, before attacks/payoff cards that benefit from it.
- Check logs for `Combat score ... category=Potion` and `Combat line ... actions=[use_potion_...`.
- Boss damage potions and attack amplifiers such as `FIRE`, `EXPLOSIVE`, `STRENGTH`, `FLEX`, `DUPLICATOR`, `DUPLICATE`, and `ENERGY` should count as offensive potion tools when attacks are available.

## Coop Target Overkill Check

- Enter a multi-enemy combat with at least two AI-controlled players able to attack.
- Put one enemy at low HP and leave another target alive.
- Expected result: after the first bot queues lethal damage on the low-HP enemy, later bots should avoid overkilling that same enemy when they have another useful target.
- Check logs for `reserved team damage` and compare later `Combat score`/`Combat line` target choices.
- In single-target boss fights, reserved team damage should not suppress later bots from attacking the only enemy.
- Expected result with 8-player RMP lobbies: low-ranked rDPS bots should still spend damage cards on the boss instead of ending with energy just because earlier teammates reserved damage.

## Enemy-Aware Targeting and AoE Check

- Enter a multi-enemy combat with at least two living enemies.
- Give the bot an all-enemy damage card such as `Sweeping Beam` or another AoE card.
- Expected result: AoE card scores should rise when multiple enemies are alive and should count useful damage/kills across enemies.
- Give one enemy high incoming damage or a recognizable scaling/status/summon-style intent.
- Expected result: single-target attacks should prefer that high-threat enemy unless another target is clearly lethal or already reserved by teammate damage.

## Combat Learning Check

- Play several combats with AI teammates or host auto-mode enabled.
- Expected result: selected combat actions log `[AITeammate][Learning] Recorded combat decision`.
- After a won combat resolves room-end rewards, logs should show `[AITeammate][Learning] Updated experience` and `Completed combat learning`.
- On run cleanup, or after enough updates, logs should show `[AITeammate][Learning] Flushed experience`.
- Expected files are written under `%APPDATA%/SlayTheSpire2/sts2AITeammate/ai-learning/experience.json` and `%APPDATA%/SlayTheSpire2/sts2AITeammate/ai-learning/runs/<runId>.json`.
- Semantic combat score logs now include `learned=...`.
- Early tests should usually show `learned=0`; learned influence only starts after enough matching samples and is capped to a small score adjustment.

## Run Telemetry Check

- Play a run with AI teammates or host auto-mode enabled.
- Expected result: combat choices log `[AITeammate][Telemetry] Combat decision`.
- Expected result: combat choices also log `[AITeammate][Diagnosis] Combat turn` with best single action, line plan, damage/block opportunities, remaining energy, and diagnosis notes.
- Card rewards, relic choices, upgrades, rest sites, shop steps, potion rewards, and combat completion should also log `[AITeammate][Telemetry] ...`.
- End or abandon the run.
- Expected result: cleanup logs `[AITeammate][Telemetry] Flushed run telemetry`.
- Expected result: the flush log includes `nextRun=...`; the next test run starts with a fresh telemetry run id and empty in-memory counters.
- Expected files are written under `%APPDATA%/SlayTheSpire2/sts2AITeammate/ai-telemetry/latest-summary.json` and `%APPDATA%/SlayTheSpire2/sts2AITeammate/ai-telemetry/runs/<runId>.json`.
- Check `latest-summary.json` first. It lists each player, build, deck size, upgrade/heal counts, card picks/skips, shop removals, HP, and `probableIssues`.
- Full run files include diagnosis notes inside combat decision `notes`; use these to count repeated rotation mistakes across runs.
- Useful issue flags include `possible_block_shortage`, `possible_scaling_shortage_for_bosses`, `death_with_unused_potions`, `frequent_end_turn_with_energy`, `starter_strikes_still_used_often`, and `active_build_missing_many_core_cards`.
- If one player id shows multiple unrelated `characterId` values inside one run file, treat that file as stale/mixed telemetry from an older build rather than clean combat evidence.

## Silent Strength Check

- Sly/Discard: give Silent `Master Planner`, `Acrobatics`, `Prepared`, `Reflex`, `Tactician`, `Calculated Gamble`, or similar Sly/discard cards.
- Expected result: semantic combat logs show high `setup=...`, `future=...`, or build score, and the combat line should play Sly engine/draw cards before ordinary attacks while energy remains.
- Poison: test `Noxious Fumes`, `Deadly Poison`, `Bouncing Flask`, `Accelerant`, `Catalyst`, and `Burst`.
- Expected result: poison stackers gain future value, and `Accelerant`/`Catalyst` become valuable only when poison is already present.
- Shiv: test `Accuracy` plus `Blade Dance` or `Cloak and Dagger`.
- Expected result: `Accuracy`/setup should be favored before shiv payoff cards when no lethal/survival emergency overrides it.
- Rest site: with Silent below 40 HP and at least 10 missing HP, expected result is Rest over Smith; above that, upgrades can still win.

## RMP Larger Lobby Check

- Install `RemoveMultiplayerPlayerLimit` in `mods/RemoveMultiplayerPlayerLimit`.
- Optional: set `[multiplayer] max_player_limit=8` or higher in `mods/RemoveMultiplayerPlayerLimit/config.ini`.
- Open the AI teammate setup screen.
- Expected result: logs show `[AITeammate][RMP]` with the detected max player count.
- Expected result: the setup screen shows a compact scrollable slot grid up to the detected limit instead of only 4 player slots.
- Expected result: with 8/8 participants, the session participant chips stay clipped inside the session panel and do not render outside the panel.
- Click `Autofill Bots`.
- Expected result: all empty AI slots are filled by cycling through the available placeholder characters.
- Press `Proceed`.
- Expected result: logs show `Created StartRunLobby maxPlayers=... requested=...`, and the run starts with more than 3 AI teammates when RMP is installed and configured above 4.

## Host Auto-Mode Foreground Reward Check

- Enable host Auto-Mode with `F4`, then choose a reward/event that opens a foreground loot choice such as `Add a card to your deck`.
- Expected result: logs show `[AITeammate][AutoMode] Pausing host controller during foreground reward resolution` before the reward is processed.
- Expected result: Auto-Mode should pick/skip the card reward using the normal build scoring, or leave the reward manual if the game does not expose rewards in time.
- Expected result: after resolution, logs show `[AITeammate][AutoMode] Resuming host controller after foreground reward resolution`.
- If the loot button remains visible and unclickable, check for repeated `[AITeammate][Event] Executing fallback event option` lines while the foreground reward is open; that means the host controller is still re-entering the event path.
- If a potion reward appears while potion slots are full, expected result is a logged replacement or `skipped_no_slot_no_better_replacement`, not `Slot already contains a potion`.

## Reward Synergy Context Check

- After a card reward, inspect the run telemetry file under `%APPDATA%/SlayTheSpire2/sts2AITeammate/ai-telemetry/runs/`.
- Expected result: `card_choice` records should have `activeBuildId` set to the detected build id or `none`; they should no longer stay at `unknown`.
- Expected result: card choice notes include `active_build=<id>` and `:locked` when the deck has locked into a build.
- In combat logs, cards such as `ZAP`, `DUALCAST`, `BODYGUARD`, and `VENERATE` should no longer resolve as `effects=[]`; expected inferred effects include `LightningOrb`, `OrbEvoke`, `OstyGuard`, or `Star`.
- If a starter `Strike` beats an affordable setup card without lethal or survival pressure, compare `Semantic score ... setup=... build=...` before changing weights again.
- For follow-up runs, useful counters are `death_with_unused_potions`, `draw_left_while_energy_available`, and starter-Strike decisions with `rank > 1`.
- `SPOILS_MAP`, `SPOILS_OF_BATTLE`, and `CONQUEROR` should now receive inferred setup effects instead of staying empty.
