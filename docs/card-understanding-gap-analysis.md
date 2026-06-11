# Card Understanding Gap Analysis

> Analyzes which in-game cards the AI understands, at what depth, and what is missing.

---

## Understanding Tiers (0–5)

| Tier | Layer | What the AI knows | Scope |
|------|-------|-------------------|-------|
| **0** | Catalog | CardId, Name, Cost, Type, Rarity, TargetType, description text, Keywords, Tags, Flags (Exhaust/Ethereal/Retain/Innate), DynamicVars | **ALL cards** — auto-built from `ModelDb.AllCards` in `CardCatalogBuilder.Build()` |
| **1** | Basic Effects | Damage, Block, Vulnerable, Weak, Strength, Dexterity, Poison, Draw, Energy — parsed from dynamic vars or regex | Cards using standard dynamic var names (most common/basic cards). **Will miss** cards whose vars use non-standard names |
| **2** | Token-Inferred Effects | LightningOrb, FrostOrb, DarkOrb, OrbEvoke, OrbSlot, Focus, Star, OstyGuard, Soul, Spoils, UnknownSetup | Only specific card IDs matched in `CardResolver.AddInferredSemanticEffects` (lines 220–294) |
| **3** | Archetype Role | Cards classified as Setup/Payoff/Cycle/Defense/Finisher/Avoid within active build via `CombatBuildRoleEvaluator` | Cards explicitly listed in `AiBuildArchetypeCatalog` or matched by token patterns in role classification |
| **4** | Special Scoring | Bonus/penalty scores for specific card families (Necrobinder soul, Silent sly engine, poison, shiv, orb/stars setup) | Cards matched by `CombatBuildRoleEvaluator.Is*` methods called in `CombatActionScorer` and `CombatTurnLinePlanner` |
| **5** | Full Strategic Model | Complex conditional effects modeled (e.g., BodySlam = block-as-damage, Finisher = attacks-spent, Catalyst = poison-multiply) | **Very few** — only cards with special-case logic in scorer or planner |

---

## Ironclad

### Builds & Listed Cards (Tier 3+)

| Build | Core Cards | Support Cards |
|-------|-----------|---------------|
| **Strength** (Tier S) | Heavy Blade, Inflame, Demon Form, Offering | Fight Me, Battle Trance, Shrug It Off, Flame Barrier, Whirlwind, Bludgeon, Limit Break |
| **Barricade** (Tier A) | Barricade, Body Slam, Impervious, Shrug It Off | Entrench, Flame Barrier, Power Through, Second Wind, True Grit |
| **Exhaust** (Tier A) | Corruption, Dark Embrace, Feel No Pain, Fiend Fire | Second Wind, True Grit, Burning Pact, Offering, Seeing Red |
| **Bloodletting** (Tier A) | Rupture, Inferno, Bloodletting, Tear Asunder | Offering, Hemokinesis, Combust, Reaper, Limit Break |
| **Strike** (Tier B) | Perfected Strike, Twin Strike, Pommel Strike, Hellraiser | Strike, Wild Strike, Swift Strike, Sword Boomerang, Inflame |
| **Self-Wound** (Tier B) | Tear Asunder, Rupture, Brand, Offering | Bloodletting, Hemokinesis, Combust, Reaper, Limit Break |

### Token-recognized in CombatBuildRoleEvaluator
- `IsWeakStarterStrike`: Strikes that aren't Pommel/Twin/Perfected/Focused → gets penalty in late-game
- `IsPriorityDrawBlockCard`: Draw + block/energy/0-cost cards (generic, not ironclad-specific)
- `IsCoreBuildPower`, `IsEngineSetup`, etc. (generic checks)
- Setup cards via IsSetupCard: Power, strength, dexterity, vulnerable → generic
- Payoff cards: `HasToken(card, "HEAVYBLADE", "SWORD", "TWIN", "POMMEL", "WHIRLWIND", "BLUDGEON")` (strength build), `BODYSLAM` (barricade), `STRIKE`/`PERFECTED` (strike)

