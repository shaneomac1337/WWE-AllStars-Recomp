# Cheats: which of the #15 requests can be built, and how

Date 2026-11-07.

Question: can infinite finishers, infinite signatures, max health, weak enemy and
no reversal be built? Where does each value or decision live, how do we tell the
human from the CPU, can each be switched during a match, and is it safe?

Method: Ghidra on `default.xexp` (project `WWEAllStarsTU2`, read-only session),
plus names in `AnimFSM_Andre.xxx` (`scripts/ue3names.py`) and `Coalesced.ini`.
Every address is TU2. The game was not run, so **no cheat has been seen working**.
"Inferred" marks what the code suggests but does not prove.

## Answer

| Cheat | Value or decision | Smallest change | Verdict | Effort |
|---|---|---|---|---|
| Infinite finishers | Finisher meter, stats +0x13C (max +0x144), stats = pawn +0x19B0 | Per-tick top-up in the pawn tick, `0x82AE7734` | Feasible | Small |
| Infinite signatures | Energy meter, stats +0x130 (max +0x138) | Same tick hook | Feasible with risk: that energy is the signature meter is inferred | Small |
| Max health | Health and body parts, pawn +0x780..+0x79C; all damage goes through `0x82ADABF8` | Midasm at `0x82ADABF8`, amount = 0 | Feasible with risk: pins and stun still work | Small |
| Weak enemy | Same function | Same hook, amount × N | Feasible; what "weak" means is an owner decision | Small (shared hook) |
| No reversal | Reversal trigger evaluate, `0x82BAB7B8` | Midasm at `0x82BABB84`, jump to the "no" exit `0x82BABBA4` | Feasible with risk: how the CPU presses is not traced; counters are separate | Medium |

Common to all five:

- **Who:** team test on +0x6FC bit 0x80 and +0x5A4 (below). Works for player 2, tag and 4-way by construction (inferred, untested).
- **Toggle:** all can change during a match. Each hook reads a hot-reload cvar on every call.
- **Online:** every hook is a no-op while `[0x83740590] != 0`.
- **Saves:** cheats do not write anything new, but a cheated win counts as a real win (below).

## Who is human

The vibration work uses +0x6FC bit 0x100. That bit is wrong here: it means "the AI
controller drives this pawn right now", which is also true of a tagged-out human.

- **Bit 0x100 is current possession.** Setter is vtable +0x450, `0x82ABD540`. `0x82ADE300` hands a pawn to its AI controller (pawn +0x1AA0) and sets it. `0x82ADE3A0` hands it back to pawn +0x1A9C and clears it.
- **Bit 0x80 is "spawned as a CPU wrestler" (inferred from three readers).**
  - The spawner `0x82BADDE8` is the only writer. It sets 0x80 together with AI possession (`0x82BAE0C0`) when spawn info +0x80 has bit 0x20000000 and `0x82B13F90()` returns 0, otherwise it clears both.
  - `0x8259A770` returns a pawn to its player only if 0x80 is clear and 0x100 and 0x40 are set: a human pawn temporarily run by AI.
  - `0x82ADE590` sets 0x40 when it hands an active human pawn with +0x5A8 == 1 to the AI.
  - `0x82BE9138` (end-of-match awards) treats 0x80 clear as the player and counts opponents by the same bit.
- **+0x5A4 is the team** (inferred). `0x82BE9138` and `0x82AE07B0` treat a different +0x5A4 as an opponent.
- **Why a team test, not just the pawn's own bit.** The tag swap `0x82AE1380` exchanges +0x1A9C between two partners. With a CPU partner, the human ends up driving a pawn that has 0x80 set, because no code writes 0x80 after spawn.

Proposed test, using the game's pawn table (`0x82B03578` returns `0x837450D0`; `0x82B02C78` gives count at +4, pawn `i` at +0x0C + 4i, at most 4):

- A team is a **player team** if any of its pawns has 0x80 clear.
- **Player side:** pawns on a player team. **Enemy:** all others.

