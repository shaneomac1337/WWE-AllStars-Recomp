# Keyboard layout: the best default for WWE All Stars

Date 2026-10-25. Question: which default keys should the keyboard ship with
(keyboard spec, `.scratch/keyboard/spec.md`; GitHub #6/#8)? Research only:
nothing in the code or settings changed. Sources: the game's own localization
(`assets/Game/Localization/Coalesced.int`, from the disc), Prima's *WWE All
Stars Official Game Guide* (eGuide, 2011, archive.org item
`wwe-all-stars-official-game-guide`; the controller chart is on printed pages
23-27), the SDK and launcher source in this repo, the SVR2011 ports' source,
and 2K's WWE 2K15 PC press release. Downloads went to the session scratchpad,
not the repo.

**[V]** marks a fact checked against a primary source (file:line, game string,
guide page, press release). **[I]** marks an inference.

## Answer

- **The game is a four-attack fighter.** X quick strike, Y strong strike,
  A quick grab, B strong grab; RT is held to run; LT is the context button (pin,
  climb, ring in/out, pick up an object); LB/RB counter and reverse, and LB + RB
  together is the finisher. Signatures are X + A or Y + B. The right stick only
  changes holds and targets; the D-pad only taunts. L3 and R3 do nothing in a
  match. [V] (§1)
- **No official keyboard layout exists to copy.** SmackDown vs. Raw 2007-2011
  never had a PC release: 2K calls WWE 2K15 (April 2015) the franchise's
  "debut on Windows PC" [V]. The SVR2011 ports use the SDK's defaults
  unchanged [V]. 2K's own PC keyboard defaults weren't found on a 2K page;
  only third-party lists exist (§2). [V]
- **The SDK defaults don't fit this game.** They put the four attacks on
  four separate fingers across the board (`;`, `'`, `L`, `P`), the counters on
  `1`/`3` and the D-pad on Shift + arrows. Holding Shift for a taunt silences
  every other key, WASD included. [V] (§3)
- **Recommended:** WASD to move, Space to run (held by the left thumb), F/E
  for the context button, a 2×2 attack block under the right index and middle
  fingers (J/K quick strike/grab, U/I strong strike/grab), the counters on L
  and `;` under the idle ring and little fingers, the right stick on O/P (plus
  9/0), and the D-pad on the arrow keys. Enter is A, Backspace is B, Tab is
  Start. Every chord the game asks for falls on two different fingers, and no
  modifier key is used. (§4, §5)
- **One owner check decides Enter.** Enter as A lets the player confirm menus
  with Enter. If the "PRESS START" title screen ignores A, Enter has to move
  to Start instead (§4 trade-offs).

## 1. What each input does

The chart is the guide's "Moves list and abbreviation chart", Xbox 360 column
(pages 23-27). The game's own strings confirm it: the `[DemoControls]` map
(`Coalesced.int:3505-3519`), the loading-screen tips `CT_*_xbox360_*`
(`:1394-1491`) and the in-match help boxes `*Help_Title` (`:3287-3361`). [V]

| Input | In a match | Pressed with | Source |
|---|---|---|---|
| Left stick | Walk, aim the Irish Whip; "Toward"/"Away" modify attacks (Toward + X, Away + X, Away + hold Y charge strike, Away + A instant waistlock, Away + LT search for an object); turn around on a turnbuckle | everything | guide p23-27; `CT_24` |
| X | Quick Strike; hold-attack in a lockup; turnbuckle/irish-whip strike | A (signature), RT (running), Toward/Away | guide p23-24; `XBOX_X` |
| Y | Strong Strike; **hold** for a Charge Strike | B (signature), RT, Away | guide p24; `ChargedMoveHelp_Title = PRESS AND HOLD [Y]` |
| A | Quick Grab (weak lockup); drop object; place ladder with LT | X (signature), RT, LT | guide p24, p26; `XBOX_A`; `PlaceLadderHelp_Title = [LTRIGGER] + [A]` |
| B | Strong Grab (front headlock); Tree of Woe; attack outside the ring with RT | Y (signature), RT | guide p24, p27; `AttackOutsideRingHelp_Title` |
| X + A, Y + B | Signature move (also from the turnbuckle, on a table, running with RT) | pressed together | `XBOX_XACombo`, `XBOX_YBCombo`, `ExtremeRunMoveHelp_Title = [RTRIGGER] + [X] + [A] or [Y] + [B]` |
| RT | **Hold** to run (rope run, turnbuckle run, dash with an object); tap for an Irish Whip from a lockup | left stick, any face button, LB/RB (running counter), LT (cancel pin) | guide p23-26; `CT_18`, `CT_23` |
| LT | Pin; climb or leave a turnbuckle/cage/ladder; enter or exit the ring; pick up an object; Away + LT searches under the apron | Away, RT, A | guide p25-26; `XBOX_LT1-4`; `CT_16`, `CT_20`, `CT_21`, `CT_29`, `CT_40` |
| LB | Grab counter; reversal; aerial counter; Irish Whip combo | RB (finisher), RT | guide p24-25, p27; `CT_30`, `CT_31` |
| RB | Strike counter; reversal; aerial counter; Irish Whip combo | LB, RT | same |
| LB + RB | **Hold** for the finisher (also the Event Horizon finisher), tap to store it, tap to use a stored one; object counter | pressed together | guide p25-26; `CT_39`, `CT_47`, `CT_48`; `XBOX_RBLBCombo = FINISHER` |
| Right stick | Change hold in a lockup ("flick the right stick left or right"), rear waistlock from a weak lockup, switch target with 3+ wrestlers | after a grab | guide p24, p27; `Basic_ChangeHold_desc` (`:1668`); `LockUpTransitionHelp_Title = [RSTICK] + [LEFT] or [RIGHT]` |
| D-pad | Taunt, any direction | alone | guide p25; `XBOX_DPad = TAUNT` |
| L3, R3 | Not used in a match (in no chart row, tip or help box) | — | guide p23-27; `Coalesced.int` |
| Start | Pause (disabled in Xbox LIVE matches); "PRESS START" title | alone | `CT_12`; `PressStart_xbox360` (`:3401`) |
| Back | No match action found | — | [I] |
| Mash | Kick out of a pin, escape a submission, struggles: "any of the action buttons or move the left stick or right stick" | — | `Basic_KickoutOfPin_desc` (`:1659`); guide p25 |

The guide also names a "Block" button ("hold the Block button", wrestling
tactics p7 and many character pages) but never says which button it is; the
game strings don't mention blocking at all. [V] Which input blocks is open;
if it is a held bumper, the layout below already keeps both bumpers free to
hold. [I]

**In menus** the strings give SELECT, BACK, ACCEPT, CONTINUE and RESTORE
DEFAULTS (`[Common]`, `:4182-4196`), and Create a Superstar has "toggle
category left/right" and "rotate Superstar left/right" (`:1160-1163`). Which
buttons carry them is drawn by Scaleform glyphs, not in these strings. The
Xbox convention (A select, B back, LB/RB or the triggers for categories, the
right stick to rotate) is likely. [I]

**Use counts** (how often each input comes up in a match, from the chart and
the tips; [I]): left stick ≫ X, Y, A, B > LB/RB (every defensive exchange) >
RT (running and every Irish Whip) > LT (pins, ring exits, climbing) >
LB + RB, X + A, Y + B (a few per match) > right stick (in lockups) > D-pad
(taunts) > Start. Back, L3 and R3 matter only in menus or not at all.

## 2. Comparable PC layouts

- **SVR 2007-2011 had no official PC release.** 2K's release of 2015-04-28 is
  headed "WWE 2K Makes Franchise Debut on Windows PC with WWE 2K15" and calls
  it the "inaugural release" on PC (Take-Two IR, Business Wire,
  https://ir.take2games.com/static-files/d8e1f321-c50e-4fe8-b80f-a93402b05c43). [V]
  The THQ-era SVR games were console and handheld only, so the reporter's
  "svr 07,08,09,10,11 have keyboard full support" must mean fan ports or
  emulators (for 2011: the ReXGlue ports below). [I]
- **SVR2011-Recomp** (`D:\Projects\SVR2011-RECOMP`) keeps the SDK at
  `f5337cd` with the stock `keybind_*` defaults and `mnk_mode` off
  (`.tools/rexglue-source/src/input/mnk/mnk_input_driver.cpp:25-55`); its own
  source defines no layout. [V]
- **wwe-svr2011-pc** (KaikoClanworth1, read through `gh` at `5547be3`,
  2026-10-25; no local clone): its Controls page lists the SDK defaults
  unchanged and only labels them (`port/src/graphics_page.cpp:162-185`: A
  "ACTION" `Semicolon,Space`, X "STRIKE" `L`, LB "RUN" `1`, D-pad "TAUNT"
  `Shift+Up`…). Keyboard play was turned on in `6f64494` with no new keys. [V]
  So neither SVR port is evidence for a good layout; they inherited one. [I]
- **WWE 2K PC games.** No official 2K support page or manual with keyboard
  defaults was found (two searches; support.2k.com returned nothing). [V]
  Third-party lists (magicgameworld.com, unverified) agree on a pattern
  across 2K20-2K25: WASD to move, attacks on the J/K/L row, run on held Left
  Shift, Space as a modifier for specials in 2K25. [I] It is a pointer only,
  but it matches the fighting-game habit below.
- **Fighting games.** Capcom's SF6 PC manual didn't load (two tries,
  manual.capcom.com 404). [V] No official keyboard default was checked, so
  the only thing taken from fighting games is the general habit: left hand on
  WASD, attacks in a block under the right hand's home row (U I O / J K L).
  [I]

## 3. What our port can bind

- **Bind syntax.** Each `keybind_*` string is a comma-separated list of
  alternatives, each a key name with optional `Shift+`/`Ctrl+`/`Alt+`
  prefixes (`mnk_input_driver.cpp:32-55`, parser `:68-139`). Modifier matching
  is exact (`TokenPressed`, `:126-137`): `Up` is silent while Shift is held,
  and a bare `Shift` can never fire (`:63-67`). [V]
- **Every input is digital.** A bound stick direction gives full deflection
  (`INT16_MAX`) and a trigger gives `0xFF` (`:300-332`). [V] The game reads
  "Toward", "Away", "flick" and "hold" from that without trouble. [I]
- **Keys are physical positions.** The window reads SDL scancodes
  (`src/ui/window_sdl.cpp:511-512` → `TranslateSDLScancode`,
  `src/ui/sdl_virtual_key.cpp`), so `W` means "the key where QWERTY has W" on
  AZERTY and QWERTZ too: WASD is the same block of keys on every layout. [V]
  Both Shifts map to one `kShift`, and keypad Enter maps to `Return`
  (`sdl_virtual_key.cpp:41-43, 72-74`). [V] The ISO key left of Z has no name
  in the table (`src/ui/keybinds.cpp:22-144`), so it can't be bound. [V]
- **Key names** (`keybinds.cpp:22-144`): `A`-`Z`, `0`-`9`, `F1`-`F24`,
  `Minus`, `Plus` (the = key), `Comma`, `Period`, `Semicolon`, `Slash`,
  `Backslash`, `LBracket`, `RBracket`, `Quote`, `Backtick`, `Escape`, `Return`,
  `Space`, `Tab`, `Backspace`, `Delete`, `Insert`, `Home`, `End`, `PageUp`,
  `PageDown`, the arrows, `Numpad0`-`9`, `NumpadPlus/Minus/Star/Slash`,
  `CapsLock`, `NumLock`, `ScrollLock`, `Pause`, `PrintScreen`, and
  `LMB`/`RMB`/`MMB`. [V] Mouse buttons do bind, but the default leaves the mouse
  alone (`mnk_mouse` false, `:26`). [V]
- **Reserved keys.** The Keys page refuses F3, F4, F6, F7, F12, Backtick and
  Escape with any modifiers, and Enter with Alt
  (`launcher/src/Launcher.Core/KeyBinds.cs:70-89`). Alt+Enter switches
  fullscreen in the app (`src/ui/rex_app.cpp:511-519`). [V]
- **The recomp's own keys.** The PC SETTINGS page reads Up/Down/Left/Right,
  Enter (as A) and Escape (as B) straight from the keyboard
  (`src/settings/settings_page.cpp:105-120`) and opens on F4 or on both stick
  clicks (`src/wweallstars_app.h:189`). [V] A default that puts the D-pad on the
  arrows, A on Enter and B next to Escape behaves the same in the game and on
  that page. [I]
- **Where the defaults live.** The SDK's `REXCVAR_DEFINE_STRING` defaults
  (`mnk_input_driver.cpp:32-55`); `KeyBinds.Inputs` copies them
  (`KeyBinds.cs:15-42`) and the README "Keyboard" table describes them
  (`README.md:118-133`). The port already overrides an SDK default without
  patching the SDK: `kPortDefaults` in `src/settings/port_defaults.h:15-17`
  (`mnk_mode`). [V]
- **Side finding: the Keys page records layout keys, the game reads
  positions.** The page builds the name from Avalonia's `e.Key`
  (`launcher/src/Launcher.App/MainWindow.Keys.cs:111-130`), which follows the
  keyboard layout, while the game matches scancodes. On AZERTY, pressing the
  key labelled A stores `A`, which the game reads on the key labelled Q. [V]
  Avalonia 11's `KeyEventArgs.PhysicalKey` would fix it [I]; worth a ticket
  before AZERTY players rebind.

## 4. Recommended layout

```
 left hand                               right hand
 Tab=Start                               9 0      = right stick up/down
 Z=Back  E=LT                            U I O P  = Y  B  | rstick left/right
 W A S D = move                          J K L ;  = X  A  | LB RB
 F=LT    C=L3                            N=R3
 Space (thumb) = RT, held to run         arrows = D-pad    Enter=A  Backspace=B
```

| Input | Keys | Why |
|---|---|---|
| Left stick up / down / left / right | `W` / `S` / `A` / `D` | PC standard; the most used input stays under the resting left hand |
| L3 | `C` | Unused in a match; only half of the recomp's both-sticks combo (F4 does the same) |
| RT (run, Irish Whip) | `Space` | Held while moving and attacking, so it goes to the one free digit, the left thumb. Shift is the PC run key but can't be used (exact modifiers) |
| LT (pin, climb, ring, object) | `F`, `E` | The PC "interact" keys for the game's context button; two keys so Away + LT always has a free finger (index on F, middle on E) |
| X (quick strike) | `J` | Right index on the home row: the most used attack |
| A (quick grab) | `K`, `Return` | Next to X, so the X + A signature is two neighbouring fingers. Enter confirms menus, as on the PC SETTINGS page |
| Y (strong strike) | `U` | Above X: strikes in the left column, grabs in the right, quick on the home row, strong above |
| B (strong grab) | `I`, `Backspace` | Above A, so Y + B is the same two fingers one row up. Backspace is the PC "go back" key, Escape being reserved |
| LB (grab counter) | `L` | Home row under the right ring finger, which no attack uses: counters never wait on a moving finger. Left of RB, like the bumpers |
| RB (strike counter) | `Semicolon` | Little finger beside it; LB + RB (finisher, hold or tap) is two neighbouring fingers |
| Right stick left / right | `O` / `P` | The two flicks the game asks for (change hold), right above the counters, reachable without leaving the attack block |
| Right stick up / down | `9` / `0` | Rarely needed (kick-outs take any button); kept near O/P |
| R3 | `N` | Unused in a match; the other half of the settings combo |
| D-pad up / down / left / right | `Up` / `Down` / `Left` / `Right` | Taunts are deliberate, so the right hand can leave the block; in menus the arrows act as the D-pad, as on the PC SETTINGS page |
| Start | `Tab` | Pause; left little finger, away from the attack block so it isn't hit by accident |
| Back | `Z` | No match action found; a free key by the left hand |

Every chord the game asks for, checked against fingers:

- X + A = J + K and Y + B = U + I: index + middle, the same shape on two rows.
- RT + attack (running attacks, running signature RT + X + A) = Space + J/U/K/I:
  thumb + right hand.
- Hold RT + direction = Space + WASD; hold RT + RB/LB (running counter) =
  Space + `;`/L.
- LB + RB (finisher) = L + `;`: ring + little, held or tapped.
- Away + hold Y (charge strike) = A/D + U; Away + LT = A/D + F or E.
- LT + RT (cancel pin) = F + Space; LT + A (place ladder) = F + K.
- No input needs Shift, Ctrl or Alt, so nothing silences anything else. [V]
  (§3)

**Layouts.** All 24 defaults are letters, digits, Space, Tab, Enter, Backspace
and arrows, and they bind by position (§3), so WASD and the J/K/U/I block sit
in the same place on QWERTY, AZERTY and QWERTZ. [V] Only the labels differ:
on AZERTY the keys are labelled Z Q S D (move), and on QWERTZ the counter
pair is L and Ö. The names in the table (and on the Keys page) are QWERTY
names. [V]

**Trade-offs.**

- **Enter is A, not Start.** Menus confirm with Enter, which most players will
  try first. If the "PRESS START" title screen accepts only Start, a new
  player is stuck there until they try Tab. Xbox title screens usually accept
  A as well [I], but it is unchecked: the owner check is pressing Enter at the
  title screen. If it fails, use `keybind_a = "K"` and
  `keybind_start = "Return,Tab"`.
- **The right stick sits on the top row, not on a cross.** Its two
  meaningful flicks are O and P; up and down are on 9 and 0, which is odd
  geometry but rarely used. Arrows went to the D-pad instead because menus and
  the PC SETTINGS page already use them that way.
- **Taunts make the right hand leave the attack block.** Acceptable: taunts
  are never timed.
- **Space for running is not 2K's Shift**, but Shift can't work here (§3).
- **The Block button is unknown** (§1). If it turns out to be a held bumper
  or trigger, it is already on a free finger.
- **Laptops and TKL boards** have every key used; none is on the numpad or
  the Home/End block.

**Alternative (close second): counters on the left hand.** LB `Q`, RB `E`
(finisher Q + E, like the bumpers either side of the stick), LT `F` only,
right stick on L/`;`/O/Period. It mirrors the pad more literally, but the
middle finger holds both W and E, so countering while walking forward (or a
running counter forward) needs one finger on two keys. The recommended
layout keeps defence off the movement hand, which is why it wins. [I]

## 5. The change

The values for `kPortDefaults` (`src/settings/port_defaults.h`), in the SDK's
syntax, with the same values in `KeyBinds.Inputs` and the README table:

```toml
keybind_a = "K,Return"
keybind_b = "I,Backspace"
keybind_x = "J"
keybind_y = "U"
keybind_left_shoulder = "L"
keybind_right_shoulder = "Semicolon"
keybind_left_trigger = "F,E"
keybind_right_trigger = "Space"
keybind_lstick_up = "W"
keybind_lstick_down = "S"
keybind_lstick_left = "A"
keybind_lstick_right = "D"
keybind_lstick_press = "C"
keybind_rstick_up = "9"
keybind_rstick_down = "0"
keybind_rstick_left = "O"
keybind_rstick_right = "P"
keybind_rstick_press = "N"
keybind_dpad_up = "Up"
keybind_dpad_down = "Down"
keybind_dpad_left = "Left"
keybind_dpad_right = "Right"
keybind_start = "Tab"
keybind_back = "Z"
```

No value repeats a key, none is reserved (`KeyBinds.Reserved`), and none uses
a modifier. `keybind_guide` stays empty (SDK default). An owner who already
saved `keybind_*` lines keeps them: port defaults only change values still at
their default (`port_defaults.h:21-27`). [V]

## Sources

- Game strings: `assets/Game/Localization/Coalesced.int` (sections
  `Controls_Xbox360`, loading tips `CT_*_xbox360_*`, help boxes, `[DemoControls]`,
  `[Common]`). [V]
- Prima Games, *WWE All Stars Official Game Guide* (eGuide), 2011, pages 7
  and 23-27: https://archive.org/details/wwe-all-stars-official-game-guide [V]
- Take-Two/2K press release, 2015-04-28, "WWE 2K Makes Franchise Debut on
  Windows PC with WWE 2K15". [V]
- KaikoClanworth1/wwe-svr2011-pc `port/src/graphics_page.cpp` at `5547be3`;
  commit `6f64494`. [V]
- Not found: THQ's Xbox 360 manual PDF (RegVault lists one behind a paywall,
  no archive copy), an official 2K PC keyboard page, Capcom's SF6 PC manual.