### Gaps
- Fire Breathing (self-wound payoff)
- Evolve (status draw)
- Sentinel, Power Through, Wild Strike (status synergy cards — part of exhaust/wound but not individually listed)
- Offering's draw has special 0-cost energy scoring but no Exhaust-specific or status-specific future value
- No specific handling for: Juggernaut, Berserk, Spot Weakness, Flex, Sword Boomerang (generic strength but not archetype-specific)
- No understanding of status cards (Wound, Burn, Dazed, Void, Slimed) or their generation

---

## Silent

### Builds & Listed Cards (Tier 3+)

| Build | Core Cards | Support Cards |
|-------|-----------|---------------|
| **Poison** (Tier S) | Noxious Fumes, Deadly Poison, Accelerant, Bouncing Flask | Catalyst, Burst, Poisoned Stab, Footwork, Backflip, Leg Sweep, Malaise, Corpse Explosion, Neutralize |
| **Sly** (Tier S) | Master Planner, Acrobatics, Prepared, Reflex | Tactician, Calculated Gamble, Concentrate, Tools of the Trade, Backflip, Adrenaline, Untouchable |
| **Grand Finale** (Tier S) | Grand Finale, Acrobatics, Calculated Gamble, Tactician | Prepared, Reflex, Backflip, Concentrate, Expertise, Master Planner |
| **Shiv** (Tier A) | Blade Dance, Accuracy, Cloak and Dagger, Infinite Blades | Finisher, After Image, Adrenaline, Backflip, Terror, Neutralize, Fan of Knives |
| **Envenom** (Tier A) | Envenom, Snakebite, Fan of Knives, Finisher | Blade Dance, Cloak and Dagger, Accuracy, Poisoned Stab, Burst |
| **Combo Execution** (Tier A) | Wraith Form, Burst, Nightmare, Footwork | Backflip, Leg Sweep, Malaise, Adrenaline, Prepared |

### Token-recognized in CombatBuildRoleEvaluator
- `IsSilentSlyEngineCard`: Recognizes SLY, MASTERPLANNER, ACROBATICS, PREPARED, REFLEX, TACTICIAN, CALCULATEDGAMBLE, CONCENTRATE, TOOLSOFTHETRADE, TOOLS + any card with draw/energy/discard in active sly build
- `IsSilentSlyPayoffCard`: GRANDFINALE, SNEAKY, EVISCERATE
- `IsSilentPoisonSetupCard`: NOXIOUS, DEADLYPOISON, ACCELERANT, BOUNCING, POISON, CATALYST
- `IsSilentShivSetupCard`: ACCURACY, AFTERIMAGE, INFINITEBLADES, ENVENOM
- `IsSilentShivPayoffCard`: BLADEDANCE, CLOAKANDDAGGER, SHIV, FINISHER, FANOFKNIVES
- Special-scored: `ScorePoisonFutureValue` — Catalyst/Burst/Bouncing scale with current poison; poison with poison build gets extra value
- Special-scored: `ScoreSilentFutureValue` — Sly engine, poison accelerator/catalyst, shiv setup
- Sly engine draw scoring has the `late_draw_no_energy` fix (Tier 4 special-case in CombatActionScorer + CombatTurnLinePlanner)

### Gaps
- Expertise, Setup, Distraction, Outmaneuver (not explicitly in any build)
- Corpse Explosion only listed as support in poison; its AOE poison-on-death mechanic not modeled
- Malaise listed only in Combo Execution; its scaling STR debuff not modeled
- Phantasmal Killer (double damage next turn) — not understood
- Bullet Time (hand becomes 0-cost) — not understood
- Storm of Steel (cards to shivs) — not listed
- Choke (damage on card play) — not modeled
- Bane (damage if poisoned) — not modeled
- Reflex (draw on discard) — listed but discard synergy not modeled at effect level
- Tactician (energy on discard) — listed but discard synergy not modeled at effect level
- Calculated Gamble (discard hand, draw that many) — listed but discard-modeling not at effect level
- Tools of the Trade (draw + discard each turn) — listed but persistent effect not modeled

---

## Defect