| Match | Infinite meters, max health | Weak enemy, no reversal |
|---|---|---|
| 1 human vs CPU | Human | CPU |
| 2 humans, same team vs CPU | Both | CPU |
| 2 humans, opposing | Both | Nobody (no enemy exists) |
| Tag, human + CPU partner | Both partners | CPU team |
| 4-way, 1 human | Human | The 3 CPUs (inferred: each 4-way pawn has its own +0x5A4) |

Not verified:

- 4-way team values at run time.
- `0x837455F8` (returned by `0x82B13F90`): when non-zero, a pawn spawns without AI, so a CPU slot would count as human. It is written at `0x82B13FA4` and cleared at `0x82B14C00`; its purpose was not traced.
- Tag and 4-way swaps with this test have only been read, not played.

Pad index is not usable as a test: the spawner gives every pawn a controller with a
`LocalPlayer` (`0x82BADDE8`, inferred from its controller search and `0x82BADB48`), which agrees with the note in
`controller-vibration.md` that CPU wrestlers have one too.

## The three meters

`0x82B56308` is a developer console handler. Its `SPV <n> <field> <value>` command names and sets each value, which is the best single piece of evidence here:

| SPV field | What it does | Used for |
|---|---|---|
| `HEALTH` | vtable +0x3E0 (`0x82AE2C18`) part 0 | Health |
| `MOMENTUM` | vtable +0x3E8 (`0x82ADA608`) | Momentum, +0x74C of max +0x750, stage byte +0x76C |
| `ENERGY` | zero stats +0x130 (`0x82B0BE70`), add (`0x82B0C138`) | Energy |
| `FINISHER` | zero stats +0x13C (`0x82B0BE58`), add (`0x82B0C070`) | Finisher |
| `STUN` | +0x7D0 = 0, `0x82AE2B10` | Stun |

Momentum is a third meter and does not gate moves as far as I can see: `AnimFSM_Andre.xxx` has no name containing "momentum". Do not write it for these cheats.

## Infinite finishers

**Where.**

- Gate: `0x82BA41C0` (vtable `0x821C0338`, an FSM trigger, class name not resolved) is true only when stats +0x13C **equals** +0x144 (`fcmpu`, `0x82BA420C`).
- HUD fill and `SFX_UI_HUD_FINISHERREADY_01` read the same value (`0x82B27F60`, `0x82B28038`, `0x82B281F4`).
- Spent in two places: on entering an attack state with state +0x84 bit 0x02000000, `0x824E3824` calls the subtract `0x82B0C118` with character data +0x180. On a hit with pawn +0x76D == 5, `0x82B0CF14` zeroes it.
- Names `bApplyFinisherCostAttempt` in the FSM package fit the first.

**Change.** Midasm `WweCheatTick` at `0x82AE7734` (`bl 0x82ADD0A8`, the momentum tick). r31 is the pawn (`0x82AE772C or r3,r31,r31`). r3 must survive, since the callee takes it.

- If the cheat is on, the pawn is on the player side, and stats +0x13C < +0x144: call the game's `0x82B0C070(stats, max - current)`.
- Call rather than store, because that function also raises the per-player HUD "ready" state when the meter fills. A plain store would skip it (inferred consequence).
- The pawn tick is reached only through vtable `0x821DB56C`; a paused world does not tick actors (`controller-vibration.md`, section 2), so nothing is written during a pause (inferred).

**Risks.** `SFX_UI_HUD_FINISHERREADY_01` may replay on every refill, once per finisher. Hit reactions see the meter empty for at most one tick. At 120 fps the write simply runs twice as often.

**Off during a match:** yes. The meter stays where it is and is spent normally.

## Infinite signatures

**Where.**

