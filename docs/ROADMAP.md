# WWE All Stars Recomp — Roadmap

Plan agreed on 2026-10-02. Each decision behind it is recorded in `docs/adr/`.

## Target

- Xbox 360 `default.xex`, title `54510866`, media `60CEF505`, version 5
  (2011-02-23), SHA-256 `521c2325d89470ff490efee257885a465360ffb64f9db2491eda6af7ab3a6892`.
- THQ-customised Unreal Engine 3 (`GameWrestlingXBox360`), Havok 4.5.1, FMOD Ex
  with XMA, Scaleform GFx, Bink 1, Quazal. XDK 2.0.11775.3 with D3D9 statically
  linked.
- Two title updates exist for this media ID (`docs/research/title-update.md`).
  The latest TU is the target once a player-supplied copy can be verified
  (ADR-0009).

## v1.0 definition of done

An enhanced port running on ReXGlue v0.10.0:

- The native Vulkan renderer is the default. The Emulated renderer (formerly Classic, ADR-0020)
  stays available as a fallback and as a debugging reference.
- Internal resolution scaling and MSAA or better anti-aliasing.
- The original frame rate, held without hitches.
- 4 local controllers, with players 2–4 joining as guests of profile 1.
- XInput, SDL3 gamepads (correct button glyphs, hot-plug), keyboard and mouse,
  and Steam Input.
- DLC packs from STFS packages the player dumps themselves, installed through
  the launcher.
- All 7 languages on the disc.
- Achievements shown in an overlay.
- A single local profile with portable saves.
- Online stubbed, so the game shows its own offline message.
- Test matrix: Exhibition (all match types), Path of Champions, Fantasy
  Warfare, Create-a-Superstar.
- Minimum spec: Steam Deck at native resolution and the original frame rate.
- Windows first; Linux and Steam Deck from the beta.

## Status

- 2026-10-02: M0 done (strict codegen, links). M1 done (main menu on Classic, `docs/boot-investigation.md`). M2 started.

## Milestones

| # | Milestone | Contents | Exit criteria |
|---|---|---|---|
| M0 | Groundwork | Repo skeleton, pinned toolchain, XEX hash gate, disc extractor, Xenia Canary baseline, TU research, Ghidra project, docs | Codegen passes in strict mode and the executable links; baseline written |
| M1 | Boot (Emulated) | Runtime-guided seed discovery, golden-trace comparison against Xenia, port SVR2011's runtime patches, audio-loop fix | Main menu reached on the emulated Xenos renderer |
| M2 | Native bring-up ⭐ | Map the D3D9 library (PM4 scan plus Ghidra), draw census, port `src/native`, extract and convert shaders; stabilise Emulated exhibition matches alongside | Native renders the menus; a Emulated exhibition match is playable |
| M3 | Native gameplay | All texture formats, render targets and resolves; resolution scale 1–4x; MSAA/AA; frame comparison against Emulated | A full exhibition match plays in Native |
| M4 | Content coverage | TU, DLC through STFS, every mode in the test matrix, 7 languages, input stack, 4-player guests, achievements overlay, online stub | Every mode in the matrix passes in Emulated and Native |
| M5 | Native default + launcher | Avalonia launcher (extract and verify, shader pre-conversion, GPU selection, DLC/TU install); Native reaches parity and becomes the default | **Repo and first binaries go public** |
| M6 | Linux/Deck beta | Linux build, Steam Deck at minimum spec, NVIDIA and Intel testers | Beta passes on all platforms → **v1.0** |

Post-v1, in this order:

1. Online play (ADR-0021, accepted after the spike in `.scratch/online-spike/`):
   the game's own online mode against a self-hostable game server
   (`.scratch/online-play/`). The build waits until after v1.0.
2. 60 fps, after the game's timing model is understood.
3. Mod and asset hooks.

Ultrawide is out of scope. HD texture packs came into scope in ADR-0028.

## Ground rules

- The ISO is never modified. Game data, generated code, converted shaders and
  logs never enter Git.
- Every tool is pinned and hash-checked, and every build checks the XEX hash.
- Codegen always runs with strict validation. Missing functions are added as
  seeds, never stubbed.
- Emulated and Native must both keep building. Each renderer serves as the
  other's reference.