### Builds & Listed Cards (Tier 3+)

| Build | Core Cards | Support Cards |
|-------|-----------|---------------|
| **Lightning** (Tier S) | Zap, Defragment, Echo Form, Electrodynamics | Ball Lightning, Storm, Static Discharge, Capacitor, Coolheaded, Turbo |
| **Claw** (Tier S) | Claw, All for One, Scrape, Feral | Go for the Eyes, Beam Cell, Hologram, Turbo, Overclock, Rebound |
| **Frost** (Tier A) | Glacier, Defragment, Coolheaded, Capacitor | Cold Snap, Loop, Consume, Echo Form, Hologram |
| **Dark Orb** (Tier A) | Darkness, Multi-Cast, Defragment, Consuming Shadow | Recursion, Dualcast, Hologram, Echo Form, Capacitor |
| **Creative AI** (Tier A) | Creative AI, Echo Form, Storm, Machine Learning | Defragment, Heatsinks, Capacitor, Turbo, Hologram |

### Token-recognized effects (Tier 2)
- LightningOrb: ZAP, BALL_LIGHTNING, LIGHTNING_ORB, CHANNEL1_LIGHTNING
- FrostOrb: COLDSNAP, COOLHEADED, GLACIER, CHILL, FROST_ORB, CHANNEL1_FROST
- DarkOrb: DARKNESS, DOOM_AND_GLOOM, DARK_ORB, CHANNEL1_DARK
- OrbEvoke: DUALCAST, MULTICAST
- OrbSlot: CAPACITOR
- Focus: DEFRA, FOCUS

### Token-recognized in CombatBuildRoleEvaluator
- `IsOrbSetupCard`: ZAP, BALL_LIGHTNING, BALL, LIGHTNING, COLDSNAP, COLD, COOLHEADED, GLACIER, DARKNESS, DARK, CHILL, DOOM_AND_GLOOM, ELECTRODYNAMICS, STATIC_DISCHARGE, TEMPEST, STORM, CAPACITOR, CHAOS, RECURSION
- `IsOrbPayoffCard`: DUALCAST, MULTICAST, RECURSION, CONSUME, CONSUMING
- Orb/star setup scoring via `IsEngineSetup` checks `IsOrbSetupBuild` (lightning/frost/dark_orb/creative_ai) → `IsOrbSetupCard`
- Premiere starter strike penalty for orb builds
- `IsPriorityDrawBlockCard` generic bonus

### Gaps
- Coolheaded, Turbo — basic orb support but listed in support, no special effect modeling for orb draw
- Fission (evoke all orbs, draw/energy) — NOT listed anywhere
- Rainbow (channel 1 of each orb) — NOT listed
- Melter (damage + remove block) — not modeled
- Boot Sequence (ethereal block + draw) — not modeled (ethereal not penalized for block)
- Buffer (prevent next damage) — not modeled
- Reboot (shuffle hand into draw, draw that many) — not modeled
- Force Field (costs 1 less for each power played) — cost reduction not modeled
- Amplify (next power played twice) — not modeled
- Self-Repair (heal) — not modeled
- White Noise (random power) — not modeled
- Stack (block = cards in discard) — conditional block not modeled
- Repulse (damage + push) — knockback mechanic not modeled
- Barrier (block based on current block) — conditional not modeled
- Overclock's exhaust not leveraged in scoring
- Consume (lose orb slot for focus) — listed in Frost support, but tradeoff not modeled

---

## Regent

### Builds & Listed Cards (Tier 3+)

| Build | Core Cards | Support Cards |
|-------|-----------|---------------|
| **Forge** (Tier S) | Seeking Edge, Conqueror, Falling Star, Sword Sage | Forge, Sovereign Blade, Glow, Convergence, Knockout Blow |
| **Star Burst** (Tier A) | Stardust, Seven Stars, Glow, Black Hole | Big Bang, Convergence, Meteor Shower, Falling Star |
| **Void Form** (Tier A) | Void Form, Big Bang, Decisions Decisions, Convergence | Black Hole, Meteor Shower, Stardust, Glow |
| **Bombardment** (Tier B) | Bombardment, Meteor Shower, Gamma Blast, Knockout Blow | Falling Star, Seven Stars, Glow, Conqueror |