- Gate: `0x82BA4028` is true when stats +0x130 is at least character data +0x168 + +0x16C, and only for a state whose flags have bit 0x08000000. The FSM package has `GameAnimFSMEnergyTrigger`, `bApplyEnergyCostAttempt` and `bApplyEnergyCostSuccess`.
- Spent: attempt at `0x824E37B4`, hit at `0x82B0CEF8` (state bit 0x04000000 or pawn +0x1A04 bit 0x02000000, amount +0x16C), plus `0x82AE3B3C` and the logic event `0x8259CF90`. All clamp through `0x82B0C158`, or the hit path's own store.
- It gains on hits except while pawn +0x76D == 15, EXTREME_SIGNATURE (`0x82B0D3D8`).
- HUD: `0x82B28308` plays `SFX_UI_HUD_SUPERDRAIN_01` when it drops.

**Inferred: energy is the signature meter.** No string says so. The evidence is the +0x76D == 15 exemption and the "super" drain sound. If it is wrong, this row fills the wrong bar and signatures stay limited. The first owner check settles it.

**Change.** Same tick hook: if on and player side, store stats +0x138 into +0x130 (`0x82B0C138` has no side effects to preserve, and adds with a clamp).

**Risk.** `SUPERDRAIN` tests an exact drop, so a top-up in the same tick might mute it. Cosmetic.

## Max health

**Where.** vtable +0x3D4 returns the raw value, +0x3D8 the max, +0x3DC the percentage (`0x82ADA9F0`). Part 0 is +0x780/+0x784, then parts 1-3 at +0x788, +0x790, +0x798.

All damage reaches `0x82ADABF8(pawn r3, part r4, amount r5)`, whose only caller function is the damage function `0x82AE58C0`. It calls it for part 0 and parts 1-3 (`0x82AE61B8`, `0x82AE62C8` onward). Buffs and damage over time come through the same function (9 callers, list in `controller-vibration.md`).

**Why not a per-tick refill.** In `0x82AE58C0` the KO test runs straight after the subtract: at or below the limit, a finisher (type 5) reaches `0x82AEA6E8(manager, pawn, 0xD)`. A tick hook would restore too late. `SetHealth`-style refilling also churns the HUD (`0x82AF77B0`).

**Change.** Midasm at `0x82ADABF8` with `r3, r4, r5`, `PPCRegister&` as in `src/vibration/enhanced_hooks.cpp`. If on and r3 is on the player side, set r5 to 0. The subtract and clamp then run harmlessly. Existing hooks sit inside functions, not on the first instruction; if codegen objects, `0x82ADABFC` works, since r5 is first read after the jump.

**What it does not stop (not verified).**

- Stun goes through `0x82AE2B10`, a separate path.
- Pin win type 1 in `0x82AEA6E8` does not read health directly. Whether the pin minigame depends on health was not read. Inferred: a full-health player can probably still lose to a pin if they fail it. Cage, ring-out and count wins are also separate.
- `0x82ADAC78` adds health on a successful reversal (`0x82BAB7B8`) and is unaffected.

The player sees a bar that never moves. Say "health", not "cannot lose", in the row text.

**Off during a match:** yes. Health stays where it was.

## Weak enemy

**Where.** Same hook. If on and r3 is an enemy: scale r5. r5 is a signed 64-bit value (`0x82ADABF8` tests the 64-bit result), so scale as a signed value.

Alternatives rejected:

- Victim damage multiplier +0x7C4 (`0x82AE5A2C`): buffs multiply it and divide it back (`0x82BFD080` onward), so a write collides.
- Attacker +0x7BC/+0x7B4: same, and it would need team logic per attacker.

**Low-health rules in `0x82AE58C0`** (TU2 `0x82AE60C8` onward):

- Compare hp/max with `0x82AF5908()` +0x154. Above it, or with manager +0x38 bit 0x80000: full damage.
- Otherwise compare with +0x158. Above it, only types 15, 5, 4, 11-14, 18 do damage. At or below it, only a finisher does, and that is the KO.
- Inferred mapping: +0x154 is `fDangerZoneLimit`, +0x158 is `fCriticalHealthLimit`, in `[Game.GameplayManager]`. Both are 0.0 in `Coalesced.ini`, which would leave these rules dormant until health hits 0.
- **Units do not match:** `0x82AE60C4` compares a fraction, while `0x82AE6464` compares a percentage from vtable +0x3DC, and the floor code scales +0x154 by 0.01. At 0.0 this does not matter. Run-time values were not read.

