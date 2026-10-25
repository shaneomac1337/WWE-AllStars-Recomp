# Ghidra and tooling review, October 2026

Date: 2026-10-24. Question from the owner: is our Ghidra setup good, are
there newer Ghidra releases or modules that would help a lot, and what other
tools should the project have? This note follows
`tooling-recommendations.md` (2026-10-11). It reports what changed since that
note and does not repeat its findings.

Local state was read on 2026-10-24, read-only. The Ghidra project was not
opened. Web facts come from primary sources read the same day: GitHub repos,
releases, commits and READMEs (through `gh api`), and Ghidra's own
`WhatsNew.md` and `ChangeHistory.md` at tag `Ghidra_12.2_build`.
**Unverified** marks anything not confirmed at the source. Nothing was
installed or run against the game.

## Verdict

**The setup is good, and current for the Ghidra line it runs on.** Ghidra
12.1.4 with Xenon360 0.7.0 is the right pairing. Xenon360 has had no release
since 0.7.0, and it builds only for 12.1.4. The TU2 program was imported
through Xenon360's own XEX/XEXP loader, not as a raw PE. One thing is new
upstream: **Ghidra 12.2 came out on 2026-10-22.** Do not move to it yet (§1).
No Ghidra module would help "massively". The gains left are small fixes, plus
one runtime tool that only just started working (Canary GPU traces).

Top actions, ranked by payoff:

| # | Action | Payoff | Cost | Verdict |
|---|---|---|---|---|
| 1 | **Update `ghidra-headless-mcp` to upstream `main`.** The local checkout is 8 commits behind. The missed fixes include one that releases the project lock after a failed open. Lock errors are this project's main Ghidra friction. | Fewer `LockException`s and broken sessions; byte search fixed | 15 min | Adopt now |
| 2 | **Add the 6 missing `vpk*128` constructors to Xenon360 locally and rebuild it for 12.1.4.** Report them upstream too. 71 TU2 functions use the forms that are missing. | Removes the main reason to leave Ghidra for `disasm.py` | ½ day | Try |
| 3 | **Update the reference Canary and build its GPU trace viewer and dump.** This became possible on 2026-10-15: trace replay was broken until then, and is now fixed. Capture is now scriptable through `trace_gpu_frames`. | Per-draw Xenos ground truth for the Native renderer and texture work | 1 day | Adopt (replaces old #7) |
| 4 | **Stay on Ghidra 12.1.4.** Plan the move to 12.2 for when Xenon360 ships a 12.2 build. 12.2 needs JDK 25 (the machine has 21), and no Xbox 360 extension supports it yet. | Avoids a broken setup | 0 | Hold |
| 5 | **Track rexglue's post-v0.10.0 codegen fixes with SVR2011** (atomic `stwcx.`, `sync`/`lwsync`/`eieio` fences). There is no new stable release. The one VMX pack bug fixed upstream does not affect this game (§4.1). | Correctness of guest atomics on x64 | none now | Watch |

Items from `tooling-recommendations.md` that are still open, with status
unchanged: TTD + `mcp-windbg` (#3), RenderDoc (#4), the AMD tool suite (#6),
Tracy 0.14 (#8), UModel (#9), and the XDK FIDB (#10). The Vulkan validation
item (#5) is solved differently: MSYS2 already ships the layer (memory note
`reference-vulkan-validation`). Version Tracking (§1.3 there) is still not
run, and its payoff has shrunk (§1.3 below).

## Already have / up to date (no action)

- **Ghidra 12.1.4**: newest 12.1.x, built 2026-09-21
  (`D:\tools\ghidra-mcp\ghidra_12.1.4_PUBLIC\Ghidra\application.properties`;
  [releases](https://github.com/NationalSecurityAgency/ghidra/releases)).
  12.1.2 is still on disk next to it.
- **Xenon360 0.7.0** (module `XenonVMX128`, language
  `PowerPC:BE:64:Xenon-VMX128-32addr`). It is installed in the Ghidra tree,
  not the user profile
  (`ghidra_12.1.4_PUBLIC\Ghidra\Extensions\XenonVMX128\XENON360_BUILD.json`:
  `loader_version 0.7.0`, `self_tests PASS`). It is the only release, from
  2026-09-30; the last commit is a README edit that same day
  ([repo](https://github.com/EveryoneEverybody/Xenon360/commits),
  [v0.7.0](https://github.com/EveryoneEverybody/Xenon360/releases/tag/v0.7.0)).
  The repo has no issues filed.
- **The TU2 import is the right one.** The program in
  `ghidra/WWEAllStarsTU2.rep` is named `default.xexp`
  (`idata/00/00000000.prp`). So it was loaded with Xenon360's XEXP loader,
  with the base XEX as **Base XEX**. That loader reconstructs the patch in
  memory and names xboxkrnl/xam ordinals (README "Install", "Supported
  formats"). This replaced the raw `tu2.pe` + `A2ALT` import described in
  `60fps-entrances.md` L13–17. The base project `ghidra/WWEAllStars`
  (`default_v5`) still exists.
- **XEXLoaderWV is not needed.** Upstream's latest is 12.1.3 (2026-09-20),
  with no 12.1.4 or 12.2 build
  ([releases](https://github.com/zeroKilo/XEXLoaderWV/releases)). The
  SaveEditors fork's PDB and `.pdata` fixes were merged upstream as
  [PR #33](https://github.com/zeroKilo/XEXLoaderWV/pull/33). It still hard-codes
  a stock language with no VMX128 (prior note §1.2). Xenon360 covers
  everything it does.
- **rexglue v0.10.0** is still the latest stable (2026-08-21). The newest
  nightly is `nightly-20261002-bd833a2a`. Upstream has been quiet since
  2026-10-02 ([releases](https://github.com/rexglue/rexglue-sdk/releases)).
- **`disasm.py` with the Xenon objdump `-M cell,vmx128`** remains the VMX128
  reference (prior note, status line).
- **JDK 21** (Temurin 21.0.12.1) matches Ghidra 12.1.4
  (`application.java.min=21`).
- **Ghidra MCP choice.** `ghidra-headless-mcp` is still a good fit (§3). Only
  the update in action #1 is needed.

---

## 1. Ghidra itself

### 1.1 Is 12.1.4 current?

It is the newest **12.1.x**. **Ghidra 12.2** was released on 2026-10-22
(`Ghidra_12.2_build`, asset `ghidra_12.2_PUBLIC_20261016.zip`;
[release](https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.2_build)).

**Why not upgrade now:**

- "Ghidra 12.2 requires, at minimum, JDK 25 to run"
  ([WhatsNew.md](https://github.com/NationalSecurityAgency/ghidra/blob/Ghidra_12.2_build/Ghidra/Configurations/Public_Release/src/global/docs/WhatsNew.md)).
  This machine has JDK 21 only (`java -version`; two Adoptium 21 JDKs on
  PATH).
- Xenon360's release says "**Requires Ghidra 12.1.4**", and its extension is
  stamped `version=12.1.4` (`XenonVMX128\extension.properties`). Ghidra
  refuses extensions built for another version. The source build would need a
  rebuild against 12.2. **Unverified** that it compiles there.
- Programs "created or modified in 12.2 may not be usable by an earlier
  Ghidra version" (WhatsNew.md). Opening `WWEAllStarsTU2` in 12.2 is one-way.
- The 12.2 PowerPC changes do not touch Xenon: "Added PowerPC 3.0B/3.0C
  instructions, corrected the signed offset for `LQ`" (WhatsNew.md, "Processor
  Module changes").

### 1.2 What 12.2 adds that matters here (for later)

From the 12.2 section of
[ChangeHistory.md](https://github.com/NationalSecurityAgency/ghidra/blob/Ghidra_12.2_build/Ghidra/Configurations/Public_Release/src/global/docs/ChangeHistory.md):

- **Function ID:** "Users can now configure whether a Function Id database
  can be applied to a binary with a compiler specification that does not
  match the database" (GP-4791). This matters if public XDK FIDBs (§2.3) are
  ever tried against the Xenon360 language. **Unverified**: whether FID also
  filters on language ID, which would still block them.
- **Structure/Union/Enum merger:** an interactive side-by-side merge of two
  partially recovered types (WhatsNew.md "Data Types"). Useful for UE3 object
  layouts that agents rebuild piecemeal in different sessions.
- **BSim:** "reconstruct a structure in one program from field use in
  another" (GP-6921). Also `dropdatabase`, and H2 scripts that skip programs
  already added (GP-6041).
- **Emulator:** an "Emulate Function" action and a `FunctionEmulationHarness`
  API (GP-6787). Limit: Xenon360 models many VMX128 ops as opaque userops
  (`define pcodeop xenon_vmulfp128`, `xenon_vperm128`…, 13 in the
  `xenon_vmx128_phase*.sinc` files, 289 in `xenon_altivec.sinc`). Emulating
  vector code would stop at those ops. Scalar timing and state code would
  work.
- **Timeless debugging (beta):** an Intel PIN tool that records TENET-format
  traces, plus a call tree, a breakpoint timeline and a variables view
  (WhatsNew.md). It traces the **x64 host**, so on the recomp it overlaps with
  WinDbg TTD (prior note §2.1). The PIN trace does not know the guest image.
  **Unverified** whether static mapping can line it up with the TU2 program.
- **Version Tracking:** a "Use Empty Composite Types" option (GP-6438). Minor.
- **Decompiler:** better recovery of offcut references to global symbols
  (GP-7038), and a "Lock display" toggle for very large functions (GP-6748).
- **Speed:** read/write locks on program database access, and compact object
  headers to cut memory use (WhatsNew.md "Speed and memory use"). That may
  help the 10 GB headless analysis. **Unverified** by how much.
- **PyGhidra 3.2.0** (GP-6664). The MCP server depends on `pyghidra>=3.0.2`
  (`ghidra-headless-mcp\pyproject.toml`).

**Verdict:** hold at 12.1.4. Upgrade once Xenon360 publishes a 12.2 build:
install JDK 25 side by side, and open a *copy* of `WWEAllStarsTU2` first.

### 1.3 Version Tracking base v5 → TU2: lower payoff than in October

The prior note ranked it #2, at a time when TU2 was not in Ghidra. Now TU2 is
the main program and agents annotate it directly. `default_v5` only holds
whatever markup was made before 2026-10-11, and every address in the repo is
TU2 (ADR-0022). Run VT only if that base program turns out to hold useful
names. **Unverified**: how much hand markup `default_v5` holds, because the
project was not opened.

---

## 2. Xbox 360 Ghidra extensions

### 2.1 VMX128 pack instructions: the real gap, measured

CLAUDE.md says Xenon360 "leaves the VMX128 pack instructions (`vpk*128`)
undecoded". The installed SLEIGH source shows a partial gap, not a total one:

| `vpk*128` form | Defined in Xenon360 0.7.0? | Uses in TU2 codegen |
|---|---|---|
| `vpkd3d128` | yes (`xenon_vmx128_phase3.sinc` L172, opaque pcodeop) | 65 |
| `vpkswss128` | yes (`phase4.sinc` L43) | 135 |
| `vpkuwus128` | yes (`phase6.sinc` L7) | 6 |
| `vpkshus128` | **no** | 348 |
| `vpkswus128` | **no** | 12 |
| `vpkuhus128` | **no** | 12 |
| `vpkshss128` | **no** | 1 |
| `vpkuhum128`, `vpkuwum128` | **no** | 0 |

The counts come from `// vpk…128` comments in `generated/default/*.cpp`.
**71 TU2 functions** contain at least one of the missing forms; 87 contain
any `vpk*128`. **Unverified**: whether the three defined forms decode
correctly on real TU2 code, because the project was not opened.

Options:

- **GhidraXenon** ([freeqaz/ghidra-xenon-extension](https://github.com/freeqaz/ghidra-xenon-extension))
  defines all nine `vpk*128` forms, with p-code (`data/languages/vmx128.sinc`
  L1435+). But it is a separate language (`PowerPC:BE:64:Xenon`), built for
  Ghidra 12.1. It has had no commit since 2026-02-12 and **no license**, so
  its SLEIGH cannot be copied into Xenon360. Switching languages would
  re-disassemble the whole program and drop the loader integration. **Skip.**
- **Write the six constructors ourselves** in a local Xenon360 build.
  Xenon360 is Apache-2.0, and its README documents
  `gradlew buildExtension -PGHIDRA_INSTALL_DIR=…`. Use the Xenon objdump and
  rexglue's decoder as the encoding reference: both already decode these
  forms, since codegen emits them. Check the result against `disasm.py` on
  the 71 functions. Then send the patch upstream as the repo's first issue or
  PR. Cost ½ day. Risk: a rebuilt `.sla` changes the language. Re-open a
  **copy** of the project first. **Try.**
- Leave it as is, and keep reading those 71 functions through `disasm.py`.
  That is acceptable: they are pack/colour/vertex helpers, not game logic.

### 2.2 XEX loaders

There is nothing newer than what is installed. Xenon360 (above) is the only
one with XEXP, a VMX128 language and 12.1.4 support. XEXLoaderWV: see
"Already have". Repo searches on GitHub for "xbox 360 ghidra", "ghidra
xenon", "vmx128 ghidra" and "xex ghidra" (2026-10-24) found only these two,
plus [murdoch3/Xbox360-Ghidra-Scripts](https://github.com/murdoch3/Xbox360-Ghidra-Scripts).
That repo is one hypervisor script (`17559_hv.py`) and is not relevant.

### 2.3 Library naming (FID, BSim, type libraries)

- **XDK FIDBs:** nothing new.
  [tomcl7/ghidra-fidb-xenonsdk](https://github.com/tomcl7/ghidra-fidb-xenonsdk)
  still lists 3529–9328 and 20764.6, with **no 11775**. The generator
  ([x360-fidb-generator](https://github.com/tomcl7/x360-fidb-generator), last
  commit 2025-06-26) needs **IDA Pro 7+** to convert the Xbox 360 COFF libs,
  plus the XDK setup exe (README "Requirements"). The project has neither.
  **Skip**, unless XDK 11775 libs and an IDA licence turn up.
- **BSim** (H2 file, no server) is still the realistic fuzzy route
  (`tooling.md` §6, Fable Anniversary symbol build). 12.2 improves its scripts
  (§1.2), but that is no reason to start now.
- **xboxkrnl/xam types:** Xenon360 names imports by ordinal for xam,
  xboxkrnl, xbdm, xnet, xapi and others (`XENON360_BUILD.json`
  `ordinal_modules`). It ships no kernel prototypes. No public Xbox 360 `.gdt`
  was found (**unverified** that none exists). The project already has the
  prototypes as C in the rexglue kernel sources. A one-off `type_parse_c` via
  the MCP could load the few that matter.

---

## 3. Ghidra MCP servers

**Local:** [mrphrazer/ghidra-headless-mcp](https://github.com/mrphrazer/ghidra-headless-mcp),
package version `0.1.0` (`ghidra_headless_mcp/_version.py`). Its checkout is
at `10a40eb` (2026-06-30) and runs from a venv against 12.1.4
(`~/.claude.json` `mcpServers.ghidra`). Upstream `main` is at `307b461`
(2026-09-16), **8 commits ahead**
([commits](https://github.com/mrphrazer/ghidra-headless-mcp/commits/main)).
The missed fixes that matter here:

- `40bac64`, "Close project when program name is missing" (PR #3): "Opening
  an existing project without program name leaves the project in a locked
  state. This fix properly releases the lock on failure." This matches the
  stale `WWEAllStarsTU2.lock` / `LockException` friction in CLAUDE.md.
- `6f7b903`, "byte searches search pattern plus blanket exception
  swallowing": a `search_bytes` fix.
- `a663589`: `ghidra_eval`/`call`/`script` no longer silently flip read-only
  sessions to writable; concurrent analysis passes on one session are refused
  "instead of corrupting Ghidra's transaction stack"; `type_parse_c` and
  `type_define_c` parse full typedef and composite declarations.
- `72e416b`: correct function-pointer C declarations; analysis guards are
  released after cancellation; decompiler timeouts are hardened.

**Action:** `git pull` in `D:\tools\ghidra-mcp\ghidra-headless-mcp`, then
reinstall into its `.venv`. The venv entry point is `ghidra-headless-mcp.exe`
and an `egg-info` is present, so it is probably an editable install.
**Unverified** that a pull alone is enough. Restart the MCP afterwards.
**Adopt now.**

Alternatives, all checked 2026-10-24:

| Server | Latest | What it adds over ours | Verdict |
|---|---|---|---|
| [LaurieWired/GhidraMCP](https://github.com/LaurieWired/GhidraMCP) | 2.0 (2026-10-10) | Release notes: 12.1.4, no Python bridge, headless mode. Smaller tool set than ours. | Skip |
| [bethington/ghidra-mcp](https://github.com/bethington/ghidra-mcp) | v7.0.0 (2026-10-21), Java 21, Ghidra 12.1.4 | 209 tools; GUI plugin + headless; "function hash matching" for cross-version documentation transfer; multi-program routing; p-code emulation (README). The hash transfer overlaps with VT and `tu_addrmap.py`. | Skip (no gap it fills) |
| [clearbluejar/pyghidra-mcp](https://github.com/clearbluejar/pyghidra-mcp) | v0.2.7 (2026-09-25) | Project-wide analysis of every binary; ChromaDB semantic search; a `--gui` mode that runs Ghidra and the MCP **in one JVM** on the same project (README). The last one is the only real answer to "the owner has the GUI open and the agent gets `LockException`". | Try only if lock clashes continue after action #1 |
| [symgraph/GhidrAssistMCP](https://github.com/symgraph/GhidrAssistMCP) | 3.0.0 (2026-10-09) | Needs Java 25+ (prior note §1.5) | Skip |

`D:\tools\ghidra-cli\ghidra-cli.exe` ("Rust CLI for Ghidra", `ghidra 0.2.1`)
is on PATH. The project does not reference it. Its origin was not traced, and
it adds nothing over the MCP's own `ghidra_cli` client in upstream `main`
(CHANGELOG "Unreleased").

---

## 4. Other tools

### 4.1 rexglue (recompiler)

- There is no stable release after v0.10.0. The latest nightly
  (`bd833a2a`, 2026-10-02) is **24 commits** past our pin `f5337cdc`
  ([compare](https://github.com/rexglue/rexglue-sdk/compare/f5337cdc...bd833a2a)).
- **Codegen fixes in that range:**
  - `6319e236` "pack vpkuwus/vpkuhus through vTemp when vD aliases a source
    (#449)". The pinned builders write vD lane by lane, so `vD == vA/vB` is
    corrupted (`.tools/rexglue-source/src/codegen/builders/vector.cpp`
    L1096–1135). **This game is not affected:** none of the 18 `vpkuhus128`
    and `vpkuwus128` sites in TU2 codegen has vD equal to a source register
    (scan of `generated/default/*.cpp`).
  - `7f7c92ef` "migrate stwcx. to std::atomic CAS, drop CR0.so and redundant
    fences". TU2 codegen has 676 `stwcx` lines.
  - `bd833a2a` "add sync fences for sync, lwsync, and eieio".
  - `ea222e98` "fix register types to be unsigned".

  The last three change how guest atomics and barriers lower to x64. That is
  relevant to `spin-loops.md`-style bugs, but none is known to fix a bug we
  have.
- The other commits are UI, input and logging features that overlap with
  this repo's own patches (fullscreen, vibration, window restore). Rebasing
  the 28 `patches/rexglue-*.patch` files has a real cost.
- **Verdict: watch.** ADR-0005 ties upgrades to SVR2011. Raise the atomics
  commits there when the next upgrade is planned.
- [hedge-dev/XenonRecomp](https://github.com/hedge-dev/XenonRecomp): last
  push 2025-08-04, stale. [XenosRecomp](https://github.com/hedge-dev/XenosRecomp):
  last push 2026-03-12. Recent community recomps (e.g.
  [nimauria/Xenon-Recomp](https://github.com/nimauria/Xenon-Recomp), 9★,
  "experimental") offer nothing over rexglue. **Skip.**

### 4.2 Xenia Canary: GPU traces now work (biggest change since October)

- Local: `02d2cb5` (2026-03-24, `xenia-baseline.md` Setup). Latest release:
  `9e03494` (2026-10-23), assets `xenia_canary_windows.7z` and a Linux
  AppImage ([releases](https://github.com/xenia-canary/xenia-canary/releases)).
- Four commits in mid-October fix the tooling the prior note could not use
  (§3.4 there):
  - [`b83593c`](https://github.com/xenia-canary/xenia-canary/commit/b83593c)
    (2026-10-15) "Fix GPU trace replay in the trace dumps and trace viewers".
    Before it, the viewers crashed on a null ImGui drawer, and "every replayed
    draw failed with 'Failed in backend'". So a 2026-10-11 build of the
    viewer would not have worked.
  - [`fbd0edf`](https://github.com/xenia-canary/xenia-canary/commit/fbd0edf)
    adds the CMake option `XENIA_ENABLE_GPU_TRACE`: "builds the trace writer
    into Release too". Before it, F4 / `--trace_gpu_stream` "only works in
    Debug" and was "silently ignored" elsewhere.
  - [`e735514`](https://github.com/xenia-canary/xenia-canary/commit/e735514)
    adds `trace_gpu_frames` ("a comma-separated list of frame numbers"), so
    captures need no one at the keyboard. That suits the rule that the owner
    drives the game by hand.
  - [`716afa0`](https://github.com/xenia-canary/xenia-canary/commit/716afa0)
    fixes the Windows link of the Vulkan trace viewer.
- The release CI still ships only the emulator. Both options are OFF by
  default (`CMakeLists.txt` L38–39: `XENIA_BUILD_MISC`,
  `XENIA_ENABLE_GPU_TRACE`). So this is a one-time local build of upstream
  at `9e03494` with both options ON.
- **Payoff:** a `.xtr` of a menu or ring frame, viewed per draw, is ground
  truth for Native renderer bugs and for the texture reskin work (formats,
  tiling, which fetch constant a texture uses). It works without RenderDoc.
- **Cost:** about 1 day (submodules, CMake, MSVC). Re-baseline
  `xenia-baseline.md` on the new build.
- Still **no GDB stub** in Canary (no `gdb` path in the tree).
- **Verdict: adopt.**

### 4.3 Runtime tracing and diffing

- **mcp-windbg** [v1.6.0](https://github.com/svnscha/mcp-windbg/releases/tag/v1.6.0)
  (2026-10-23) adds `--log-dir` and `read_debugger_log`, plus remote-server
  warnings. Neither its README nor its last four release notes mention TTD or
  `.run` traces. The question in the prior note stays open (**unverified**).
  TTD itself is still not installed (`ttd` not on PATH).
- No Xenia↔recomp lockstep diff tool appeared. The prior note's §5 plan
  stands.

### 4.4 GPU tools

There is no change in versions since the prior note: RenderDoc v1.46 and
Tracy v0.14.1 are still the latest
([renderdoc](https://github.com/baldurk/renderdoc/releases),
[tracy](https://github.com/wolfpld/tracy/releases)). RenderDoc is still not
installed (no `C:\Program Files\RenderDoc`). The AMD developer tools are
still absent (`C:\Program Files\AMD` holds only driver components).
Priorities are as in the prior note, but Canary traces (§4.2) now cover part
of what RenderDoc would give for Xenos-level questions.

### 4.5 Asset and modding tools (UE3, for texture reskins)

Nothing new that writes All Stars packages:

- [UE Viewer](https://github.com/gildor2/UEViewer): last push 2024-03-16. It
  reads only (as in `modding.md` §3.5).
- [UE Explorer](https://github.com/UE-Explorer/UE-Explorer): still 1.6.2
  (2026-06-03).
- [UPKUtils](https://github.com/wghost/UPKUtils) has
  `ImportTexturesFromDDS`, which injects textures into packages and the `.tfc`
  file. But "the tools are locked to work with certain versions of the
  engine", XCOM and Batman PC (README). The last commit is 2022-11-04, and it
  is GPL-2.0. Its approach (rewrite the texture export's bulk data and the
  TFC entry) is the same one `modding.md` §3.5 sketches. **Skip as a tool;
  read it as a format reference only.** Mind the GPL if any code is reused
  (ADR-0007).
- No All Stars–specific modding tools were found on GitHub (searches for
  "upk texture inject", "ue3 package xbox 360", "xbox 360 texture untile",
  2026-10-24). The texture tool remains in-house work.

### 4.6 IDA / Binary Ninja

No change. IDA's VMX128 decode was the only edge, and Xenon360 mostly closes
it. [idaxex](https://github.com/emoose/idaxex) is still 0.44 (2026-06-04).
**Skip**, unless the owner already has an IDA licence. If so, IDA would also
unlock the FIDB generator (§2.3).

## Open questions

- Whether Xenon360's three defined `vpk*128` forms decode correctly on TU2
  (open a copy of the project, compare with `disasm.py`).
- How much hand markup `default_v5` holds (decides whether VT is worth
  running).
- Whether a `git pull` of `ghidra-headless-mcp` needs a reinstall of its
  venv.
- Whether FID in 12.2 can apply an FIDB built for another PowerPC language
  ID.
- Whether `mcp-windbg` can open TTD `.run` traces.