### Token-recognized effects (Tier 2)
- Star: VENERATE, GUIDING_STAR, STARDUST, CONVERGENCE, CONQUEROR, FALLING_STAR

### Token-recognized in CombatBuildRoleEvaluator
- `IsStarSetupCard`: GUIDING_STAR, FALLING_STAR, STARDUST, SEVEN_STARS, GLOW, CONVERGENCE, VENERATE
- `IsStarPayoffCard`: BIG_BANG, BLACK_HOLE, METEOR, BOMBARDMENT, GAMMA, PHOTON, KNOCKOUT
- Setup via `IsEngineSetup` → `IsStarSetupBuild` (forge/star_burst/void_form/bombardment) → `IsStarSetupCard`
- Setup cards in IsSetupCard: CONQUEROR, SEEKING, VOID
- Payoff cards in IsPayoffCard for builds: forge → FALLING, SWORD, KNOCKOUT, PHOTON, SEEKING; star_burst/void_form/bombardment → FALLING, SEVEN, BIGBANG, METEOR, GAMMA, BOMBARDMENT

### Gaps
- Most Regent cards seem reasonably well covered by Star archetypes
- Void Form (ethereal, energy each turn) — listed in Void Form build core but game effect not modeled
- Decisions, Decisions (draw + discard down) — listed but discard mechanic not effect-modeled
- Conqueror — listed but persistent strength modeling not detailed
- Any Forge (upgrade-in-combat mechanic) cards not effect-modeled
- Seeking Edge (draw from exhaust pile) — not effect-modeled
- Sovereign Blade — blocker-type mechanic not modeled
- Glow, Convergence mechanics not modeled at effect level (only token-matched)
- Gamma Blast — listed but delayed-damage mechanic not modeled

---

## Necrobinder

### Builds & Listed Cards (Tier 3+)

| Build | Core Cards | Support Cards |
|-------|-----------|---------------|
| **Osty** (Tier S) | Dirge, Invoke, Borrowed Time, Reanimate | Bodyguard, Summon, Sacrifice, Grave Warden, Danse Macabre, Lethality |
| **Soul** (Tier S) | Haunt, Soul Storm, Death March, Capture Spirit | Borrowed Time, Dirge, Reanimate, Grave Warden, Danse Macabre |
| **Doom** (Tier A) | Capture Spirit, Countdown, Grave Warden, Danse Macabre | Haunt, Borrowed Time, Dirge, Reanimate |
| **Reaper** (Tier A) | Reaper Form, The Scythe, Eradicate, Lethality | Death March, Soul Storm, Borrowed Time, Capture Spirit |

### Token-recognized effects (Tier 2)
- Soul: BORROWEDTIME, RIGHTHANDHAND, SOUL
- OstyGuard: BODYGUARD
- Spoils: SPOILSMAP, SPOILSOFBATTLE

### Token-recognized in CombatBuildRoleEvaluator
- `IsNecrobinderEarlySoulCard`: SOUL, BORROWED, RIGHTHAND, HAUNT, CAPTURE, SPIRIT, DIRGE, INVOKE + any 0-cost draw/energy/exhaust in active necro build
- `IsNecrobinderFreeSoulDraw`: Necrobinder soul card that draws
- `IsOstyGuardCard`: BODYGUARD, GUARDIAN, PROTECT
- Setup in IsSetupCard for osty/soul/doom/reaper: INVOKE, BORROWED, DIRGE, REANIMATE, HAUNT, CAPTURE, COUNTDOWN, REAPERFORM, SOUL
- Payoff in IsPayoffCard: SOULSTORM, DEATHMARCH, SCYTHE, ERADICATE, UNLEASH