So scaling should bring the CPU to zero fast, and a finisher should still be what ends it by KO. Pins and submissions are as usual. Submission uses parts 1-3 (`0x82AEA6E8` case 2), which the same hook scales.

**Owner decision.** Damage × N (proposed N = 3) or "starts low". The first needs no match-start hook and can be switched during a match. Proposed: damage × 3.

## No reversal

**Where.** `GameAnimFSMReversalButtonTrigger`, vtable `0x821CB7F0`, evaluate at +0xD0 `0x82BAB7B8`, press handler at +0xD8 `0x82BAB548` (which sets trigger +0x5C bit 31 inside the window). `AnimFSM_Andre` alone holds 400 of them (`docs/research/modding.md`). The trigger's result is the decision.

**Which pawn is the reverser (inferred, strong).** The FSM pawn is: on success `0x82BAB7B8` raises `0x82B260F8(..., pawn, pawn +0x81C, 1, ...)`, and event 1 is `ECombatEvent_Reversal` (enum order from the strings). The spam lockout (+0x1A04 bit 0x20000000, set by ReversalKeyPress `0x82ADB130`) is on that same pawn, and it gets the health refund. Note +0x1A04 bit 0x04000000 is read on the *target*, and only `GamePlayerController::TickInput` (`0x82B54980`, at `0x82B550B0`) sets it. That looks like a counter-input by the attacker, not the reverser's press.

**The game already has a "no" exit.** `0x82BABB94`-`0x82BABBA4`: if the spam flag is set, clear +0x5C and return 0. So:

- Midasm at `0x82BABB84` (`lwz r11,0x78(r31)`), registers `r30` (pawn), `jump_address_on_true = 0x82BABBA4`.
- Return true if on and r30 is an enemy. Starting at `0x82BABB84` also skips the trigger's exemption bit (+0x78 bit 31) tested at `0x82BABB88`.
- The code at `0x82BABBA4` uses only r31, r11, r10 and r3, and restores its frame itself.

**Why not set the spam flag.** Writing +0x1A04 bit 0x20000000 per tick would also work for most triggers but not those with the +0x78 exemption, and the trigger clears it on its first evaluate (`0x82BAB844`).

**Not verified.**

- How the CPU presses. I found the window flag (+0x6FC bit 0x100000, set in the evaluate) and the reader `0x82B82100` (read and clear, AI logic, inferred to be `IsReversalWindowOpen`). I did not trace how AI reaches +0xD8. The hook works whichever way, if CPU reversals go through this trigger; that they do is inferred from these FSMs being per wrestler rather than per controller.
- Side effects of a CPU that keeps trying: it may stand in a "reversal attempt" state, or trigger animation it cannot finish. The return 0 is the game's own path for a spam-locked press, so it should look like a late press.
- **Counters are separate.** `CounterTrigger`, `GrabCounterTrigger` and the counter volumes (`EnableCounterVolume`) are category 5, not 6 (`controller-vibration.md`, section 4). The CPU can still counter. Also blocking them is a follow-up, not this hook.

**Off during a match:** yes. A press under way ends normally.

## Online guard

`[0x83740590] != 0` means a Net-Z session is up: the engine tick's own test (`0x82417CD4`), and the same check as `At120Allowed` (`src/game_tick_log.cpp`) and `src/faster_starts.cpp`. It is set at `0x82A60560`. A second write at `0x82A5F9A0` was not read (inferred: it clears).

Every hook returns at once when it is non-zero, and the shared predicate should do this. It must: SyncSim checksums wrestler state each step (`high-framerate.md`, section 5), so a cheat on one side would desync. No online-specific testing is possible in this task.

Consequence to check: the guard goes by session, not by wrestler. An online lobby that has not started a session would still allow cheats on local pawns, which has no effect until a session exists.

## Saves, unlocks and Fantasy Warfare

