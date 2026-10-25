# WWE All Stars Recomp

A static recompilation of the Xbox 360 game WWE All Stars that runs natively on PC.

## Language

### Renderers

**Emulated**:
The renderer that emulates the Xbox 360 GPU (Xenos), inherited from the ReXGlue SDK. Serves as the reference for Native, and as the player's alternative when Native is not wanted. Formerly **Classic** (ADR-0020); older tickets and ADRs, and the oracle's stored names, still say Classic.
_Avoid_: Classic (in new text), Xenos renderer, old renderer

**Native**:
The renderer that intercepts the game's own D3D9 calls and draws them directly with Vulkan. The default (ADR-0020).
_Avoid_: new renderer, D3D renderer, hook renderer

**Oracle frame**:
The Emulated frames at a given frame number, from several runs of the same scene, used as the expected image when judging a Native frame at that frame number.
_Avoid_: golden image, reference shot

**Emulated envelope**:
The per-pixel judgement against an oracle frame: a Native pixel is wrong only when it differs from every Emulated run. Formerly the Classic envelope. It absorbs run-to-run animation phase.
_Avoid_: tolerance band, noise mask

**Artifact**:
A visible difference in a Native frame that the oracle frame and the Xbox 360 do not show (wrong geometry, missing or corrupt textures, wrong colours, flicker, holes in models).
_Avoid_: glitch, bug (when meaning a visual difference)

**Draw skip**:
Native drops a single draw it cannot reproduce and counts it under a named reason. Any draw skip fails a scene.
_Avoid_: dropped draw, missing draw

**Renderer fallback**:
A session that was asked to run on Native runs on Emulated instead, because Native could not start or its shader pack is missing, damaged, of another format version or stale. The log names the reason. A renderer fallback fails a scene.
_Avoid_: emulation fallback, safe mode

**GPU failure**:
Native was running and the GPU or driver stopped working under it. The game ends the session with the reason logged and does not try to recover; the launcher offers Emulated for the next start. It is neither a renderer fallback (the session did not go on to run on Emulated) nor a crash (the game ended it on purpose).
_Avoid_: device lost (one cause, not the term), GPU crash, renderer crash

**Fault finder**:
A Native debug setting a player turns on when asked: for the first few frames that draw, the game submits and waits after every draw, so a GPU failure names the last draw that finished and the draw it failed in, in the log and the crash report. Off by default; it slows those frames down a lot.
_Avoid_: draw tracer, GPU debugger

### Support

