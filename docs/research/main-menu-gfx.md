# Main menu (GFx) and an EXIT TO DESKTOP row

Ticket: `.scratch/gpu-robustness/issues/06-research-main-menu-rows.md`.
Date 2026-10-16. Addresses are TU2. Native evidence is from Ghidra
(`ghidra/WWEAllStarsTU2`, program `default.xexp`). Movie offsets are from the
throwaway SWF tag parser and AS2 disassembler used for
`options-menu-gfx.md` (session scratchpad, not committed), plus a script that
builds the patch below and checks it. No game data is in this repo.

## Answer

- The main menu is `fe_main` in `UI_SHELL.xxx`. Its rows are hard-coded in the
  movie's ActionScript, like `fe_options`. Frame 1 builds `menuContents` as
  an array literal of 7 keys. A **dormant 8th row** follows:
  `DownloadableContent_xbox360`. The script appends it only when
  `GetVar("DLC", "DLCMenuItem") == "on"`, and no config entry sets that key.
  The row's icon (`DownloadableContent`), its labels in `Coalesced.int` and
  its A branch in `EnterMainMenuOption` all exist already.
- A selection never reaches native code as a row id. A calls
  `EnterMainMenuOption(sel)`. That function plays `SFX_UI_Menu_Select`
  (FSCommand `Call_Kismet`), returns on a locked row, and then matches the
  key against a fixed set. Unknown keys fall through to the default branch,
  `LoadSubMenu(menuTargets[sel])`. `menuTargets` has only 7 entries, so a
  new key would load `undefined`. A brand-new key is therefore unsafe. The DLC
  key is safe, because it has its own branch.
- The DLC branch sends one FSCommand
  (`online_ExecCmd`, `system=showMarketplace`) and then joins the common tail,
  which stays on `fe_main`. That 51-byte `getURL` is the place to hook.
- **A confirm dialog to reuse exists.** It is
  `_global.IssueQuestion(sType, sTitle, sBody, sAnswer1, cbAnswered1,
  sAnswer2, cbAnswered2)` in `interface_Loader` (`UI_LOADER.xxx`). `fe_main`
  already uses it for its Path of Champions "invalid record" yes/no
  (`ShowPOCInvalidRecordPopup`, layer `"GAME"`). A calls `cbAnswered1`. B
  calls `cbAnswered2`. Both then kill the popup. The texts are plain strings,
  so they can come from `Localize`, which the port already hooks.
- **Verdict: a safe hook exists.** It needs no new hook sites, only the three
  that `options_hooks.cpp` already uses:
  1. In the `GASActionBufferData::Read` hook (`0x83134B60`), patch `fe_main`'s
     frame-1 action buffer:
     - a 2-byte change that always adds the DLC row;
     - a length-preserving change of the DLC branch to `WWEExitQuestion()`;
     - 309 appended bytes that define `WWEExitQuestion`, which calls
       `IssueQuestion`. The yes callback sends
       `FSCommand:WWE_ExitToDesktop`.
     The buffer is grown with the game's own GFx array resize (`0x83102D20`).
  2. In the `Localize` hook (`0x82600608`), answer the row label, its
     description and the question's title and body. Section is `MainMenu`,
     package is `Screens`.
  3. In the FSCommand hook (`0x825CB4F0`), when the command is
     `WWE_ExitToDesktop`, close the game the way the window's close button
     does. Do not pass this command to the original.
- Unlike the OPTIONS row, this design needs no cursor tracking. The yes answer
  has its own command name, and no other movie sends it.

## How the movie is stored

- `fe_main` sits at `UI_SHELL.xxx` file offset `0x2D263` and is `0x2D9D`
  bytes long. It is a `GFX\x08` stream, not compressed, with the ExporterInfo
  name `fe_main`. The same package also holds `fe_options` at `0x30024`.