- **No cheat writes the save.** They touch only pawn fields in guest memory, which are built at spawn (`0x82BB0C00`, `0x82BB7DE0`) and thrown away with the match.
- **A cheated win counts as a win.** `0x82BE9138` awards achievements through `0x82BD3790` when every opponent is CPU, using thresholds in `Game.GameAchievementTuneData` (`0x82BFBD68`: `SignatureMoveLimitPerMatch`, `FinisherLimitOverTime`, and others). `0x82B260F8` and `0x82BCE310` read the same tune data; neither was read in full.
- **Fantasy Warfare and unlocks** (`[0x83744F48]+0x28`, `+0xBC`, `docs/research/unlock-everything.md`) should advance on a cheated win as on any other (inferred). The mode flag was not researched, so this cannot say whether a Fantasy Warfare match can be recognised.
- **Precedent:** the `wwe_unlock_everything` row already tells the player that progress earned with it on is saved. Same wording fits here. Blocking awards would be a separate hook and is not proposed.

## Docs that disagree with the code

- `docs/research/controller-vibration.md` and `impact-events.md` use base v5 addresses. The ticket's starting points `0x82AE39B4`, `0x82AE392C` and `0x82AE3910` are base; TU2 has the damage function at `0x82AE58C0` and the hooks at `0x82AE63D4` and `0x82AE649C` (manifest). Nothing base is copied here.
- The ticket and `controller-vibration.md` say health is read through vtable +0x3DC. That is the **percentage**; raw is +0x3D4.
- `src/vibration/game_state.h` calls `0x83740590` `kPauseMenuOverride`; `src/game_tick_log.cpp` and `high-framerate.md` call it the Net-Z session flag. Same address, and compatible: a session means no pause.
- `src/game_tick_log.cpp` reads a pawn count at manager +0x40; the game's own accessor uses +4. Not an error (different field, same manager), but use `0x82B02C78`'s layout here.

## Owner checks (cannot be settled statically)

1. Fill test: with both meter rows on, confirm which HUD bar each fills, and that signature and finisher can be used back to back. This settles "energy = signature".
2. Take a finisher at max health, and a pin attempt. Confirm health does not move, and see what the pin does.
3. Weak enemy: a normal hit takes three times the health. Win by finisher.
4. No reversal: a long grapple session against a hard CPU, watching for a stuck reaction state.
5. Tag and 4-way once each, including a tag swap with a CPU partner.

## What the player sees (proposed, for the spec)

**Which cheats.** Five new On/Off rows, all **Off** by default, under a "Cheats" group in the existing **GAMEPLAY** tab.

| Row | Text | Applies to |
|---|---|---|
| Infinite finishers | The finisher bar is always full. | You and your tag partner |
| Infinite signatures | The signature bar is always full. | You and your tag partner |
| Max health | You take no health damage. You can still be pinned, stunned or counted out. | You and your tag partner |
| Weak enemy | CPU opponents lose health three times as fast. | CPU opponents only |
| No reversal | CPU opponents can't reverse your moves. They can still counter. | CPU opponents only |

**Who.** "You" is every human player and their team, so player 2 gets them too. Two humans against each other both get the first three, and the last two do nothing.

**Where.**

- One `PlayerSettings()` row each in `src/settings/registry.h`, next to `wwe_unlock_everything`, so the same list drives both places.
- **In game:** F4 settings page. Rows apply at once, also during a match (`kHotReload` cvars).
- **Launcher:** Gameplay tab, marked `startup=true` as `fullscreen` is, which shows them in the launcher while they stay live in game. The launcher writes only those keys (`docs/research/live-settings.md`).
- **Online:** no effect, stated once in the group text: "Cheats do nothing in online matches." Rows stay visible rather than greyed, since the guard is at run time.
- **Saves:** group text: "Wins with cheats on still count towards progress and achievements."

**Open for the owner.**

1. Weak enemy as damage × 3, or a different strength.
2. Whether the rows should reset to Off on each start. Proposed: remembered, like every other row.
3. Whether a row for "no counters" is wanted later.
