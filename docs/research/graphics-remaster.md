# Graphics: how close can we get to a remaster look?

Date 2026-10-24. Owner's question: "Is there any possibility for us to improve
the graphics of this, what can we do to be close to a remaster look?" This
note maps what the port and its runtime already do, what each further lever
would cost, and in what order to build them. It decides nothing. No code,
asset, ISO or GPU setting was changed, and the game was not run.

Tags: **[S]** this repo or the SDK (file:line). **[B]** the TU2 binary or the
game's data, read with Python on `analysis/xex/tu2.pe` and on decompressed
packages (the Ghidra project was locked by another session). **[P]** a
primary external source (linked). **[I]** my inference. **[U]** unverified.
`SDK:` is `.tools/rexglue-source` (`f5337cdc` plus `patches/`). Repo HEAD is
`729b45d`. Every guest address is TU2 (ADR-0022).

## Answer

- **A real remaster look is within reach, but most of it is not here yet.**
  The port today is a very clean 720p-era image: 2x2 supersampling, CAS, 4x
  anisotropic filtering, 60/120 fps. A remaster shows four further things, in
  this order of how much players notice them: (1) a sharp image at the
  display's resolution with clean edges, (2) shadows and textures that hold up
  at that resolution, (3) modern post-processing (tonemapping, ambient
  occlusion, better bloom/DOF), and (4) better assets. All four are possible.
  Only (1) is cheap.
- **The biggest gap is cheap to fix.** With Supersampling On (the default),
  Native renders 2560x1440 and filters it **down to 1280x720**. The presenter
  then scales that 720p frame **up** to the window
  (`src/native/native_renderer.cpp:2730-2740`, `guest_decode.h:558-568`). On
  a 1440p or 4K monitor, the default picture is therefore a soft 720p.
  "Render resolution" gives 1440p or 2160p, but with no AA, because the two
  modes are never combined (`native_renderer.cpp:1432-1438`). The first job
  is one mode that renders above the display's resolution and filters down
  to it, not to 720p. That is a Native-only change of a few days, and it
  needs no RE.
- **The game's own config has quality knobs, and the existing mod system can
  already reach them.** TU2's `Xe-Engine.ini` sets
  `MaxShadowResolution=432`, `ShadowFilterRadius=2`, `BranchingPCFQuality=0`,
  a 62 MB texture streaming pool and per-group `TEXTUREGROUP_*` LOD settings.
  The engine really reads these keys: the names are in the XEX or in the
  cooked `Engine` package. The `[D3DDrv.D3DRenderDevice]` block
  (`ShadowBufferResolution`, `LevelOfAnisotropy`) is PC-only dead config. A
  `config/Xe-Engine.ini` mod (`docs/modding.md:135-139`) can change the live
  keys with no code. Whether higher values hold on Xenon memory budgets is
  [U]. This is the cheapest experiment in the note.
- **Host-side post-processing is the main medium-term lever.** Native owns
  every render target, sees every draw's shader hash, and has the game's
  depth resolve (`k_24_8_FLOAT`). That is enough to insert our own passes:
  SMAA, tonemapping or colour grading, SSAO, and later a better bloom. The
  pass goes after the 3D composite and before the UI. The hard part is
  finding that point robustly, plus the projection parameters for SSAO. The
  SDK's "FSR2/FSR3" presets are not real temporal upscalers: they feed the
  colour image as depth and motion vectors and reset every frame
  (`SDK:src/ui/vulkan/vulkan_presenter.cpp:377-413`).
- **Assets: texture packs are feasible on Native; models stay a community
  package job.** Native already hashes every guest texture with XXH3
  (`textures.cpp:466-475`) and can dump textures as DDS
  (`wwe_native_dump_texture`, `textures.cpp:45-48`). A
  dump → AI-upscale → BC7 → load-by-hash pipeline is the standard emulator
  route (Dolphin, PCSX2, Xenia forks). ADR-0001 and the ROADMAP mark HD
  texture packs and ultrawide "out of scope". They need an ADR amendment if
  the owner wants them now.
- **ReShade works on any Vulkan app and is a zero-code stopgap for players.**
  It also paints over the ImGui overlays, and its depth access is unlikely to
  find the game's depth. That depth lives in Native's off-screen targets, not
  in the swapchain [I]. Document it; don't build on it.