- Its frame-1 `DoAction` tag body starts at movie offset `0x39` and is **8556
  bytes** long. The last byte is End. `GASActionBufferData::Read` copies the
  records byte for byte up to and including End. So `count == 8556` at
  `this+0x14` identifies the buffer, the same way `count == 4587` identifies
  `fe_options`.
- Buffer offset → movie offset: +`0x39`. Movie offset → `UI_SHELL.xxx` file
  offset: +`0x2D263`.

## fe_main ActionScript (frame 1 DoAction, 8556 bytes)

Offsets are buffer offsets.

| Buf off | What it does |
|---|---|
| `0x0A19` | `function Localize(StringID, Section, Package)`, the same helper as in `fe_options` (`ExternalInterface.call("Localize", Section, StringID, Package)`). |
| `0x0E32` / `0x0F4A` | `Up()` / `Down()`: they move the cursor, then set `descTxt.text = Localize(menuLocked[i] ? "FeatureUnavailable" : menuContents[i] + "_Desc", "MainMenu")` and move the scroll bar to `floor(i/(len-1)*100)+1`. |
| `0x1064` | `EnterMainMenuOption(option)` (body to about `0x12D4`). First it runs `_root.playAudio("SFX_UI_Menu_Select")`. If `menuLogic.isItemLocked(option)`, it plays `SFX_UI_Menu_Error` and returns. Otherwise `r0 = menuContents[option]` and it matches the key against `"Online_"+pf`, `PathOfChampions`, `"DownloadableContent_"+pf`, `Create` and `FantasyWarfare`. The default branch (`0x1299`) runs `LoadSubMenu(menuTargets[sel])`. |
| `0x11A4` | **DLC branch:** `getURL("FSCommand:online_ExecCmd", "system=showMarketplace")`, 51 bytes, then `Jump -> 0x12D4`. |
| `0x118D` | PoC branch, used as a model: `this.ShowPOCInvalidRecordPopup()` is `push 0, r1, "ShowPOCInvalidRecordPopup"; callMethod; pop`. |
| `0x12D4` | Common tail for every branch: `_global.Localize = Localize`, ExternalInterface `ResetGameMode`, `EnableSecondaryInput(false)`, `Footer.Raise()`, Back/Accept prompts. Online (popup) and PoC-invalid (popup) also run it and stay on `fe_main`, so it is safe to stay on the screen after it. |
| `0x14BE` | `menuLocalizationGroup = "MainMenu"` |
| `0x14C6` | `menuContents = new Array("Exhibition", "Online_"+pf, "PathOfChampions", "FantasyWarfare", "Create", "Options", "Extras_"+pf)` (`pf = _global.platformSwitch`, set to `"xbox360"` at `0x146C`). |
| `0x14FC` | `menuIcons`: the same 7 keys plus `"DownloadableContent"` (8 entries). |
| `0x1527` | `menuTargets`: 7 paths (fe_exhibition, online_main, st_newstory, fw_fantasywarfare, fe_create, fe_options, fe_extras). |
| `0x1543` | `menuLocked = [false, false, false, false, bIsCAPLocked, false, false]` |
| `0x158C`–`0x15CD` | `if (_global.GetVar("DLC", "DLCMenuItem") == "on") menuContents.push("DownloadableContent_" + pf)`. The test is `Not` at `0x15A7`, then `If -> 0x15CE` at `0x15A8` (bytes `12 9D 02 00 21 00`). |
| `0x1626` | "finished" callback: header `Localize("HeaderName")`, the description for the current row, then for each row `item<i>.Initialize(Localize(key))` and `SetIcon(menuIcons[i])`, or `lockItem` + `SetIcon("Locked")`. |
| `0x1835` | init callback: `InitializeRows(menuContents.length)`, `pxGutter=-65`, `nScrollTolleranceDown=5`, then `SetContent(DynamicMenuItem.swf, i)` for each row. |
| `0x1951` | `onKeyDown`: Up/Down; B → `LoadMovie(fe_start.swf)`; A → `EnterMainMenuOption(getCurrentSelection())`. |
| `0x2037` | `ShowPOCInvalidRecordPopup`: `_global.IssueQuestion("GAME", Localize(Error title), Localize(Error body), Localize("Accept","Common","SystemMessages"), fnAccept, Localize("Cancel",…), fnCancel)`. |
| `0x216B` | End of the frame-1 script (top level). No branch targets it. |