### Special scoring (Tier 4)
- **ScoreResourceSetup** (CombatActionScorer:347–355): Necrobinder soul cards get +80 (has follow-up) or +10 (no follow-up), +64 base, +24 if energy > 0
- **ScoreBuildCombatFit** (CombatActionScorer:645–658): Early soul gets 92 + 28 (energy>0) + 24 (unplayed non-soul actions)
- **ScoreNecrobinderFutureValue** (CombatActionScorer:898–935): Soul cards get +76 + 28 (energy>0); OstyGuard +34/22; high-damage soul/osty/death/unleash/reaper/scythe cards get damage scaling
- **CombatTurnLinePlanner** (lines 722–762, 776–808, 850): Late-draw energy=0 penalty applied in Apply, ScoreBuildRotation, and LineNode

### Gaps
- Invoke (summon Osty) — listed in Osty core but summon mechanics not modeled
- Summon, Sacrifice, Reanimate — Osty/soul synergy but mechanics not effect-modeled
- Bodyguard (damage redirect to ally) — only token-matched as setup, redirection not modeled
- Reaper Form, The Scythe — listed in Reaper build but mechanics not effect-modeled
- Countdown — listed but delayed-damage mechanic not modeled
- Lethality — temporary strength buff, not modeled
- SpoilsMap/SpoilsOfBattle — token-matched but spoils mechanic not modeled
- Grave Warden, Danse Macabre — listed but mechanics not effect-modeled
- Eradicate — listed but mechanics not modeled

---

## Colorless Cards

### What's Handled
- No dedicated colorless builds or archetypes
- Basic effects (Damage, Block, Draw, Energy, etc.) parsed via Tier 1 dynamic vars / regex
- Generic role classification via `CombatBuildRoleEvaluator.Classify`: damage→Finisher, block→Defense, cost=0/draw→Cycle

### Gaps
- No special handling whatsoever — colorless cards evaluated purely on generic stats
- Apotheosis (upgrade all) — huge effect, not modeled
- Mayhem (play top card each turn) — not modeled
- Master of Strategy (draw) — treated as generic draw card
- Panic Button (massive block + frail) — tradeoff not modeled
- The Bomb (delayed 40 damage) — not modeled
- Hand of Greed (gold on kill) — not modeled
- Sadistic Nature (damage when debuff) — not modeled
- Flash of Steel, Finesse (0-cost damage/block, draw) — treated as generic
- All non-damage/block colorless cards (metamorphosis, trip, dark shackles, etc.) — effects not parsed

---

## Summary: Strategic Understanding by Character

| Character | Builds | Cards in Archetypes | Special Scoring | Effect-Level Gaps |
|-----------|--------|--------------------|-----------------|-------------------|
| **Ironclad** | 6 | ~35 listed | None (generic only) | Exhaust synergy, status synergy, self-wound, HP-as-resource, strength-as-block cards |
| **Silent** | 6 | ~45 listed | Sly engine, poison future, shiv setup, starter strike penalty | Discard-as-engine at effect level, phantasmal, bullet time, bane, choke, defense-as-attack |
| **Defect** | 5 | ~30 listed | Orb setup/starter strike penalty | Fission, rainbow, buffer, reboot, amplify, force field cost reduction, block conditionals |
| **Regent** | 4 | ~25 listed | Star setup/starter strike penalty | Specific star/forge mechanics, void form persistent, forge upgrade mechanic |
| **Necrobinder** | 4 | ~20 listed | **Extensive** — soul cards in 3 scoring layers | Invoke/summon/osty mechanic, reaper form transformation, delayed effects |
| **Colorless** | 0 | 0 listed | None | All colorless cards are effect-blind beyond basic damage/block/draw |

---

## Recommended Implementation Priority

1. **Necrobinder** cards outside soul/osty archetype — currently focusing debug on this character, need remaining cards understood
2. **Ironclad exhaust/wound synergy** — status generation cards not modeled as setup
3. **Silent discard-engine cards** (Tactician, Reflex, Calculated Gamble) — listed in builds but discard trigger not effect-modeled
4. **Colorless cards** — enhance basic effect parsing or add special cases for major ones (Apotheosis, The Bomb, etc.)
5. **Defect utility cards** (Fission, Reboot, Buffer, Amplify) — major cards not listed in any build