- **Recommended order:** quick wins (display-resolution SSAA, a
  shadow/texture `Xe-Engine.ini` experiment, the Graphics preset
  "Ultra") → medium (SMAA or forced MSAA, host tonemap/grade, LOD bias tied to
  scale, texture replacement) → deep (SSAO, a better bloom/DOF, wrestler LOD
  forcing, ultrawide, TAA). Details at the end.

## 1. What the runtime offers today

### 1.1 Native (the default renderer)

| Setting (cvar) | Default | Where | Exposed |
|---|---|---|---|
| `wwe_native_supersample` | **true** | `src/settings_cvars.cpp:12-15`; `guest_decode.h:558-568` (only 1280x720 targets ×2); downsample blit `native_renderer.cpp:2730-2740` | In-game page and launcher, "Supersampling" (`src/settings/registry.h:104-107`) |
| `wwe_native_resolution_scale` 1-3 | 1 | `settings_cvars.cpp:29-33`; every target ×s, including bloom and depth (`native_renderer.cpp:913-918`) | "Render resolution" 720p/1440p/2160p (`registry.h:111-115`) |
| `wwe_native_cas` 0-1 | 0.5 | `settings_cvars.cpp:25-28`; drives the SDK presenter's CAS (`native_renderer.cpp:1344-1360`), live | "Sharpening" (`registry.h:108-110`) |
| `wwe_native_anisotropy` -1..5 | 3 (4x) | `settings_cvars.cpp:36-39`; `textures.cpp:375` | "Texture filtering" Off..16x (`registry.h:116-119`) |
| `wwe_native_color_mode` | 0 Original (Xenos PWL) | `settings_cvars.cpp:18-22`; `color-palette-original-vs-enhanced.md` | "Colour" Original/Natural (`registry.h:120-123`) |
| `wwe_native_lod_bias` | **-0.5** | `textures.cpp:42-44`, applied `textures.cpp:368` (clamped ±2) | Not exposed |
| MSAA | none | single-sample images and pipelines (`native_renderer.cpp:534, 951`; `pipelines.cpp:354`; `textures.cpp:216`) | n/a |

- The Graphics preset groups four of these: Low, Medium and High
  (`registry.h:21-27`). High is the defaults. Render resolution above 720p is
  never part of a preset.
- Supersample and resolution scale exclude each other. The scale wins and
  supersampling is switched off with a warning (`native_renderer.cpp:1432-1438`).
- **Consequence [I]:** on a 1440p display, "High" shows 2x2-SSAA 720p,
  upscaled by the presenter. "1440p" shows a native 1440p image with no AA.
  Neither is what a remaster shows: native-resolution output with AA.
- GPU cost has large headroom on desktops. On the owner's RX 7900 XT, SSAA
  takes a median 1.6-2.3 ms per frame and scale 3 (4K) takes 3.1-3.4 ms,
  against a 16.7 ms budget
  (`docs/research/performance-and-low-end-hardware.md`, C.1). The Deck is the
  constraint (ADR-0006), not the desktop.

### 1.2 SDK presenter (both renderers)

- `present_effect`: `bilinear | cas | fsr | fsr2 | fsr3`
  (`SDK:src/ui/presenter.cpp:50-80`). The build enables FidelityFX when
  `.tools/fidelityfx-sdk` exists (`scripts/build.ps1:104-105`).
  - `fsr` is FSR1 (EASU+RCAS), a spatial upscaler. AMD requires
    anti-aliased input and wants it applied before the UI
    ([FSR1 docs](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-spatial/)).
    Here it runs on the final frame, UI included.
  - `fsr2`/`fsr3` are **not temporal in practice.** Colour is passed as depth
    and as motion vectors, jitter is 0, and `reset = true` on every dispatch
    (`SDK:src/ui/vulkan/vulkan_presenter.cpp:377-413`). The SDK warns that it
    is "an experimental temporal upscaler path with synthesized depth/motion
    inputs" (`SDK:src/ui/presenter.cpp:280-285`). Do not expose these.
- `swap_post_effect`: `none | fxaa | fxaa_extreme`
  (`SDK:src/graphics/graphics_system.cpp:52-54`). Runs on the final frame, so
  it softens Scaleform text (`native-image-quality.md` §B).