- The row count follows `menuContents.length` everywhere: the init callback,
  the finished callback, the scroll bar and the Runbot `InformMenuContents`
  string. No 7 is hard-coded.
- `menuLocked[7]` is `undefined`, so the 8th row is never locked, and its
  description key is `DownloadableContent_xbox360_Desc`.
- `Coalesced.int` `[MainMenu]` has `DownloadableContent_xbox360 =
  DOWNLOADABLE CONTENT` and `DownloadableContent_xbox360_Desc`. It also has a
  leftover `Exit = EXIT DEMO` / `Exit_Desc`. No movie code uses that key, and
  the Icon sprite has no `Exit` frame.
- The Icon sprite in `fe_templates` `DynamicMenuItem` (sprite 105) has a
  `DownloadableContent` frame label, so the 8th row shows the DLC icon. No
  label suits "exit". The other choices are `Locked` and the first frame.
- `showMarketplace` is also sent by `online_main` (`UI_ONLINE.xxx`, two
  sites), so the port must not hook that command. The patch removes it from
  `fe_main` and adds a command of its own instead.

## The confirm: `_global.IssueQuestion` (interface_Loader)

`UI_LOADER.xxx` `interface_Loader`, buffer offset `0x49F8`, size 665:

- It plays `SFX_UI_Confirm_Build_On` and builds a popup from
  `PopupContents_Text.swf`: `Title.text = sTitle`, `Body.text = sBody`,
  button 1 = A icon + `sAnswer1`, button 2 = B icon + `sAnswer2`. Then
  `popupManager.CreatePopup(…, sType)`.
- Its key handler (`0x4A80`): on A it calls `cbAnswered1()`, plays
  `SFX_UI_Confirm_Build_Off` and kills the popup. On B it does the same with
  `cbAnswered2()`. Calling an `undefined` callback in AS2 does nothing, so B
  needs no callback.
- The popup has the game's font, frame, button icons and sounds, so it
  matches spec story 21 better than a port-drawn dialog would. The port does
  not need to draw its own.

## Native side (TU2)

| Address | Role | Evidence |
|---|---|---|
| `0x83134B60` | `GASActionBufferData::Read(this, stream)` | Decompile: per byte it calls `0x83102D20(this+0x10, count+1)` and stores the byte at `data[count-1]`. It stops after End. Already hooked (`options_hooks.cpp`). |
| `0x83102D20` | GFx `ArrayLH<UByte>::Resize(array, n)` | Array is `{data, size, capacity|flags}`. It sets `size = n` and reallocates through `0x83101808` with growth `n + n/4` when `n > capacity`. Called by `Read` above. It is in `generated/default` (`wweallstars_recomp.18.cpp`), so no seed is needed. |
| `0x825C5560` | `GFxExternalInterface` callback (base v5 `0x825C5328`) | The `GetVar` branch (`0x825C5758`) calls `GConfig->vtbl[0x1C](Section=args[0], Key=args[1], out, file)` (`*0x8370148C`, file `0x8370B540`). On a miss it returns `"***N/A***"` (`0x8208DB90`). `DLCMenuItem` appears in no config, so the DLC row stays hidden. |
| `0x82600608` | UE3 `Localize` | Already hooked. `fe_main` asks `Localize("MainMenu", key, "Screens")`. |
| `0x825CB4F0` | `GFxFSCommandHandler::Callback(this, movieView, cmd, args)` | Already hooked. `getURL("FSCommand:X", "")` arrives as `cmd="X"`, `args=""`, in the same action run (`options-menu-gfx.md`). |
| `0x82C179CC` | Native `showMarketplace` handler (string `0x8223F5B0`) | No longer reached from `fe_main` after the patch. |