**Crash report**:
One file the player attaches to a bug report, made on their PC after the game crashes, the game ends a session with a GPU failure, or the launcher crashes. It holds what the developer needs to find the cause (logs, settings, the machine's GPU and driver, and a dump of the crashed process). It never leaves the PC on its own: the player hands it over.
_Avoid_: crash dump (that's one part of it), telemetry, error report

**Shader pack**:
The game's shaders converted for Native on the player's machine, in one file in the cache folder that the game loads at startup. It records the XenosRecomp patch it was converted with; a pack from another patch is stale.
_Avoid_: shader cache (the SDK's own), shader library (when meaning the file)

**Shader top-up**:
Shader containers the game compiles at runtime and so are not on the disc (six known, such as the autosave busy icon). The game leaves any that its shader pack lacks in `cache/shader-topup` and converts them live with XenosRecomp (zero-stutter ticket 22): each becomes a one-shader pack beside its container, used in that session as soon as it is ready and merged into the pack at the next start. A top-up that could not be converted live is added by the launcher the next time it prepares shaders.
_Avoid_: capture, dump (those are the developer's `--wwe_shader_dump_dir`)

**Enhancement**:
A PC-only option that goes beyond the Xbox 360: image options such as anti-aliasing, supersampling, sharpening or the Natural colour curve (Native only), and gameplay feel such as enhanced vibration. Each is on or off by default as the owner decided; scene runs always turn enhancements off. "Better than Emulated" means no artifacts plus image enhancements on.
_Avoid_: improvement, mod, graphics option (when meaning this)

**Colour**:
The Native setting for the colour curve of the game's gamma surfaces. **Original**: the Xbox 360's piecewise-linear (PWL) curve, as in Xenia and Emulated: richer, deeper shadows. **Natural**: sRGB, softer with more detail in dark areas, and an enhancement. Original is the default (owner, 2026-10-10); Emulated is always Original. Applies at the next start.
_Avoid_: colour palette, gamma setting

**Render resolution**:
The Native setting for how many pixels the game draws: 720p, 1440p, 2160p or 2880p, which are 1x to 4x the game's own 720p. The picture is always fitted to the window: above the window's size it is filtered down to it, which gives anti-aliasing (supersampling); below it, it is scaled up and sharpened. It replaces the earlier separate Supersampling setting (owner, 2026-10-24).
_Avoid_: render scale, supersampling (as a separate setting), internal resolution

**Enhanced shadows & textures**:
An experimental enhancement that raises the game's own shadow and texture-streaming quality values. It is on in the Ultra preset only, applies only when the game is started from the launcher, and also applies in online play, because it changes only how the match looks. A player's config mod that sets the same values wins over it (owner, 2026-10-24). Not shipped: none of its candidate values changed the picture in the staged check (graphics-remaster ticket 03, 2026-10-24).
_Avoid_: HD shadows, graphics config mod

### Graphics presets

**Graphics preset**:
A named set of values for Native's image settings that the player picks in one step: **Low**, **Medium**, **High** or **Ultra**. High is the defaults; Ultra is only ever the player's own choice. The preset is never saved on its own: the game shows the preset whose values all match the current settings, and **Custom** when none does.
_Avoid_: quality level, graphics profile, low-end profile

**Suggested preset**:
The graphics preset the game proposes for a GPU from what kind of GPU it is: Low for a Steam Deck or another integrated GPU, Medium for a small discrete GPU, High otherwise or when unknown. It is applied at first setup and by Reset to defaults, and never over a choice the player has made.
_Avoid_: recommended settings, auto-detect

**Minimum spec**:
The weakest machine the port promises to run at the original speed: the Steam Deck, at its native resolution on the Low preset. Its bar is the zero-stutter match bar with guest work and GPU time per frame each under about 14 ms at p99.
_Avoid_: min requirements, low-end target

### Pipelines

**Pipeline store**:
The pipeline keys Native has met and the driver's pipeline cache, kept in `cache/native-pipelines` and tagged with the shader converter, the GPU, the driver version and the pipeline-cache UUID. Keys from another GPU or driver are kept; that driver cache is never loaded.
_Avoid_: pipeline cache (when meaning both files), shader cache

**Shipped key list**:
The pipeline keys recorded in the owner's sessions and shipped with the release (`data/pipeline-keys.txt`): shader hashes and render state only, no shader code or game data. Native builds every key on it before play, with the player's own stored keys.
_Avoid_: pipeline list, PSO list

**Prepare (pipelines)**:
The game's headless mode that builds the shader modules and every planned pipeline on all cores, writes the pipeline store and exits. The launcher runs it after the shader pack at setup, after Repair, and before Play whenever its rebuild rule asks.
_Avoid_: warm-up, precompile run

**Pipeline library / fast link**:
On GPUs with graphics pipeline libraries, Native builds each shader ahead of play as a library per spec-constant value, and a pipeline it has not built yet is fast-linked from libraries on the render thread instead of skipped or compiled. The fully optimized pipeline replaces the fast-linked one when a worker has it.
_Avoid_: GPL path (in prose), partial pipeline

**Stale marker**:
The `pipelines-stale` file the game writes beside the pipeline store when it starts on another GPU, driver or pipeline-cache UUID than the store's. The launcher rebuilds the pipelines before the next Play without a click, then removes it.
_Avoid_: dirty flag, invalidation file

### Verification

**Scene**:
A reproducible stretch of play reached by scripted input from boot, with the frame numbers to capture and the checks to run on them.
_Avoid_: test case, level, scenario

**Playable build**:
A build in which a 1v1 Exhibition match can be played from boot to the result screen on Native at the original frame rate, with no artifacts against the oracle and the owner's visual sign-off.
_Avoid_: working build, beta

### Smoothness

**Hitch**:
A single frame that takes much longer than one 16.7 ms game step, so it shows as a spike in a frame-time graph.
_Avoid_: stutter, lag spike, freeze

**Judder**:
Uneven motion on screen while frames are still sent at an even pace, so a frame-time graph stays flat. It comes from when or which frames reach the display.
_Avoid_: stutter, microstutter, frame pacing issue

### Loading

**Pre-match load**:
The wait from confirming the wrestlers and arena to the match starting. It is the load the player sits through most often, and the one load-time work is judged on.
_Avoid_: loading time (unqualified), match load, load screen

**Skippable**:
Something the game shows that adds wait without being loading work, such as a logo, intro movie or loading-screen minimum time. Skipping one is the player's choice, never the default.
_Avoid_: padding, fake load

### Vibration

**Hit pulse**:
A short rumble on a player's pad when their wrestler takes a hit. The Xbox 360 game sends one only for aerial hits; the PC port can send one for every hit.
_Avoid_: hit rumble, damage vibration

**Pulse tier**:
The strength class of a hit pulse, decided by the move's combat type alone: light, strong, grab or weapon. Two wrestlers using the same move get the same pulse.
_Avoid_: intensity, hit strength

**Hit shape**:
The two-motor waveform of a pulse in Enhanced: a short crack weighted to the right (high-frequency) motor, then a fading body on the left (low-frequency) motor. Light hits are a crack only; critical health is a left-motor heartbeat.
_Avoid_: rumble pattern, envelope

**Big moment**:
A match event that deserves a stronger or longer rumble than a hit pulse: a finisher or extreme signature landing on you, or your wrestler dropping to critical health. In Enhanced it layers in the rumble mixer, so the hits that follow are felt over it.
_Avoid_: special event, highlight

**Mat impact**:
A rumble when a wrestler's body hits the mat (a slam, a drop in a hold, a landing), whether or not damage is applied at that moment, sized by the game's own impact type. The wrestler hitting the mat gets the impact; the other wrestler in the exchange gets a lighter echo.
_Avoid_: ground rumble, slam vibration

**Rumble mixer**:
The port's own layer for Enhanced rumble. Every event adds an effect for one pad, and each motor plays the strongest active effect (or the game's own request, if stronger). Effects run on real time, so slow motion never stretches them.
_Avoid_: rumble queue, haptics engine

**Attacker confirm**:
A light tap on your pad when your own hit lands, always lighter than what the same hit feels like to the wrestler taking it.
_Avoid_: hit feedback, attack rumble

**Vibration strength**:
The one setting for all pads that scales every rumble from 0 to 100 %, beside the vibration mode.
_Avoid_: rumble intensity, gain

**Vibration mode**:
The one setting for all pads that decides how much the game rumbles. **Off**: the pads never rumble. **Original**: exactly the Xbox 360 (a hit pulse for aerial hits only). **Enhanced**: a hit pulse for every hit from a move, aerial hits included, sized by its pulse tier, plus big moments, mat impacts, attacker confirms, reversals, finisher wind-ups and the match end, all layered in the rumble mixer. Enhanced is the default and an enhancement.
_Avoid_: rumble level, vibration on/off

### Online

**Online play**:
The game's own Xbox LIVE mode (Quick Match, Player Match, Ranked Match), working again against a game server instead of Xbox Live and THQ's servers. It is not a new netplay mode built around local multiplayer. Experimental, and off unless the player turns it on (ADR-0021).
_Avoid_: netplay, Xbox Live (when meaning ours), multiplayer (that's local)

**Game server**:
The port's own server, standing in for Xbox Live and THQ's Quazal servers. It lists sessions, answers the Rendez-Vous login and relays traffic. Anyone can run one; the launcher points at the owner's by default.
_Avoid_: backend, master server, Xenia Web Services

**Rendez-Vous stand-in**:
The part of the game server that answers the game's login to Quazal Rendez-Vous, the service THQ hosted, so the game runs its real login unchanged.
_Avoid_: login hook, fake login, RV bypass

**Relay**:
The game server forwarding match traffic between two players whose PCs can't reach each other directly. Used only when a direct connection fails.
_Avoid_: proxy, TURN server

**Player Match**:
The game's online match that doesn't count toward leaderboards, and the first target of online play.
_Avoid_: unranked match, casual match

**Ranked Match**:
The game's online match that counts toward its leaderboards and needs every player to agree on the result. A later target, if ever.
_Avoid_: competitive match

**Desync**:
Two PCs' match simulations drifting apart even though they received the same controller input. The game detects it with its own checksums.
_Avoid_: out of sync, lag (that's latency)

### Modding

**Mod**:
A player-supplied change to the game: replacement packages, config edits, or new content, in one folder with a manifest. Never shipped with the port.
_Avoid_: patch (that's a title update or a code change), addon, plugin

**Cosmetic mod**:
A mod that changes only how the game looks, so it can be used in online play. A mod counts as cosmetic only if its author says so and it touches nothing on the gameplay list.
_Avoid_: visual mod, skin

**Gameplay mod**:
A mod that can change how a match plays: any config edit, any script package, or any mod not declared cosmetic. Left out of online play, because it can cause a desync.
_Avoid_: balance mod, cheat

**Content mod**:
A mod that adds to the game instead of replacing, such as a new wrestler in an unused roster slot. It rides on the game's own DLC mechanism. Never used in online play.
_Avoid_: DLC (that's the official packs), custom DLC

**Roster entry**:
A content mod's description of one new wrestler: a placeholder slot's id, the complete entry it copies, and the property values it changes, among them its own `CodeName`, the key of its label and pick. The launcher writes it into the roster package at Play, with its name text.
_Avoid_: character patch, roster edit

**Load order**:
The player's ordering of enabled mods. When two mods replace the same file, the later one wins.
_Avoid_: priority, mod order