- `present_letterbox` (default on) and `present_safe_area_x/y`
  (`SDK:src/ui/presenter.cpp:38-48`). `present_dither` (off) adds output
  dithering (`presenter.cpp:86`). It is a cheap anti-banding option for
  dark arena gradients [I].

### 1.3 Emulated (the Xenia-style path)

- `resolution_scale` / `draw_resolution_scale_x|y` 1-8
  (`SDK:src/graphics/pipeline/texture/cache.cpp:66-78`). They are wired into
  the Vulkan render-target cache, texture cache and SPIR-V translator (grep:
  `vulkan/render_target_cache.cpp`, `vulkan/texture_cache.cpp`,
  `spirv_translator_rb.cpp`).
- `anisotropic_override` (default 3, `cache.cpp:52-64`), `native_2x_msaa`
  (`SDK:src/graphics/flags.cpp:18`). The latter only matters when the guest
  asks for MSAA, and this game never does (`native-image-quality.md` §B).
- None of these is in the launcher, which is right: Emulated is the reference
  and fallback (ADR-0002, ADR-0020), and the oracle scene runs must stay at
  1x with no effects.

### 1.4 What the game itself asks for

The render targets created in a match (`analysis/session-sync-nw04busy1-A/runtime.log`):

| Target | Host format | Likely use |
|---|---|---|
| 1280x720 colour ×2, depth ×3 | RGBA8 / sRGB view; D24S8-class | scene, UI, depth (shadow depths are rendered into a depth surface too [I]) |
| 400x182 colour | `R16G16B16A16_SFLOAT` | HDR bloom chain (`native-image-quality.md` §A) |
| 560x512, 320x256 colour | RGBA8 | [U] reflections, attire/portrait bakes or DOF |

- `RB_SURFACE_INFO` MSAA bits are 0 in every trace, so the game renders at 1x
  (`native-image-quality.md` §B).
- The engine's post chain is `DefaultPostProcessName=GameResources.PostProcess.DOFBloom`
  (TU2 `Xe-Engine.ini`, `[Engine.Engine]`). The cooked `Engine` package has
  `DOFAndBloomEffect`, `MotionBlurEffect`, `UberPostProcessEffect`,
  `MaterialEffect` and `BlurEffect`. It has **no AmbientOcclusionEffect** and
  no tonemapper class [B]. AO, if wanted, has to come from the host.

## 2. Render-target resolution scaling in EDRAM emulation

**How it works (Xenia, and our Emulated path).** The guest believes it
renders into 1280x720 surfaces in 10 MB of EDRAM. The host makes every
render target, resolve destination and resolved texture N× larger, and
scales viewports, scissors and resolve rectangles to match. The game never
sees the change, which is why Xenia calls it scaling "opaquely to the game"
([`texture_cache.cc`](https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_cache.cc);
copied in `SDK:src/graphics/pipeline/texture/cache.cpp:82-94`).

**Known artifacts (primary sources):**

