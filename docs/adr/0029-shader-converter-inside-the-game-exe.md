# ADR-0029: The shader converter lives inside the game exe

Status: accepted (2026-11-07). The owner requires a release no virus scanner flags, without
waiting on Microsoft.

## Context

The release shipped XenosRecomp as `xenosrecomp/XenosRecomp.exe`. On 2026-11-07 Windows Defender
removed it as `Trojan:Win32/Bearfoos.B!ml`, a cloud machine-learning verdict:

- The 1.7.0 converter (`E401B34E…`), clean for 13 days and downloaded 1744 times, is now refused on
  first read, before it runs.
- The convicted 1.7.1 converter (`9E629B0F…`) and an unflagged rebuild (`368A4152…`) differ only in
  the link time in the PE header. So the verdict follows the hash once it is judged, and "not
  flagged yet" says nothing about a configuration.
- Version information did not help: a static build with it was flagged after a conversion, on its
  next read.
- `wweallstars.exe`, with its icon and version information, has never been flagged.

A rebuild only gets a hash that has not been judged yet, and code signing or a false-positive
report means waiting on someone else.

## Decision

Ship no standalone converter. XenosRecomp's sources link into `wweallstars.exe`, and
`wweallstars.exe --wwe_convert_shaders <input> <output> <shader_common.h> <hlsl folder>` runs it and
exits before any window, Vulkan or guest memory exists. The launcher runs that, and so do the game's
live top-ups, which also no longer start the converter suspended.

- `xenosrecomp/` keeps `shader_common.h` and Microsoft's signed `dxcompiler.dll`, loaded only when
  converting. `fmt.dll` is gone.
- The launcher deletes `XenosRecomp.exe` and `fmt.dll` left by an older release unzipped over.
- The converter id is still the patch's hash, and the pack is byte-identical to the old exe's, so
  players' packs stay valid.
- `scripts/build-xenosrecomp.ps1` stays as a dev tool (a baseline for `shader_regen_diff.py`) and
  nothing it builds ships.

The Tracy profiler client also leaves the release. A VirusTotal scan of the first candidate zip was
1/67: Kaspersky flagged `TracyClientrd.dll` as `VHO:Trojan.Win32.Sdum.gen`, and every other binary
looked up was clean, including the game exe at 0/70. `build.ps1` now configures with
`REXGLUE_ENABLE_TRACY=OFF`, and profiling takes `-CMakeArgs '-DREXGLUE_ENABLE_TRACY=ON'`.

## Consequences

- `scripts/check-defender.ps1` can only show that a build has no verdict yet. A later model can still
  flag `wweallstars.exe` or the launcher. Signing the release is the lasting fix.
- XenosRecomp's fmt 11 sits header-only in its own inline namespace beside the SDK's fmt 12.