## The movie patch

All three edits are made to the copy that GFx parses, in the `Read` hook,
when `count == 8556` and the bytes below match. The checks were run on the
patched buffer: re-disassembly is clean, all 72 `If`/`Jump` targets still land
on instruction starts, and no branch targets the old End. No function body
spans an edited region except `EnterMainMenuOption`, where the DLC-branch edit
keeps the length.

1. **Always add the DLC row.** At buffer `0x15A7`, check
   `12 9D 02 00 21 00` and write `00 00` at `0x15AB` (file `0x2E847`).
   `If -> 0x15CE` becomes `If -> 0x15AD`, so the push runs in both cases.
   `GetVar` is still called and its answer is ignored.
2. **DLC branch → `WWEExitQuestion()`.** At buffer `0x11A4` (file
   `0x2E440`), check the 51-byte
   `83 30 00 "FSCommand:online_ExecCmd\0system=showMarketplace\0"` and write:

   ```
   96 16 00 07 00 00 00 00 00 57 57 45 45 78 69 74 51 75 65 73 74 69 6F 6E 00  push 0, "WWEExitQuestion"
   3D 17                                                                       callFunction; pop
   96 14 00 00 78 78 78 78 78 78 78 78 78 78 78 78 78 78 78 78 78 78 00 17     push "x"*18; pop (filler)
   ```

   This is 51 bytes. The `Jump -> 0x12D4` after it is unchanged.
3. **Append the question.** Resize the array to 8865 (`0x83102D20(this+0x10,
   8865)`), re-read the data pointer, and write these 309 bytes at
   `0x216B`, then End (`00`) at `0x22A0`:

   ```
   function WWEExitQuestion() {               // DefineFunction, body 286 bytes
     _global.IssueQuestion("GAME",
       Localize("ExitToDesktop_Title", "MainMenu"),
       Localize("ExitToDesktop_Body", "MainMenu"),
       Localize("Accept", "Common", "SystemMessages"),
       function () { getURL("FSCommand:WWE_ExitToDesktop", ""); },
       Localize("Cancel", "Common", "SystemMessages"),
       undefined);
   }
   ```

   Hex, as the check script emits it:

   ```
   9b1400575745457869745175657374696f6e0000001e01963000030053797374656d4d6573
   73616765730000436f6d6d6f6e000043616e63656c000703000000004c6f63616c697a6500
   3d9b05000000002000831d004653436f6d6d616e643a5757455f45786974546f4465736b74
   6f700000962f000053797374656d4d657373616765730000436f6d6d6f6e00004163636570
   74000703000000004c6f63616c697a65003d962d00004d61696e4d656e7500004578697454
   6f4465736b746f705f426f6479000702000000004c6f63616c697a65003d962e00004d6169
   6e4d656e75000045786974546f4465736b746f705f5469746c65000702000000004c6f6361
   6c697a65003d9614000047414d45000707000000005f676c6f62616c001c960f0000497373
   75655175657374696f6e005217
   ```

   Strings are inline (push type 0), so the constant pool is unchanged. A
   named `DefineFunction` at the top level defines `WWEExitQuestion` on the
   `fe_main` timeline when frame 1 runs, long before any A press. Names
   resolve the same way as `Localize` does when `Up()` calls it. Answer
   labels are the game's own `SystemMessages/Common` `Accept`/`Cancel`, so
   they are localized.

## Plan for the implementation ticket

Follow the shape of `src/settings/options_row.h` + `options_hooks.cpp`:

