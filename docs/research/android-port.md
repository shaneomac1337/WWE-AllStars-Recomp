# Android port: feasibility, and doing it without a phone

Date 2026-10-23. Owner's question: "Is it possible to have an Android port
done, even though I don't have an Android phone?" Paths in ReXGlue are
relative to `.tools/rexglue-source` (`f5337cdc` plus `patches/`). "SVR-PC"
means [wwe-svr2011-pc](https://github.com/KaikoClanworth1/wwe-svr2011-pc)
(KaikoClanworth1), read for this note from a shallow clone at `b2521fe`
(2026-10-22); its paths are prefixed `svr-pc:`. Reuse of its code is allowed
with credit (`docs/research/svr2011-pc-port.md` §1). Nothing was built or run
for this note. **[V]** means checked against source or an official page.
**[I]** means inference or an estimate.

## Verdict

- **Technically: yes.** The CPU side is already portable:
  - ReXGlue builds for `linux-arm64` and `mac-arm64` in its own CI.
  - The generated code uses SIMDe, not raw SSE.
  - ReXGlue carries Xenia's half-finished Android hooks.

  The closest prior art runs on phones today. SVR-PC is a game on the same
  ReXGlue v0.10.0 with the same native D3D9-hook renderer design, and it ships
  an Android APK that "holds 60 fps" on a Galaxy Z Fold 7. Its author granted
  us reuse of its code. **[V]**
- **The work is not in the recompiled code.** It is in five places, in this
  order of risk:
  1. GPU driver variance (Mali, old Adreno drivers).
  2. A 16 KB page-size bug in ReXGlue's memory macros.
  3. Shader delivery under ADR-0012.
  4. An Android front end that replaces the Avalonia launcher.
  5. Input (touch) and app lifecycle.

  This repo has also never been built off Windows
  (`performance-and-low-end-hardware.md:57`). **[V]**
- **Without a phone: you can build almost all of it, but you can't sign it
  off.**
  - **Can do on this PC:** build the APK on Windows with the NDK, as SVR-PC
    does with no Gradle.
  - **Can do on this PC:** test the platform layer, lifecycle, storage and
    16 KB pages in the x86_64 Android emulator.
  - **Can do on this PC:** test the arm64 guest code headless on a free
    arm64 Linux runner.
  - **Can do on a remote device:** spot-check on real phones through Android
    Device Streaming (30 free minutes a month), Samsung Remote Test Lab or AWS
    Device Farm.
  - **Can't do without hardware:** performance, thermals, Adreno/Mali driver
    bugs, controller and touch feel, audio latency. Volunteer testers have to
    cover these. Nine people asked for the port, and issue #4's reporter has
    an AYN handheld. **[V]/[I]**
- **Effort (estimate).**
  - A controller-only MVP for Snapdragon devices, installed from the PC
    launcher: about **3–5 weeks** of focused work.
  - A polished port (on-device ISO install, touch controls, Mali fallback):
    **2–3 months**, plus an open-ended tail of device bugs reported by
    testers.
  - The main risk is not "can it run". It is that every fix to an
    Android-only bug goes through a tester round trip. **[I]**

## 1. Technical feasibility (arm64-v8a)

### 1.1 Toolchain and build

- ReXGlue requires Clang ≥ 18 and a 64-bit target, "x86_64 or ARM64"
  (`CMakeLists.txt:83-99`). The NDK toolchain is Clang, so this holds. **[V]**
- An Android CMake build is `UNIX AND NOT APPLE`, which takes the Linux branch:
  - It sets `REX_PLATFORM linux-arm64` (`CMakeLists.txt:159-166`).
  - It sets `-mcmodel=small` on non-x86 targets (`CMakeLists.txt:118-126`).
  - `include/rex/platform.h:35-37` defines both `REX_PLATFORM_ANDROID` and
    `REX_PLATFORM_LINUX` under `__ANDROID__`. **[V]**
- Upstream CI builds `linux-arm64` and `mac-arm64`
  (`.github/workflows/build-linux-aarch64.yaml`, `build-mac-arm64.yaml`).
  Upstream has no Android issue or PR. A search of `rexglue/rexglue-sdk`
  finds only unrelated PRs (#8, #514, #522, #528). **[V]**
- **The stock tree does not compile for Android.** It still has Xenia's
  Android code paths, which reference files that don't exist:
  - `rex/ui/surface_android.h` (used in `src/ui/vulkan/vulkan_presenter.cpp:36-37,437-439,813-817`).
  - `rex/main_android.h` (used in `src/core/memory_posix.cpp:56-57`).
  - `GetAndroidApiLevel` (used in `src/core/threading_posix.cpp:74`).

  SVR-PC's SDK patch adds them all: `include/rex/main_android.h`,
  `src/core/main_android.cpp`, `include/rex/ui/surface_android.h`,
  `src/ui/surface_android.cpp`, and SDL window changes
  (`svr-pc:port/patches/rexglue-sdk-svr2011.patch`, diff headers at lines
  415, 755, 1111, 5839, 6376–6619). **[V]**
- Fibers: ReXGlue's POSIX fibers use `makecontext`/`swapcontext`
  (`src/core/fiber_posix.cpp:3,55,68`). SVR-PC found that bionic lacks them
  and wrote an arm64 register switch in inline asm (patch L993–1090).
  Bionic's [status page](https://android.googlesource.com/platform/bionic/+/main/docs/status.md)
  doesn't list them among missing functions, so treat SVR-PC's comment as
  the evidence. Either way, the patch exists. **[V]** for the patch, **[I]**
  for the reason.
- Shared memory: bionic has no `shm_open`
  ([bionic status](https://android.googlesource.com/platform/bionic/+/main/docs/status.md)).
  ReXGlue already switches to `ASharedMemory_create` under
  `REX_PLATFORM_ANDROID` (`src/core/memory_posix.cpp:87-110,434-440`). **[V]**
- Older kernels: Android 10 devices on kernel 4.14 don't know
  `MAP_FIXED_NOREPLACE` and treat the address as a hint. SVR-PC detects that
  case (patch L1161–1190). **[V]**
- SVR-PC builds its APK on Windows with the NDK and no Gradle:
  - Script: `svr-pc:port/tools/build_apk.py`.
  - NDK `30.0.16248370`, `-DANDROID_ABI=arm64-v8a`,
    `-DANDROID_PLATFORM=android-29` (`build_apk.py:30,51`).
  - Native libs: `librexruntime.so` and `librexgpu-xenos.so` from
    `out/linux-arm64` (`build_apk.py:139-140`).
  - Manifest: minSdk 29, targetSdk 36
    (`svr-pc:port/android/AndroidManifest.xml:7`).

  NDK r28+ aligns ELF segments to 16 KB by default
  ([Android 16 KB guide](https://developer.android.com/guide/practices/page-sizes)).
  **[V]**

### 1.2 Generated code: SIMD, byte swaps, FP

- **No raw x86 intrinsics in the generated code.** All 113,415 vector loads
  in `generated/default` are `simde_mm_load_si128`. Other heavy users:
  `simde_mm_shuffle_epi8` (33,668), `simde_mm_dp_ps` (2,106),
  `simde_mm_sllv_epi16` (2,438) (counted with grep over the generated tree).
  SIMDe maps these to NEON on arm64. **[V]**
- The runtime helpers have explicit arm64 paths:
  - `include/rex/ppc/intrinsics.h:25-27,246-260` (NEON).
  - `include/rex/platform/fpscr.h:53` (FPCR).
  - `src/core/memory.cpp:205-215` (NEON byte-swap copies).
  - `include/rex/exception_handler.h:241,298,448` (arm64 thread context for
    MMIO faults). **[V]**
- Byte swaps are `__builtin_bswap*` in the load/store macros
  (`generated/default/wweallstars_pch.h:127-137`). On arm64 these are a
  single `rev`. **[V]**
- Performance risk:
  - SSE ops without a one-instruction NEON equivalent (`dp_ps`, `sllv_epi16`
    and the `blendv`/`shuffle_epi8` pairs in `intrinsics.h:162`) become
    several NEON instructions.
  - This repo's code passes `-ffp-contract=off` (`CMakeLists.txt:100-110`),
    so there is no FMA fusion. That is correct, and it costs the same on
    arm64.
  - This is only a guess until it is measured. Guest work per tick on the
    owner's desktop is median 3.3 ms, p99 7.5 ms against a 16.67 ms budget
    (`performance-and-low-end-hardware.md:40`). A phone core that is 2–3×
    slower would pass at the median and get close to the budget at p99. **[I]**

### 1.3 Guest memory: 4 GB reservation and 16 KB pages

- ReXGlue maps a 0x120000000-byte shared mapping. It tries views at
  `1<<32`, `1<<33`, … (`src/system/xmemory.cpp:139-175`). arm64 Android user
  space is at least 39-bit (512 GB), so this fits. **[I]** (no device run)
- **Bug for 16 KB-page devices.** The 0xE0000000 physical heap sits at a
  0x1000 offset in the backing file. When the host allocation granularity is
  larger than 4 KB, the mapping rounds that offset away, so every guest
  access has to add it back:
  - The decision is made at compile time, only for
    `REX_PLATFORM_WIN32 || (REX_PLATFORM_MAC && arm64)`.
  - It appears in the codegen template
    (`resources/templates/codegen/pch_h.inja:127-131`), in our generated
    header (`generated/default/wweallstars_pch.h:119-123`) and in
    `include/rex/system/xmemory.h:30-45`.
  - The comment there assumes "Linux (any): 4KB granularity".
  - But `MapViews` masks by the runtime granularity
    (`src/system/xmemory.cpp:360-371`), and on POSIX that is `page_size()`
    (`src/core/memory_posix.cpp:114-118`).

  On a 16 KB Android device the views shift while the generated code adds
  0. Every guest access at 0xE0000000 and above would then read 4 KB off.
  The fix is small: always apply the offset on Android, and force the
  granularity to ≥ 16 KB there. It needs a regenerate. **[V]** for the code,
  **[I]** for the failure.
- Why this matters now:
  - Android 15+ supports 16 KB devices. Pixel 8/8a/9/9a have a developer
    option to boot in 16 KB mode.
  - Play requires 16 KB support for apps targeting API 35+. Updates that
    don't support it are blocked from February 1, 2027.
  - Native code must not assume 4096-byte pages.

  Source: [Android 16 KB guide](https://developer.android.com/guide/practices/page-sizes).
  We would sideload rather than use Play (§1.8), but 16 KB phones will
  exist regardless. **[V]**

### 1.4 GPU: Vulkan features the renderers require

**Native renderer.** It refuses to start without these features
(`src/native/vk_context.cpp:150-161`):

- `bufferDeviceAddress`
- `runtimeDescriptorArray`
- `descriptorBindingPartiallyBound`
- `descriptorBindingSampledImageUpdateAfterBind`
- `descriptorBindingUpdateUnusedWhilePending`
- `shaderInt64`
- `dynamicRendering`

It also uploads DXT textures as `VK_FORMAT_BC1/2/3`
(`src/native/texture_formats.h:31-35`) with no CPU fallback. **[V]**

**Emulated renderer.** ReXGlue rejects the whole Vulkan device for GPU
emulation in any of these cases (`src/ui/vulkan/vulkan_device.cpp:112-144`):

- `vertexPipelineStoresAndAtomics` is missing.
- `fragmentStoresAndAtomics` is missing.
- `geometryShader` or `fillModeNonSolid` is missing (both are on by default
  except on macOS, `vulkan_device.cpp:34-41`). **[V]**

Android coverage on [gpuinfo.org](https://vulkan.gpuinfo.org/listfeaturescore10.php?platform=android)
(share of uploaded reports, not of devices in use, retrieved 2026-10-23):

| Feature | Android coverage |
|---|---|
| `textureCompressionBC` | 37.8 % |
| `shaderInt64` | 50.4 % |
| `vertexPipelineStoresAndAtomics` | 56.8 % |
| `fillModeNonSolid` | 55.9 % |
| `geometryShader` | 93.9 % |
| `bufferDeviceAddress` ([core 1.2 list](https://vulkan.gpuinfo.org/listfeaturescore12.php?platform=android)) | 99.9 % |
| descriptor indexing, update-after-bind | 93.7 % |

**[V]**

What this means per GPU family:

- **Adreno (Snapdragon).** Workable. SVR-PC targets it first, and it lets
  players load Mesa Turnip or Qualcomm driver packages through
  [libadrenotools](https://github.com/bylaws/libadrenotools)
  (`svr-pc:README.md:178-180`, `port/src/gpu_driver.cpp`). It also bundles
  two Turnip builds because the stock drivers on Adreno 710/720/722 "are
  often too old for the Native renderer" (`svr-pc:port/android/drivers/`).
  **[V]**
- **Mali.** Needs fallbacks:
  - No BC sampling, so SVR-PC decodes BC on the CPU
    (`svr-pc:port/src/native/textures.cpp:72,246-530`,
    `backend_vulkan.cpp:151-157`).
  - No `vertexPipelineStoresAndAtomics`, `geometryShader` or
    `fillModeNonSolid`, so SVR-PC has a "Mali alpha" mode that relaxes the
    Emulated checks (`backend_vulkan.cpp:28-30,63-71`; SDK patch L2263–2300,
    L5874).
  - A second shader pack for drivers without descriptor indexing
    (`svr-pc:port/android/java/.../Shaders.java`, `kFiles`).
  - The crowd is turned off on weak phones (`svr-pc:port/src/crowd_off.cpp`).

  Our Native renderer would need the BC fallback at minimum. **[V]**
- **Baseline.** Android's own Vulkan profiles guarantee only ASTC/ETC
  textures and Vulkan 1.1 (AVP 2022, 86.5 % of active devices)
  ([Android Baseline Profile](https://developer.android.com/ndk/guides/graphics/android-baseline-profile)).
  BC is not part of any Android baseline. **[V]**

### 1.5 Shaders (ADR-0012)

- Native shaders are made on the player's machine:
  - The launcher reads the containers from the disc packages and runs
    XenosRecomp (ADR-0012 amendment).
  - The in-game top-up runs the converter as a child process through
    `CreateProcessW`, under `#ifdef _WIN32` only
    (`src/native/topup_converter.cpp:53-100`).
  - The release ships no game-derived shaders.
- SVR-PC does it differently. It **bundles** its converted SPIR-V pack in the
  APK (`svr-pc:port/tools/build_apk.py:147-164`). Our ADR-0012 forbids that.
  **[V]**
- Options for us. **[I]**
  1. The Windows launcher converts as today and pushes the pack with the game
     over adb. This is SVR-PC's "Android Install" route, minus the bundled
     shaders. Cheapest, but the player needs a PC.
  2. Build XenosRecomp and DXC for arm64 Android and convert on the phone.
     DXC builds on Linux, so arm64 should be reachable. Untried.
  3. No Native, Emulated only. Not viable on Mali (§1.4), and slow on phones.

### 1.6 Audio, input, windowing

- **Audio.** ReXGlue's audio driver is SDL (`src/audio/sdl`). The vendored
  SDL3 has AAudio and OpenSL ES backends (`thirdparty/sdl3/src/audio/aaudio`,
  `openslES`) and supports API 21+
  (`thirdparty/sdl3/docs/README-android.md:19`). XMA decoding goes through
  vendored FFmpeg, which is CPU work that is plain C. No Oboe is needed.
  **[V]**
- **Input.** SDL3 gamepads (`src/input/sdl`) cover Android controllers.
  XInput and MnK (`src/input/xinput`, `mnk`) are Windows or desktop only.
  - Touch controls don't exist. SVR-PC shipped controller-only first
    (v0.3.0: "There are no touch controls yet") and added touch later.
  - SVR-PC's v1.0.1 touch fix shows the trap: touches arrived in pixels while
    the layout used dp.
  - Sources: [v0.3.0](https://github.com/KaikoClanworth1/wwe-svr2011-pc/releases/tag/v0.3.0),
    [v1.0.1](https://github.com/KaikoClanworth1/wwe-svr2011-pc/releases/tag/v1.0.1).
    **[V]**
- **Lifecycle.** Android replaces the window surface when the app goes to
  the background. SVR-PC's v1.0.1 fixed a "black screen with sound" after an
  app switch ([v1.0.1](https://github.com/KaikoClanworth1/wwe-svr2011-pc/releases/tag/v1.0.1)).
  SDL blocks the app thread on pause by default
  (`thirdparty/sdl3/docs/README-android.md:307`). **[V]**
- **Our own code.** 7 of 43 `src/` files have `_WIN32` paths:
  - `crash_report.cpp` is Windows only: minidump and `Sleep`.
  - `startup_paths.cpp`, `pipelines.cpp` and `topup_converter.cpp` use
    `GetModuleFileNameW`.
  - `resolve_watch.cpp` is a Windows dev tool behind `#ifndef _WIN32`.

  No x86 intrinsics in `src/`. Rumble goes through SDL. **[V]**

### 1.7 Game data and storage

- The game reads about 5.9 GB of extracted data (`du -sh assets`). SVR-PC
  asks for about 7 GB free (`svr-pc:README.md:160`). **[V]**
- SVR-PC uses `MANAGE_EXTERNAL_STORAGE` and keeps the game under `games/…`
  in shared storage (`svr-pc:port/android/AndroidManifest.xml:17-20`).
  - Google Play allows that permission only for file managers, backup,
    antivirus and similar apps
    ([All-files access](https://developer.android.com/training/data-storage/manage-all-files)).
  - That is fine for a sideloaded APK. On Play, the data would have to go in
    app-specific storage or through SAF.
  - ReXGlue already has a content-URI file opener under
    `REX_PLATFORM_ANDROID` (`include/rex/filesystem.h:129-134`,
    `src/core/mapped_memory_posix.cpp:124-143`). Its implementation
    `OpenAndroidContentFileDescriptor` is not in the tree. **[V]**
- Distribution is a sideloaded APK from GitHub releases, as SVR-PC does:
  - The game is a copyrighted retail title, played from the player's own dump.
  - Its APK is 95.5 MB (`SvR2011-Android-v2.1.5.apk`, 95,521,191 bytes).
  - Our Windows exe is 107.6 MB, so an arm64 `.so` would be similar. **[V]**
    for the sizes, **[I]** for the arm64 estimate.

### 1.8 Launcher

- The launcher is Avalonia 11.3 on .NET 10 with Native AOT (ADR-0018,
  `launcher/Directory.Packages.props:3`). Avalonia runs on Android, but
  NativeAOT on Android is still "experimental" (.NET warning
  [XA1040](https://learn.microsoft.com/en-au/dotnet/android/messages/xa1040)).
- The launcher's real work wouldn't port as-is:
  - ISO extraction and TU2 apply would.
  - Running XenosRecomp and DXC as child processes, which are Windows
    executables today, would not.
- SVR-PC kept its PC launcher (with an "Android Install" tab that uses adb)
  and wrote a small Java front end for the phone
  (`svr-pc:port/android/java/.../LauncherActivity.java`, `InstallPage.java`).
- The cheapest path for us is the same split:
  - The PC launcher prepares data and shaders and pushes them.
  - A thin Java/SDL front end on the phone. **[V]/[I]**

### 1.9 Other Xbox 360 projects on Android

| Project | What it is | Status | Source |
|---|---|---|---|
| wwe-svr2011-pc | ReXGlue recomp (same SDK as ours) | Android since v0.3.0 (2026-09-29), one day after v0.1.0; Fold 7 at 60 fps; open Android issues are freezes at 30 fps and slowdowns on an Adreno 642L | [README §Android](https://github.com/KaikoClanworth1/wwe-svr2011-pc#android), [#8](https://github.com/KaikoClanworth1/wwe-svr2011-pc/issues/8), [#18](https://github.com/KaikoClanworth1/wwe-svr2011-pc/issues/18) |
| aX360e (aenu) | Xenia/Canary port with its arm64 backend | Experimental, on Play since Dec 2025, last push 2026-08-16 | [github.com/aenu1/ax360e](https://github.com/aenu1/ax360e) |
| xenia-android → Xeo | Canary ports | Both **archived**: xenia-android points to Xeo, and Xeo was archived 2026-08 | [xenia-android](https://github.com/xenia-android/xenia-android), [libaerto/xeo](https://github.com/libaerto/xeo) |

**[V]** (repo metadata via `gh repo view`)

Static recompilation avoids the JIT problem that held Xenia back on Android.
The emulator projects stall on CPU translation, while the ReXGlue port shipped
in about a day. **[I]**

## 2. Doing it without an Android phone

| Way | What it verifies | What it doesn't | Cost |
|---|---|---|---|
| **NDK build on this PC** | Compile and link for arm64, APK packaging, 16 KB ELF alignment (`zipalign -c -P 16`, `llvm-objdump`) | Anything at runtime | Free |
| **Android Emulator, x86_64 image** with an x86_64 build of the game | SDL activity, surface and lifecycle, storage, guest memory reservation, audio, input plumbing. A **16 KB x86_64 image** exists, so the §1.3 bug can be reproduced on this PC | arm64 code, phone GPUs, performance | Free |
| **Emulator, ARM translation** | The arm64 APK starts (Android 11+ Google APIs/Play x86 images translate ARM per process; "only for development and debug") | Speed (a translated 100 MB binary), GPU realism | Free |
| **arm64 Linux, headless** (GitHub `ubuntu-24.04-arm` runners, free for public repos; or Oracle Always Free Ampere A1) | The arm64 build of the recompiled guest code and SIMDe/NEON correctness: run the scripted-input harness and compare tick logs with x64 | Android specifics, phone GPUs | Free |
| **Android Device Streaming** (Android Studio; Pixels plus Samsung/Xiaomi/OPPO/OnePlus/vivo partner labs) | Interactive real device over ADB-over-SSL: install, logcat, look at the screen | Smooth play (video stream), controllers, long sessions | 30 min/month free, then $0.15/min (Blaze) |
| **Firebase Test Lab, Game Loop** | Automated runs on real phones: the app starts a scripted scenario from an intent, with crash and timeout tracking | Interaction, frame pacing feel | Spark: 5 physical runs/day; deprecated, shuts down 2027-09-30 |
| **AWS Device Farm remote access** | Interactive real device in the browser, with video and logs | Gamepad, audio | One-time 1,000 free minutes, then $0.17/min |
| **Samsung Remote Test Lab** | Interactive Galaxy devices, including 16 KB-page ones | Audio, peripherals (per older guides) | Free daily credits (current numbers unverified) |
| **Community testers** | Real performance, drivers, thermals, controllers, touch | Fast iteration | Free; nine requesters, AYN/Retroid owners |

Sources:

- Emulator images, 16 KB:
  [Android 16 KB guide](https://developer.android.com/guide/practices/page-sizes).
- ARM translation:
  [Android Developers Blog, 2020](https://android-developers.googleblog.com/2020/03/run-arm-apps-on-android-emulator.html).
- Emulator GPU modes (`host`, `swiftshader`, `lavapipe`) and WHPX:
  [emulator acceleration](https://developer.android.com/studio/run/emulator-acceleration).
- GitHub arm64 runners:
  [changelog 2025-08-07](https://github.blog/changelog/2025-08-07-arm64-hosted-runners-for-public-repositories-are-now-generally-available/).
- Oracle A1:
  [Always Free resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm).
- Device Streaming:
  [Android Studio docs](https://developer.android.com/studio/run/android-device-streaming),
  [pricing](https://firebase.google.com/docs/test-lab/usage-quotas-pricing#device-streaming).
- Test Lab:
  [quotas](https://firebase.google.com/docs/test-lab/usage-quotas-pricing),
  [Game Loop](https://firebase.google.com/docs/test-lab/android/game-loop).
- AWS:
  [pricing](https://aws.amazon.com/device-farm/pricing/),
  [remote access](https://docs.aws.amazon.com/devicefarm/latest/developerguide/remote-access.html).
- Samsung 16 KB devices: linked from the Android 16 KB guide. Credit numbers
  only from older third-party guides, e.g.
  [TestFairy](https://testfairy.com/blog/test-android-apps-on-the-latest-samsung-devices-remotely-for-free/).
  **[V]** except where marked.

Notes:

- **No arm64 system image on an x86_64 host.** Reports show the emulator
  refusing ("CPU Architecture 'arm64' is not supported by the QEMU2 emulator
  on x86_64 host") ([android-emulator-runner logs](https://redirect.github.com/ReactiveCircus/android-emulator-runner/actions)).
  ARM translation inside an x86_64 image is the only way to run ARM code
  there. **[I]** (no official page found)
- **The emulator GPU is not a phone GPU.** In `host` mode the emulator
  forwards Vulkan to the PC's GPU through its own translation layer. Which
  Vulkan 1.2/1.3 features it exposes is not documented. It may lack
  `bufferDeviceAddress` or BC, or have them when a phone wouldn't. So it
  verifies plumbing, not Adreno/Mali behaviour. Running it uses the owner's
  GPU like any app and changes no GPU settings. **[I]**
- **Windows Subsystem for Android is gone.** It was removed from the
  Microsoft Store on 2025-03-05
  ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/android/wsa/)).
  **[V]**
- **The emulator hypervisor driver (AEHD) ends on 2026-12-31.** Use WHPX
  ([emulator acceleration](https://developer.android.com/studio/run/emulator-acceleration)).
  **[V]**

**What can't be verified without hardware in someone's hands:**

- Sustained performance and thermal throttling over a full match. SVR-PC
  logs the thermal state for this reason (`svr-pc:port/src/frame_stats.cpp:20,57-62`).
- Driver bugs on Adreno or Mali stock drivers, and on Turnip versions.
- Controller latency and feel over Bluetooth.
- The touch layout's ergonomics.
- Audio latency and crackle.
- Memory pressure on 6–8 GB phones: a 4 GB guest plus GPU memory in shared
  RAM, and the low-memory killer.
- HyperOS and One UI quirks. SVR-PC needed a HyperOS 3 Turnip variable
  (`svr-pc:README.md:178`).

Remote devices and community testers can partly cover these. The owner's
usual by-hand play check does not translate to them. **[I]**

**Suggested no-phone sequence.** **[I]**

1. Native Linux x64 build. This is the first non-Windows build of this repo,
   and it surfaces most platform gaps for free.
2. A linux-arm64 headless run on a free arm64 runner, comparing tick logs
   with x64.
3. An Android x86_64 build in the emulator, including the 16 KB image.
4. The arm64 APK on Device Streaming for a few minutes per milestone.
5. An "experimental, Snapdragon + controller" prerelease for testers, with a
   log and crash bundle they can send.

## Open questions

1. Is Android in scope at all? ADR-0006 lists Windows and Linux/Deck and says
   "No macOS". Android would need a new ADR. Does it come before or after the
   native Linux build?
2. Shader delivery under ADR-0012: PC-converted and pushed over adb, or
   XenosRecomp and DXC built for arm64 and run on the phone? Bundling a
   converted pack, as SVR-PC does, breaks ADR-0012.
3. What Vulkan feature set does the emulator's host-GPU mode expose on this
   PC: BDA, descriptor indexing, BC, `shaderInt64`? Run `vulkaninfo`, or our
   device report, in an x86_64 AVD.
4. Does the stock ReXGlue `MapViews` succeed on Android's 39-bit address
   space starting at `1<<32`, and with `ASharedMemory` at 4.5 GB? Check in the
   emulator first.
5. Upstream or local fix for the 16 KB `REX_PHYS_HOST_OFFSET` issue (§1.3)?
   Desktop arm64 Linux kernels with 16K or 64K pages (for example Asahi) would
   hit it too, so it may be worth an upstream PR.
6. Mali support: day one, or Adreno only at first, as SVR-PC did? Each Mali
   fallback (CPU BC decode, relaxed Emulated checks) is work that can only be
   tested remotely.
7. Do we bundle Turnip drivers? Mesa is MIT. The Turnip zips SVR-PC ships are
   third-party builds whose provenance would need checking.
8. Which testers, and with which devices? Ask issue #4's reporter (AYN) and
   the r/retroid poster whether they would run test builds and send logs.
9. Current Samsung Remote Test Lab terms (credits, adb access): the official
   pages didn't render for this note.
10. Online play (ADR-0021) on mobile networks and over the relay is untested.
    Is it in scope for a first Android build?