- Xenia's own help text: "Various effects and parts of game rendering
  pipelines may work incorrectly as pixels become ambiguous from the game's
  perspective and because half-pixel offset (which normally doesn't affect
  coverage when MSAA isn't used) becomes full-pixel" (same file).
- **Half-pixel gaps at resolve edges.** Xenia stretches "the first surely
  covered host pixel in the left and top sides of render target resolve
  areas to eliminate the gap caused by the half-pixel offset (this is
  necessary for certain games to display the scene graphics)"
  ([`draw_util.cc`](https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/draw_util.cc);
  `SDK:src/graphics/util/draw.cpp:31-41`).
- **Texel-offset fetches.** Post effects that sample with fixed texel
  offsets (blur kernels, DOF, bloom downsamples) would sample N times
  closer. `draw_resolution_scaled_texture_offsets` corrects this, but only
  in the DXBC translator (`SDK:src/graphics/pipeline/shader/dxbc_translator_fetch.cpp:27, 885`).
  I found no Vulkan/SPIR-V counterpart [S].
- **CPU readbacks** of resolved data come back at guest size or need
  downscaling. This game reads none (`native-renderer.md`, ticket 06), so
  this risk is small here.
- The Canary wiki only says: "This is a hack, and as such, **bugs should be
  expected when changed from the default**", with 1x-7x per axis
  ([Options wiki](https://github.com/xenia-canary/xenia-canary/wiki/Options)).

**How Native differs.** Native is not EDRAM emulation. It keys host images by
EDRAM base, pitch and format, so the size of each image is our choice:

- **Supersample mode** doubles only 1280x720 targets, so bloom (400x182),
  shadow depths in other surfaces and bakes keep their size. This is the
  choice made in `.scratch/image-quality/spec.md` (stories 15-17), so effects
  "that assume their size keep working".
- **Resolution scale** multiplies every target, bloom and depth included
  (`native_renderer.cpp:913-918`). That raises shadow-depth resolution for
  free. It also changes the bloom chain's texel-to-UV ratio, so the blur
  kernels skip texels, which can look grainy or ringed. [U] Check bloom and
  DOF at scale 3 against scale 1 (frame dumps).
- The half-pixel offset scales per guest pixel (`guest_decode.h:402-427`,
  `native-image-quality.md` §B), so Native avoids Xenia's resolve-edge gap.

## 3. Game-side improvements through config and recompiled code

### 3.1 Config keys the engine really reads (no code)

What the binary and packages show [B]:

| Key (TU2 `Xe-Engine.ini`) | Value | Read by | Expected effect |
|---|---|---|---|
| `[Engine.Engine] MaxShadowResolution` | 432 | `Engine` package property (script config) | Cap on per-subject shadow depth size. UE3 docs: "max square dimensions (in texels) allowed for rendering shadow subject depths" ([UDK SystemSettings](https://docs.unrealengine.com/udk/Three/SystemSettings.html)) |
| `MinShadowResolution` 32, `ShadowFilterRadius` 2, `bEnableBranchingPCFShadows` True, `BranchingPCFQuality` 0, `DepthBias` .012 | | `Engine` package | Softer or higher-quality PCF; `BranchingPCFQuality` 0 is the lowest [I] |
| `[TextureLODSettings] TEXTUREGROUP_*=(MinLODSize, MaxLODSize, LODBias)` | 256 / 4096 / 0 | XEX (UTF-16 `MinLODSize=` etc. at `0x821370C8`, group names at `0x82137408`) | Negative LODBias keeps higher mips; 4096 already allows full size |
| `[TextureStreaming] PoolSize` 62, `SecondaryPoolSize` 138 | MB | XEX (UTF-16 at `0x82198C52`) | A larger pool keeps top mips resident (less blur on close-ups) |
| `[Engine.Engine] MinStreamedInMips` 9 / `MaxStreamedInMips` 14 | | not found in XEX or `Engine` [U] | unknown |
| `[Engine.Client] DisplayGamma` 2.5 | | XEX (UTF-16 at `0x8215C5A4`) | Gamma; already handled by the colour work |
| `[D3DDrv.D3DRenderDevice] ShadowBufferResolution`, `LevelOfAnisotropy`, `UseTrilinear` | | **no reference** in the XEX or `Engine` | Dead PC config; changing it does nothing [I] |

- Method: a byte search of `analysis/xex/tu2.pe` (ASCII and UTF-16), plus a
  scratch LZO1X unpack of `CookedXenon/Engine.xxx`. The unpacker is a port
  of `launcher/src/Launcher.Core/Lzo1x.cs` and is not committed.
- **Delivery already exists.** A `config/Xe-Engine.ini` mod edits these keys
  (`docs/modding.md:135-160`). A "graphics config" mod is a zero-code
  experiment the owner can run today.
- **Risks [U]:**
  - On the 360, shadow depths share EDRAM and the 512 MB guest heap. Values
    above the depth surface the game allocates (1280x720 here) may clip or
    corrupt shadows.
  - A larger streaming pool can run the guest heap out of memory in 4-player
    matches or Create-a-Superstar.
  - Each value needs an owner check in a 4-player match and in CAS.
- How other ports did it: wwe-svr2011-pc zeroes fields of the game's settings
  struct for its DOF, motion-blur and soft-filter toggles
  (`svr2011-pc-port.md` §2, "Post-effect toggles"). Unleashed Recompiled
  exposes `ShadowResolution` (default x4096), `DepthOfFieldQuality`,
  `MotionBlur`, `GITextureFiltering` and `TransparencyAntiAliasing` as
  user options
  ([`config_def.h`](https://github.com/hedge-dev/UnleashedRecomp/blob/main/UnleashedRecomp/user/config_def.h)).
  Its README notes "Multiple optimizations were made to the game's shadow map
  rendering" and "MSAA levels beyond the 2x used by the original game"
  ([README](https://github.com/hedge-dev/UnleashedRecomp)).

### 3.2 What needs RE in the TU2 code

| Target | What to find | Effort | Notes |
|---|---|---|---|
| Shadow map size beyond config | The UE3 function that sizes the shadow depth render target (reads `MaxShadowResolution`), plus the surface it uses | M | Native can allocate any size. The guest-side resolve texture must fit too |
| Wrestler and crowd LOD | `USkeletalMeshComponent` LOD choice (screen-size based; the engine thinks the screen is 720p) and the crowd system | M-L | Forcing LOD0 for wrestlers is the classic "remaster" fix. The crowd is probably sprite or impostor based [U] |
| DOF, bloom, motion blur parameters | The `DOFBloom` chain objects (in a seek-free startup package; `GameResources.xxx` is a 32 KB stub) and `UberPostProcessEffect` fields | M | Toggles in the svr2011-pc style. Tuning bloom scale and DOF radius for higher resolutions |
| Mip bias by group | Already config (3.1) | S | Prefer config over code |
| Aspect / FOV for ultrawide | The projection constant, the HUD's Scaleform stage and the culling | L | svr2011-pc did it: aspect constant, HUD heuristics, widened targets (`svr2011-pc-port.md` §2). Out of scope by ADR-0001 |

Every hook goes through `[[midasm_hook]]`/`REX_HOOK` and manifest seeds, with
instruction-level evidence (CLAUDE.md). Ghidra (`WWEAllStarsTU2`) is the tool.
It was locked during this research, so no function addresses are proposed
here.

## 4. Shader-level enhancements

### 4.1 Replacing or augmenting translated shaders

- Native runs XenosRecomp output as SPIR-V from a shader pack, keyed by the
  container's XXH3 (`src/native/shader_pack.h:1-40`). At draw time it knows
  each draw's VS/PS hash (`wwe_native_trace_ps`, `wwe_native_debug_skip_ps`;
  `native_renderer.cpp:99-103`).
- **A per-hash override** (load `overrides/<hash>.spv` in place of the pack
  entry) is therefore a small renderer change. Uses:
  - fix banding with higher-precision maths;
  - better PCF (more taps, a Poisson disk) in the shadow-projection pixel
    shaders;
  - a better skin or specular term on wrestler materials.
- Limits:
  - Overrides must be **our own code**: game-derived shader source cannot
    ship (ADR-0012). An override could be a patch applied to the player's
    locally converted HLSL at setup (XenosRecomp already runs on the
    player's machine).
  - UE3 builds hundreds of material permutations (816 PS on the main menu
    alone, `native-renderer.md`). Hand edits only scale to a few shared
    shaders: shadow projection, bloom, DOF, the composite.

### 4.2 Injecting host post-process passes

| Pass | Inputs it needs | Available? | Payoff |
|---|---|---|---|
| SMAA 1x / CMAA2 | Scene colour before the UI | Yes, once the "pre-UI" point is found | High at native resolution; cheap (<0.5 ms at 1440p [I]) |
| Tonemap or colour grade (for example ACES-like or a LUT) | HDR scene colour | Partly: bloom is HDR (`R16G16B16A16_SFLOAT`), but the main scene is RGBA8 with an sRGB/PWL view, so it is already tonemapped by the game | Medium; a LUT grade is cheap and safe |
| SSAO / GTAO | Depth plus projection (near, far, FOV) | Depth yes: the game resolves `k_24_8_FLOAT` depth for its own post (`native-image-quality.md` §A). Projection: read from VS constants (c0-c3 hold a view-projection, `native-renderer.md`) [U] | **High**: arenas and wrestlers look "flat" without AO |
| Better bloom | The bloom chain's targets | Yes (400x182 target known) | Medium |
| Sharpening | Final frame | Already CAS | Done |
| TAA / FSR2 / DLSS / XeSS | Jitter in every VS, motion vectors | No motion vectors in the 1x scene. UE3's velocity buffer exists only for motion blur, if this game renders it at all [U] | Highest AA quality, high risk; see §7 |

- **Where to insert.** UI is Scaleform drawn into the same 1280x720 surface
  through the non-gamma view (`000002D0`) after the 3D draws, which use the
  `_GAMMA` view (`000102D0`) (`native-image-quality.md` §A table). The first
  draw that switches from the gamma view to the non-gamma view after the
  bloom composite is a candidate "end of 3D" marker [I]. A more robust
  marker is the composite pixel shader's hash, recorded once by trace
  (`wwe_native_trace_frame`).
- Check the match HUD (UE3 Canvas `GameHUD`) as well as menus: the HUD may
  not be Scaleform.
- **Libraries:**
  - [SMAA](https://github.com/iryoku/smaa) (MIT);
  - [CMAA2](https://github.com/GameTechDev/CMAA2) (Apache-2.0);
  - FidelityFX CACAO for AO, which is in the FidelityFX SDK the build already
    vendors
    ([CACAO docs](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/combined-adaptive-compute-ambient-occlusion/)) [U: check
    it is in the vendored SDK version].
- The oracle constraint applies. Every pass is an Enhancement and must be off
  in scene runs (ADR-0002; `.scratch/image-quality/issues/01-enhancements-report-and-scene-guard.md`).

## 5. Asset level

### 5.1 Textures

- **Today (1.4.0):** texture changes only through package mods, built with
  community tools. umodel reads and exports; writing packages is
  "not solved anywhere public" (`modding.md` §3.5). One Reddit modder ships a
  re-textured attire anyway (`dlc-mods.md`).
- **Native texture replacement** (`modding.md` §3.3; not built):
  1. **Dump:** `wwe_native_dump_texture=1` already writes every uploaded
     texture as DDS (`textures.cpp:45-48`). A pack mode would name files by
     the XXH3 key (`textures.cpp:466-475`).
  2. **Upscale:** the usual workflow is an ESRGAN-family model per texture
     class (diffuse, normal map, UI) in chaiNNer or Real-ESRGAN, then
     manual fixes. Normal maps need a normal-aware model or
     renormalisation. Alpha-tested hair needs its alpha handled separately.
  3. **Encode:** BC7 for colour, BC5 for normal maps (the host can sample
     formats the 360 lacked), with full mip chains.
  4. **Load:** by key, with the host image at the larger size. Native
     already owns the host image size.
- **Risks:**
  - UE3 streams mips, so keys must survive the base level changing as mips
    arrive. Key on the smallest resident mip's hash, or on the mip-tail
    hash [I].
  - CAP textures are composed at runtime, so they cannot be replaced by key.
  - The VRAM cost of a full 4x pack.
  - Emulated would need its own SDK patch.
- **Licence:** upscaled game textures are game-derived. The project ships
  the loader and the tools, and packs come from the community. The same
  stance as the shader pack (ADR-0012) [I].
- **Scope:** "HD texture packs are out" (ADR-0001; `docs/ROADMAP.md:62`).
  Needs an amendment before work starts.

### 5.2 Models

- Mesh replacement only works through packages: new `SkeletalMesh` exports,
  with the skeleton and anim compatibility the game's animation sets expect.
  Writing such packages is unsolved (`modding.md` §3.5). A host-side mesh
  swap (hash vertex buffers, substitute) is impractical: skinned meshes are
  posed on the GPU through bone constants, so a replacement would need the
  same bone palette and vertex layout [I].
- Practical limit: the community edits meshes in packages; the port adds
  nothing here beyond the existing package overlay.

## 6. External: ReShade

- ReShade "supports all of Direct3D 9, Direct3D 10, Direct3D 11, Direct3D 12,
  OpenGL and Vulkan" and offers "an automated and generic way to access both
  frame color and depth information", with depth "automatically disabled
  during multiplayer" ([reshade.me](https://reshade.me/)). On Vulkan it loads
  as a layer [U: the README does not describe the mechanism].
- **Caveats here:**
  - It runs on the swapchain image, after the SDK presenter, so it also
    filters the ImGui overlays (settings page, achievements) and the
    Scaleform UI [I].
  - Depth: the swapchain has no depth. Native's depth targets are off-screen
    images that ReShade's generic depth heuristics may or may not pick up
    [U].
  - Online play (Quazal traffic) likely trips the multiplayer check and
    switches depth off [I].
  - Crash reports (ADR-0025) will include ReShade-induced GPU failures. The
    crash report should note an active layer [I].
- **Recommendation:** document it as "use at your own risk" in the README or
  FAQ. Don't ship or depend on it. Payoff: medium for players who like
  presets. Effort for us: about zero.

## 7. Options ranked

Payoff is the visual effect at 1440p/4K. Effort is S (days), M (1-2 weeks)
or L (weeks or more).

| # | Option | Payoff | Effort | Risk | Verify in game |
|---|---|---|---|---|---|
| 1 | **Display-resolution SSAA:** render at scale s (2-4) and filter down to the window size (not 720p) with a proper downsample filter (Lanczos, or box at integer ratios); merge Supersampling and Render resolution into one "Render resolution + AA" choice | **Very high** | S | Low: the scale path exists; bloom/DOF at scale needs a look | Text crispness, bloom/DOF at scale 2-3, GPU time (PresentMon `GPUBusy`), Deck stays at the Low preset |
| 2 | **`Xe-Engine.ini` quality mod:** shadow resolution/PCF, texture pool, LOD bias | High if it holds | S | Medium: guest memory, EDRAM-era limits | 4-player match, CAS, Path of Champions; shadow acne/clipping; long-session memory |
| 3 | **"Ultra" preset + LOD bias tied to scale** (`wwe_native_lod_bias` -0.5 at 1x is shimmer; at 2x+ it can go to -1) | Medium | S | Low | Crowd and floor shimmer in motion |
| 4 | **SMAA/CMAA2 pre-UI pass** | High at 1x/scale without SSAA | M | Medium: pre-UI detection | Edges vs. UI text; no smearing of HUD |
| 5 | **Forced 4x MSAA** on the main scene (+ alpha to coverage) | High, cheap on the Deck | M | Medium: resolves, depth resolve (`native-image-quality.md` §B) | Hair, ropes, crowd; resolve correctness |
| 6 | **Host colour grade / tonemap LUT** | Medium | S-M | Low (off by default) | Owner taste; Original colour stays the reference |
| 7 | **Native texture replacement** (dump/load by key) + docs for AI upscaling | High (with community packs) | M-L | Medium: mip streaming keys | Pack loads, no pop-in, VRAM |
| 8 | **SSAO (CACAO/GTAO)** from the game's depth | High | M-L | Medium-high: projection, pre-UI point, transparent draws | Halos around hair, crowd, ring ropes |
| 9 | **Shadow-projection shader override** (more PCF taps) and a shadow size hook | Medium-high | M | Medium | Shadow stability in motion |
| 10 | **Wrestler LOD0 forcing** | Medium (wide shots) | M | Medium: RE | Wide camera in Royal Rumble-like scenes, entrances |
| 11 | **Better bloom/DOF** (host replacement of the chain) | Medium | L | High: it is the game's look | Matches against Emulated |
| 12 | **Ultrawide** | Medium for some players | L | High: HUD, culling, cutscenes | Out of scope (ADR-0001) |
| 13 | **TAA / real FSR2-3** | Highest AA | L | High: jitter in every VS, motion vectors the game lacks | Ghosting on fast moves; probably not worth it next to 1+5 |
| — | ReShade (document only) | Medium (player-driven) | ~0 | Low for us | — |

## 8. Recommended phased plan

**Phase 1: quick wins (1-2 weeks, no RE)**

1. A spike on display-resolution SSAA. Let `wwe_native_resolution_scale`
   go to 4, and add an "output at window size" path:
   - downsample Native's frame to the swapchain size in Native, with a
     filter chosen for the ratio, before `Publish`
     (`native_renderer.cpp:2730-2805`);
   - fold it into the Graphics preset as "Ultra" (scale 2-3 + downsample +
     CAS 0.25 + 16x AF).

   Measure GPU time at 1440p and 4K.
2. An owner-run config experiment with a `config/Xe-Engine.ini` mod:
   `MaxShadowResolution` 432 → 720, `BranchingPCFQuality` 0 → 2,
   `PoolSize` 62 → 96, `TEXTUREGROUP_Character` `LODBias` -1. Check one change
   at a time, in a 4-player match and in CAS. If it holds, ship it as a
   built-in "Enhanced config" toggle or a documented example mod.
3. Tie `wwe_native_lod_bias` to the effective scale (0 at 1x, -0.5 at 2x,
   -1 at 3x+). Add `present_dither` to the Ultra preset if banding shows.
4. A README/FAQ note on ReShade (caveats from §6).

**Phase 2: medium (weeks)**

5. Trace one match frame to pin down the pre-UI point (the composite PS hash
   and the gamma→non-gamma view switch). Add the hook point for host passes.
6. SMAA (or CMAA2) as the AA for scale ≥ 2 without SSAA. Forced 4x MSAA as
   the Deck/low preset's AA.
7. A colour-grade LUT pass (off by default; Original stays the reference).
8. An ADR amendment to ADR-0001 for texture packs. Then Native texture
   replacement: dump mode by key, a loader, a launcher "Texture packs" mod
   type, and a short upscaling guide.

**Phase 3: deep work (RE-driven)**

9. SSAO from the game's depth, with projection read from VS constants.
10. Shadow map size and filter: a hook on the shadow depth allocation (Ghidra
    on TU2, manifest seeds with evidence), plus a shadow-projection shader
    override.
11. Wrestler LOD forcing, and a look at the crowd system.
12. Bloom/DOF retune at high resolutions. Ultrawide and TAA only if the owner
    widens scope.

Every enhancement stays a cvar that the scene runner forces off (ADR-0002),
and the Deck keeps a Low preset (ADR-0006).

## Open questions

- Do shadow depths render inside a 1280x720 depth surface (which caps useful
  `MaxShadowResolution` near 720), or into their own surface? Trace a match
  frame's shadow pass.
- What are the 560x512 and 320x256 RGBA8 targets?
- Does the game render a velocity buffer (`MotionBlurEffect` is cooked, but
  is it in the `DOFBloom` chain)? Find the chain in the seek-free startup
  package.
- What size are wrestler textures on disc? An umodel export, or the Native
  DDS dump, shows whether full mips are already 1024-2048 (then the pool and
  LOD config is the win) or 512 (then a texture pack is the win).
- Is FidelityFX CACAO in the vendored `.tools/fidelityfx-sdk` version?

## Sources

- This repo:
  - `src/settings_cvars.cpp`, `src/settings/registry.h`,
    `src/native/native_renderer.cpp`, `src/native/textures.cpp`,
    `src/native/guest_decode.h`, `src/native/pipelines.cpp`,
    `src/native/shader_pack.h`, `scripts/build.ps1`;
  - `docs/modding.md`, `docs/ROADMAP.md`, ADR-0001/0002/0006/0012/0015/0020;
  - research notes `native-image-quality.md`, `native-renderer.md`,
    `color-palette-original-vs-enhanced.md`,
    `performance-and-low-end-hardware.md`, `svr2011-pc-port.md`,
    `modding.md`, `dlc-mods.md`;
  - `.scratch/image-quality/spec.md`;
  - `analysis/session-sync-nw04busy1-A/runtime.log`.
- SDK: `src/ui/presenter.cpp`, `src/ui/vulkan/vulkan_presenter.cpp`,
  `src/graphics/graphics_system.cpp`, `src/graphics/flags.cpp`,
  `src/graphics/pipeline/texture/cache.cpp`, `src/graphics/util/draw.cpp`,
  `src/graphics/pipeline/shader/dxbc_translator_fetch.cpp`.
- Game data (read only): TU2 `updatedata/Coalesced.ini` (inner
  `Xe-Engine.ini`), `assets/Game/CookedXenon/Engine.xxx` (decompressed in the
  scratchpad), `analysis/xex/tu2.pe`.
- External:
  - Xenia [`texture_cache.cc`](https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_cache.cc),
    [`draw_util.cc`](https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/draw_util.cc);
  - Xenia Canary [Options wiki](https://github.com/xenia-canary/xenia-canary/wiki/Options);
  - Unleashed Recompiled [`config_def.h`](https://github.com/hedge-dev/UnleashedRecomp/blob/main/UnleashedRecomp/user/config_def.h)
    and [README](https://github.com/hedge-dev/UnleashedRecomp);
  - [UDK SystemSettings](https://docs.unrealengine.com/udk/Three/SystemSettings.html)
    (quoted through a search excerpt; the page returned 403 to a direct
    fetch);
  - [AMD FSR1](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-spatial/);
  - [ReShade](https://reshade.me/);
  - [SMAA](https://github.com/iryoku/smaa), [CMAA2](https://github.com/GameTechDev/CMAA2).