1. **Pure part** (`src/…/exit_row.h`, tested in `tests/core` from a byte
   fixture, with no game data):
   - `bool PatchMainMenu(bytes in, size) -> patched bytes` checks the two
     original regions and returns the 8865-byte buffer.
   - `Text(section, key, package)` answers the four keys.
   - `OnCommand(cmd)` is true for `WWE_ExitToDesktop`.
2. **`Read` hook (`0x83134B60`)** gets a second case. When `count == 8556`
   and the pure check passes, call `sub_83102D20` with `r3 = this+0x10`,
   `r4 = 8865`. Then copy the patched bytes to the new `data` and log once.
   The `fe_options` case stays as it is.
3. **`Localize` hook (`0x82600608`)**. For Section `MainMenu` and Package
   `Screens` it answers:
   - `DownloadableContent_xbox360` → `EXIT TO DESKTOP`;
   - `DownloadableContent_xbox360_Desc` → "Quit WWE All Stars and return
     to the desktop.";
   - `ExitToDesktop_Title` → `EXIT TO DESKTOP`;
   - `ExitToDesktop_Body` → "Quit the game? Progress that has not been
     saved will be lost."

   It builds the result the same way as for PC SETTINGS. If
   the platform key ever differs (`_win32`/`_ps3`), match the
   `DownloadableContent_` prefix.
4. **FSCommand hook (`0x825CB4F0`)**: on `cmd == "WWE_ExitToDesktop"`, post
   the same quit request as the window's close button, from this guest UI
   thread to the host. Return without calling the original. Every other
   command goes to the original as now.
5. Checks for the session:
   - the main menu shows 8 rows, EXIT TO DESKTOP last, inside the divider or
     scrolling cleanly;
   - its description line;
   - A shows the game's question popup;
   - B closes the popup and leaves the menu as it was;
   - A on Accept closes the game with exit code "closed";
   - the other 7 rows still open their screens;
   - after returning from a submenu, 8 rows are still there.

## Candidate approaches, ranked

1. **Recommended: the dormant DLC row, plus the appended `IssueQuestion`
   call (above).** It uses the game's own row, icon slot, branch and popup,
   and the three hook sites the port already has. The one new mechanism is
   growing the action buffer with the game's own resize.
2. **No buffer growth: call `ShowPOCInvalidRecordPopup` from the DLC branch
   (17 bytes) and retext it in `Localize`.** Rejected. Its Accept callback
   calls ExternalInterface `CleanInvalidStoryRecords` (it deletes story
   records unless swallowed) and then `OpenPathOfChampions`, which loads a
   story screen while the game is closing. A stale "pending" flag would also
   retext the real PoC prompt.
3. **A brand-new key (not DLC) in `menuContents`.** Rejected. It falls into
   the default branch, which calls `LoadSubMenu(menuTargets[7])` =
   `undefined`. `menuTargets` would have to grow too, and the default branch
   would still unload `fe_main`.
4. **Answer `GetVar("DLC", "DLCMenuItem")` with `"on"` natively instead of
   edit 1.** This works, but it needs a fourth hook, on the ExternalInterface
   callback `0x825C5560`. The 2-byte edit sits in a buffer that is patched
   anyway.
5. **Port-drawn confirm (ImGui, settings-page look).** Fallback only, in case
   the appended code fails at runtime. In that case keep edits 1–2, make the
   DLC branch send `FSCommand:WWE_ExitToDesktop` directly (it fits in the
   51 bytes), and draw the yes/no natively.

## Open points, to be checked at runtime

- Whether 8 rows fit inside the lower divider or the list scrolls
  (`nScrollTolleranceDown = 5`). The DLC row was designed as an 8th row, so
  the layout should support it.
- That GFx runs the grown buffer to its new End. `Read` is the only place
  that sets the count, and later readers use the same array (`0x83132A78`
  in base v5), but this was not run.
- The row shows the DLC (shopping) icon. Changing it is a 1-byte pool index
  in the `menuIcons` push, but no icon label means "exit".
