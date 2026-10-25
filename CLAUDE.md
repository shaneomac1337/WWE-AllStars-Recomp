# WWE All Stars Recomp

A static recompilation of the Xbox 360 game WWE All Stars, built on ReXGlue
v0.10.0. It follows the layout of SVR2011-Recomp
(`D:\Projects\SVR2011-RECOMP`). Plan and milestones: `docs/ROADMAP.md`.
Decisions: `docs/adr/`.

## Hard rules

- Never modify the ISO. Game data (`assets/`), generated code (`generated/default`),
  converted shaders, logs (`analysis/`) and the decrypted image never enter Git.
- Codegen runs in strict mode only. Add a missing function as a seed in
  `wweallstars_manifest.toml`, with a comment giving the instruction-level
  evidence. Never stub it or force generation.
- The build checks the XEX SHA-256 (`521c2325…6892`, base v5) and the Title
  Update 2 patch `assets/default.xexp` (`a6a45704…ed6a`). Codegen applies TU2;
  every address in the manifest and `src/` is a TU2 address (ADR-0022).
- Run PowerShell scripts with `pwsh` (PowerShell 7). Windows PowerShell 5 lacks
  `Get-FileHash` here.

## Commands

```
pwsh ./scripts/build.ps1 -Action Codegen      # regenerate C++ from the XEX
pwsh ./scripts/build.ps1 -Action Configure    # Vulkan, source-tree SDK (patches applied)
pwsh ./scripts/build.ps1 -Action Build -Jobs 20
pwsh ./scripts/build.ps1 -Action Launcher      # launcher (Native AOT) -> out/launcher/publish
pwsh ./scripts/build.ps1 -Action LauncherTest  # launcher xUnit tests (pinned .NET SDK)
pwsh ./scripts/build.ps1 -Action Image        # TU2 memory image -> analysis/xex/tu2.pe
python scripts/disasm.py <hex addr> [count]   # disassemble the TU2 image, VMX128 included
python scripts/tu_addrmap.py <paths>          # map base v5 addresses to TU2 (report only)
python scripts/inspect_disc.py <iso> --extract-all assets
```

## Layout

- `.tools/`: llvm, the rexglue prebuilt package, xenosrecomp and fidelityfx are
  junctions to SVR2011's `.tools`. `rexglue-source` is a separate copy at
  `f5337cdc` with this repo's `patches/` applied.
  `.tools/xenia-canary/` holds the reference emulator (binary only, no source).
  `.tools/dotnet-sdk/` is the launcher's .NET SDK, pinned by `launcher/global.json`.
- `launcher/`: the Avalonia launcher (ADR-0018): `Launcher.Core` (UI-free, tested),
  `Launcher.App` (views), `tests/Launcher.Tests` (xUnit).
- `art/icon/`: the original icon of both exes; `make_icon.py` is the source and
  writes the `.ico`, `.svg` and 64 px `.png`. Never use game art there. A
  `metadata/icons` folder (game art) fails the build (`cmake/no_game_art.cmake`).
- `analysis/xex/tu2.pe`: the TU2 memory image (`build.ps1 -Action Image`), read by
  `disasm.py` and the analysis scripts. `analysis/xex/default.pe` is the base v5
  image, kept as `tu_addrmap.py` input (file offset = address − 0x82000000).
- `docs/research/`: TU research and the Xenia baseline.

## Reverse engineering

- Ghidra (the `mcp__ghidra__*` MCP, Ghidra 12.1.4 at `D:\tools\ghidra-mcp`) holds
  TU2 as program `default.xexp` in project `ghidra/WWEAllStarsTU2`: Xenon360
  extension, language `PowerPC:BE:64:Xenon-VMX128-32addr`, fully auto-analysed,
  kernel imports named. Open it with `project_program_open_existing` (location
  `D:\Projects\WWE_ALLSTARS-RECOMP\ghidra`). `ghidra/` is gitignored.
  `ghidra/WWEAllStars` is the base v5 image at base addresses; use it only for
  base-to-TU2 work.
- Ghidra is the first tool for every RE question; `disasm.py` only supplements it
  (VMX128 packs). A Ghidra project opens in one process at a time, so every session
  uses one shared server: the user-scope `ghidra` MCP is
  `D:\tools\ghidra-mcp\ghidra_mcp_bridge.py`, a stdio bridge to one long-lived
  `ghidra-headless-mcp --transport tcp` on `127.0.0.1:8765`, started on first use. It
  opens each project once and gives each session its own program session on it
  (the installed `.venv\Lib\site-packages\ghidra_headless_mcp\backend.py` carries a
  local patch that serialises project open and close, `backend.py.orig` beside it is
  the original; reapply it after reinstalling the server). Stop it with `D:\tools\ghidra-mcp\stop-shared-ghidra.ps1`.
- A `LockException` means some process other than the shared server holds the
  project: an old per-session `ghidra-headless-mcp.exe` (started before the bridge),
  or the Ghidra GUI. It is a blocker, never a reason to fall back to `disasm.py`.
  Find the holder (the `python.exe` child of a `ghidra-headless-mcp.exe` with a few
  hundred MB) and ask the owner before stopping it or its session.
- Xenon360 leaves the VMX128 pack instructions (`vpk*128`) undecoded; a
  decompile with a hole there gets read through `disasm.py`, which decodes them.
- Headless Ghidra (`support/analyzeHeadless.bat`) needs `GHIDRA_HEADLESS_MAXMEM=10G`;
  the 2 GB default runs out of heap in analysis.
- The MCP's `search_bytes` finds nothing, even bytes that are there. Scan
  `analysis/xex/tu2.pe` directly instead (file offset = address − 0x82000000).
- Tools still missing, ranked by payoff: `docs/research/tooling-recommendations.md`.

## Known game facts

- TU2: entry point `0x8241A7E0`, image base `0x82000000`, size `0x18C0000`,
  `.text` 0x82410000–0x83504018 (base v5: entry `0x8241A8E0`, `.text` end 0x83500DB0).
- Xenia reports a stuck audio loop. Canary's patch writes `li r3,1` at
  `0x833A6C98` in the base XEX, which is `0x833A9B50` in TU2 (same code).

## Agent skills

### Issue tracker

Local markdown under `.scratch/<feature>/`, committed to `main`. The public GitHub history is built without it (`scripts/publish-tree.ps1`, ADR-0023). See `docs/agents/issue-tracker.md`, also before writing a spec or tickets (what the player sees, how much game testing a ticket plans).

### Releasing

Version bump, package, public push and GitHub release, in order: `docs/agents/release.md`.

### Performance A/B

Comparing builds or releases for speed or frame pacing: `docs/agents/ab-match.md`.

### Triage labels

The five default roles (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`) as `Status:` lines. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` + `docs/adr/`. See `docs/agents/domain.md`.
