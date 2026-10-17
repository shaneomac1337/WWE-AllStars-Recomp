# SDK boilerplate lives in generated/rexglue.cmake.

cmake_minimum_required(VERSION 3.25)
# One product version for both exes: the launcher's <Version>, shown in
# Explorer's file properties (src/res/wweallstars.rc.in).
file(STRINGS launcher/Directory.Build.props WWEALLSTARS_VERSION_LINE
    REGEX "<Version>[0-9]+\\.[0-9]+\\.[0-9]+</Version>" LIMIT_COUNT 1)
string(REGEX MATCH "[0-9]+\\.[0-9]+\\.[0-9]+" WWEALLSTARS_VERSION "${WWEALLSTARS_VERSION_LINE}")
if(NOT WWEALLSTARS_VERSION)
    message(FATAL_ERROR "No <Version>x.y.z</Version> in launcher/Directory.Build.props")
endif()
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/launcher/Directory.Build.props")
project(wweallstars VERSION ${WWEALLSTARS_VERSION} LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Before the generated CMake, which embeds metadata/icons (game art) if it exists.
include(cmake/no_game_art.cmake)
include(generated/rexglue.cmake)

set(WWEALLSTARS_SOURCES
    src/main.cpp
    src/crash_report.cpp
    src/d3d_census.cpp
    src/frame_capture.cpp
    src/settings_cvars.cpp
    src/startup_paths.cpp
    src/image_check.cpp
    src/settings/settings_page.cpp
    src/settings/options_hooks.cpp
    src/settings/schema_export.cpp
    src/settings/gpu_list.cpp
    src/frame_clock.cpp
    src/game_tick_log.cpp
    src/gpu_wait.cpp
    src/resolve_watch.cpp
    src/online_trace_hooks.cpp
    src/unlock_everything.cpp
    src/script_input.cpp
    src/vibration/enhanced_hooks.cpp
    src/vibration/mode.cpp
    src/vibration/rumble_mixer.cpp
    src/xma_stats.cpp
    src/native/geometry_cache.cpp
    src/native/live_topup.cpp
    src/native/native_renderer.cpp
    src/native/pipelines.cpp
    src/native/pipeline_parts.cpp
    src/native/pipeline_store.cpp
    src/native/prebuild_plan.cpp
    src/native/prepare_output.cpp
    src/native/shader_library.cpp
    src/native/shader_pack.cpp
    src/native/shader_topup.cpp
    src/native/texture_layout.cpp
    src/native/textures.cpp
    src/native/topup_converter.cpp
    src/native/vk_context.cpp
    src/native/write_watch.cpp
)

# Native renderer shaders come from the shader pack in the cache folder,
# converted locally from the player's game (scripts/convert-shaders.ps1 or the
# launcher; game-derived, never committed). The build only records which
# XenosRecomp patch a pack must be converted with; Native refuses a pack
# converted with another one (stale) and the session runs on Emulated.
set(WWEALLSTARS_XENOSRECOMP_PATCH "${CMAKE_CURRENT_SOURCE_DIR}/patches/xenosrecomp-wweallstars.patch")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${WWEALLSTARS_XENOSRECOMP_PATCH}")
file(SHA256 "${WWEALLSTARS_XENOSRECOMP_PATCH}" WWEALLSTARS_PATCH_SHA256)
string(SUBSTRING "${WWEALLSTARS_PATCH_SHA256}" 0 16 WWEALLSTARS_EXPECTED_CONVERTER_ID)

# The disc manifest the launcher's setup marker must name: started without the
# launcher, the game refuses a game data folder set up against another one.
set(WWEALLSTARS_DISC_MANIFEST "${CMAKE_CURRENT_SOURCE_DIR}/launcher/disc-manifest.tsv")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${WWEALLSTARS_DISC_MANIFEST}")
file(STRINGS "${WWEALLSTARS_DISC_MANIFEST}" WWEALLSTARS_MANIFEST_LINE
    REGEX "^manifest_version\t" LIMIT_COUNT 1)
string(REGEX REPLACE "^manifest_version\t([^\t]+)$" "\\1" WWEALLSTARS_MANIFEST_VERSION "${WWEALLSTARS_MANIFEST_LINE}")
if(NOT WWEALLSTARS_MANIFEST_VERSION OR WWEALLSTARS_MANIFEST_VERSION STREQUAL WWEALLSTARS_MANIFEST_LINE)
    message(FATAL_ERROR "No manifest_version line in ${WWEALLSTARS_DISC_MANIFEST}")
endif()

if(WIN32)
    add_executable(wweallstars WIN32 ${WWEALLSTARS_SOURCES})
else()
    add_executable(wweallstars ${WWEALLSTARS_SOURCES})
endif()

rexglue_setup_target(wweallstars GPU_PLUGINS xenos)

# Code generation for the recompiled code and the game only (ticket 11); the SDK keeps the
# preset's flags. Target options follow CMAKE_CXX_FLAGS(_<CONFIG>) on the command line, so
# the last -march and -O win. Empty keeps today's x86-64-v2 and the configuration's -O2.
set(WWE_GAME_ARCH "" CACHE STRING "-march for the recompiled code and the game, e.g. x86-64-v3 (empty: the preset's)")
set(WWE_GAME_OPT "" CACHE STRING "Optimization level for the recompiled code and the game, e.g. 3 (empty: the configuration's)")
# The SDK's -ffp-model=strict does not reach these targets (it is scoped to the SDK's
# directory), so contraction is off explicitly: with FMA available (x86-64-v3) clang would
# otherwise fuse a*b+c inside one expression, which a PPC fmul followed by fadd never does.
# A no-op at x86-64-v2, which has no FMA instruction.
foreach(wwe_target wweallstars wweallstars_recomp)
    if(TARGET ${wwe_target})
        target_compile_options(${wwe_target} PRIVATE -ffp-contract=off)
        if(WWE_GAME_ARCH)
            target_compile_options(${wwe_target} PRIVATE -march=${WWE_GAME_ARCH})
        endif()
        if(WWE_GAME_OPT)
            target_compile_options(${wwe_target} PRIVATE -O${WWE_GAME_OPT})
        endif()
    endif()
endforeach()

# ThinLTO and PGO for the recompiled code and the game (ticket 12). Both are off by default;
# the SDK libraries stay plain objects. Developer path: README, "Build variants".
set(WWE_GAME_LTO "" CACHE STRING "LTO for the recompiled code and the game: empty (off) or thin")
set_property(CACHE WWE_GAME_LTO PROPERTY STRINGS "" thin)
set(WWE_GAME_LTO_JOBS 8 CACHE STRING "ThinLTO backend threads in the link")
option(WWE_GAME_PGO_GENERATE "Instrument the recompiled code and the game for PGO training" OFF)
set(WWE_GAME_PGO_USE "" CACHE FILEPATH "A merged .profdata (scripts/pgo_profile.py merge) to optimize with")
if(WWE_GAME_LTO AND NOT WWE_GAME_LTO STREQUAL "thin")
    message(FATAL_ERROR "WWE_GAME_LTO must be empty or thin, not '${WWE_GAME_LTO}'")
endif()
if(WWE_GAME_PGO_GENERATE AND WWE_GAME_PGO_USE)
    message(FATAL_ERROR "WWE_GAME_PGO_GENERATE and WWE_GAME_PGO_USE exclude each other")
endif()
if(WWE_GAME_PGO_USE AND NOT EXISTS "${WWE_GAME_PGO_USE}")
    message(FATAL_ERROR "WWE_GAME_PGO_USE: no profile at ${WWE_GAME_PGO_USE}")
endif()
set(WWE_GAME_PGO_RAW_DIR "${CMAKE_BINARY_DIR}/pgo-raw")
set(wwe_codegen_options "")
if(WWE_GAME_LTO)
    list(APPEND wwe_codegen_options -flto=thin)
endif()
if(WWE_GAME_PGO_GENERATE)
    # Each run of the instrumented exe merges its counters into pgo-raw/default_<id>.profraw
    # when it is closed with the window's X (the game dumps them before the SDK's hard exit),
    # not when it is killed.
    list(APPEND wwe_codegen_options "-fprofile-generate=${WWE_GAME_PGO_RAW_DIR}")
    target_compile_definitions(wweallstars PRIVATE WWE_PGO_GENERATE)
elseif(WWE_GAME_PGO_USE)
    list(APPEND wwe_codegen_options "-fprofile-use=${WWE_GAME_PGO_USE}")
endif()
if(wwe_codegen_options)
    foreach(wwe_target wweallstars wweallstars_recomp)
        if(TARGET ${wwe_target})
            target_compile_options(${wwe_target} PRIVATE ${wwe_codegen_options})
        endif()
    endforeach()
    # The link runs the ThinLTO backends and pulls in the profile runtime.
    target_link_options(wweallstars PRIVATE ${wwe_codegen_options})
endif()
if(WWE_GAME_LTO)
    target_link_options(wweallstars PRIVATE
        "LINKER:/opt:lldltojobs=${WWE_GAME_LTO_JOBS}"
        "LINKER:/lldltocache:${CMAKE_BINARY_DIR}/thinlto-cache")
    if(WWE_GAME_OPT)
        # The backends' level; without it lld optimizes at -O2 whatever the compile used.
        target_link_options(wweallstars PRIVATE "LINKER:/opt:lldlto=${WWE_GAME_OPT}")
    endif()
endif()
if(WWE_GAME_PGO_USE)
    # Recompile when the profile changes.
    get_target_property(wwe_game_sources wweallstars SOURCES)
    set_property(SOURCE ${wwe_game_sources} ${REXGLUE_ENTRYPOINT_GENERATED_SOURCES}
        APPEND PROPERTY OBJECT_DEPENDS "${WWE_GAME_PGO_USE}")
endif()

# The original icon (art/icon, BSD-3) and VERSIONINFO, compiled with llvm-rc
# (CMakePresets.json). The guard also runs before every build, so a
# metadata/icons folder made after configuring still fails the build.
if(WIN32)
    enable_language(RC)
    set(WWEALLSTARS_ICON "${CMAKE_CURRENT_SOURCE_DIR}/art/icon/wweallstars.ico")
    configure_file(src/res/wweallstars.rc.in "${CMAKE_CURRENT_BINARY_DIR}/wweallstars.rc" @ONLY)
    target_sources(wweallstars PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/wweallstars.rc")
    set_source_files_properties("${CMAKE_CURRENT_BINARY_DIR}/wweallstars.rc"
        PROPERTIES OBJECT_DEPENDS "${WWEALLSTARS_ICON}")
endif()
add_custom_target(wweallstars_no_game_art
    COMMAND "${CMAKE_COMMAND}" "-DWWE_SOURCE_ROOT=${CMAKE_CURRENT_SOURCE_DIR}"
        -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/no_game_art.cmake"
    COMMENT "Checking that no game art can be embedded"
    VERBATIM)
add_dependencies(wweallstars wweallstars_no_game_art)
# The shipped pipeline key list (ADR-0019) goes beside the exe; Native
# prebuilds its keys at startup. scripts/pipeline_keys.py merges new keys in.
set(WWEALLSTARS_PIPELINE_KEYS "${CMAKE_CURRENT_SOURCE_DIR}/data/pipeline-keys.txt")
add_custom_command(OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/pipeline-keys.txt"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${WWEALLSTARS_PIPELINE_KEYS}"
        "${CMAKE_CURRENT_BINARY_DIR}/pipeline-keys.txt"
    DEPENDS "${WWEALLSTARS_PIPELINE_KEYS}"
    COMMENT "Copying the shipped pipeline key list"
    VERBATIM)
add_custom_target(wweallstars_pipeline_keys ALL DEPENDS "${CMAKE_CURRENT_BINARY_DIR}/pipeline-keys.txt")
add_dependencies(wweallstars wweallstars_pipeline_keys)
target_compile_definitions(wweallstars PRIVATE
    WWE_EXPECTED_SHADER_CONVERTER_ID=0x${WWEALLSTARS_EXPECTED_CONVERTER_ID}ull
    WWE_DISC_MANIFEST_VERSION="${WWEALLSTARS_MANIFEST_VERSION}"
    # Live shader top-ups (src/native/topup_converter.h) in a dev build: the XenosRecomp
    # scripts/build-xenosrecomp.ps1 builds. The release ships its own beside game/.
    WWE_DEV_XENOSRECOMP="${CMAKE_CURRENT_SOURCE_DIR}/out/build/xenosrecomp/XenosRecomp/XenosRecomp.exe"
    WWE_DEV_XENOSRECOMP_INCLUDE="${CMAKE_CURRENT_SOURCE_DIR}/.tools/xenosrecomp/XenosRecomp/shader_common.h")

# Diagnostic build: count calls to every D3D library function
# (scripts/gen_d3d_sweep.py). Never used for releases.
option(WWE_D3D_SWEEP "Hook every XDK D3D function with a call counter" OFF)
if(WWE_D3D_SWEEP)
    target_sources(wweallstars PRIVATE src/d3d_sweep.gen.cpp src/d3d_sweep.cpp)
    target_compile_definitions(wweallstars PRIVATE WWE_D3D_SWEEP)
endif()

# The native renderer un-tiles textures with the SDK's Xenos texture layout
# code (BSD-3), which lives in the GPU plugin rather than the host runtime.
if(REXSDK_DIR)
    target_sources(wweallstars PRIVATE
        ${REXSDK_DIR}/src/graphics/pipeline/texture/util.cpp)
endif()
target_include_directories(wweallstars PRIVATE src src/native)

# zstd (decompression only) and smol-v unpack the shader pack; both
# come with XenosRecomp in .tools/xenosrecomp (BSD-3 and MIT).
set(WWEALLSTARS_XENOSRECOMP_THIRDPARTY "${CMAKE_CURRENT_SOURCE_DIR}/.tools/xenosrecomp/thirdparty")
set(WWEALLSTARS_ZSTD "${WWEALLSTARS_XENOSRECOMP_THIRDPARTY}/zstd/lib")
add_library(wweallstars_native_deps STATIC
    ${WWEALLSTARS_ZSTD}/common/debug.c
    ${WWEALLSTARS_ZSTD}/common/entropy_common.c
    ${WWEALLSTARS_ZSTD}/common/error_private.c
    ${WWEALLSTARS_ZSTD}/common/fse_decompress.c
    ${WWEALLSTARS_ZSTD}/common/xxhash.c
    ${WWEALLSTARS_ZSTD}/common/zstd_common.c
    ${WWEALLSTARS_ZSTD}/decompress/huf_decompress.c
    ${WWEALLSTARS_ZSTD}/decompress/zstd_ddict.c
    ${WWEALLSTARS_ZSTD}/decompress/zstd_decompress.c
    ${WWEALLSTARS_ZSTD}/decompress/zstd_decompress_block.c
    ${WWEALLSTARS_XENOSRECOMP_THIRDPARTY}/smol-v/source/smolv.cpp)
target_compile_definitions(wweallstars_native_deps PRIVATE ZSTD_DISABLE_ASM)
target_compile_options(wweallstars_native_deps PRIVATE -w)
target_include_directories(wweallstars_native_deps PUBLIC
    ${WWEALLSTARS_ZSTD} ${WWEALLSTARS_XENOSRECOMP_THIRDPARTY}/smol-v/source)
target_link_libraries(wweallstars PRIVATE wweallstars_native_deps)
# Crash report (src/crash_report.cpp): the version and commit go in its manifest.
execute_process(COMMAND git rev-parse --short HEAD WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    OUTPUT_VARIABLE WWEALLSTARS_COMMIT OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
if(NOT WWEALLSTARS_COMMIT)
    set(WWEALLSTARS_COMMIT "unknown")
endif()
target_link_libraries(wweallstars PRIVATE dbghelp)
target_compile_definitions(wweallstars PRIVATE
    WWE_VERSION="${WWEALLSTARS_VERSION}" WWE_COMMIT="${WWEALLSTARS_COMMIT}")

# The v0.10.0 source-tree runtime does not propagate ImGui's include directory
# to the host, although its public UI headers include imgui.h.
if(REXSDK_DIR)
    target_include_directories(wweallstars PRIVATE
        $<TARGET_PROPERTY:imgui::imgui,INTERFACE_INCLUDE_DIRECTORIES>)
    # The native renderer includes the SDK's Vulkan headers, which include RenderDoc's.
    target_include_directories(wweallstars PRIVATE ${REXSDK_DIR}/thirdparty/renderdoc
        ${REXSDK_DIR}/thirdparty/xxHash ${REXSDK_DIR}/thirdparty/tomlplusplus/include)
    # nlohmann/json (MIT, vendored with inja) reads the launcher's state file (src/startup_paths.cpp).
    target_include_directories(wweallstars PRIVATE ${REXSDK_DIR}/thirdparty/inja/third_party/include)
endif()

# Offline tests of the native renderer's pure code (decoding, tiling): Catch2
# from the SDK tree. scripts/build.ps1 -Action Test configures, builds and runs
# them; they never ship.
option(WWE_BUILD_TESTS "Build the native renderer's offline tests" OFF)
if(WWE_BUILD_TESTS AND REXSDK_DIR)
    enable_testing()
    add_executable(wweallstars_native_tests
        tests/native/test_guest_decode.cpp
        tests/native/test_texture_layout.cpp
        tests/native/test_sampler_choice.cpp
        tests/native/test_write_watch.cpp
        tests/native/test_resolve_watch.cpp
        tests/native/test_draw_skips.cpp
        tests/native/test_renderer_fallback.cpp
        tests/native/test_gpu_failure.cpp
        tests/native/test_frame_dumps.cpp
        tests/native/test_texture_formats.cpp
        tests/native/test_pipeline_key.cpp
        tests/native/test_pipeline_parts.cpp
        tests/native/test_pipeline_store.cpp
        tests/native/test_prebuild_plan.cpp
        tests/native/test_prepare_output.cpp
        tests/native/test_shader_pack.cpp
        tests/native/test_shader_topup.cpp
        tests/native/test_live_topup.cpp
        tests/native/test_module_table.cpp
        tests/native/test_gpu_timer.cpp
        tests/native/test_pass_names.cpp
        tests/native/test_debug_labels.cpp
        tests/native/test_thread_budget.cpp
        tests/core/test_frame_clock.cpp
        tests/core/test_swap_choice.cpp
        tests/core/test_device_extensions.cpp
        tests/core/test_vblank_source.cpp
        tests/core/test_present_pacing.cpp
        tests/core/test_frame_capture.cpp
        tests/core/test_setting_lifecycles.cpp
        tests/core/test_settings_page.cpp
        tests/core/test_graphics_preset.cpp
        tests/core/test_gpu_suggestion.cpp
        tests/core/test_crash_report.cpp
        tests/core/test_crash_zip.cpp
        tests/core/test_crash_dump_request.cpp
        tests/core/test_spin_waits.cpp
        tests/core/test_settings_config.cpp
        tests/core/test_settings_registry.cpp
        tests/core/test_settings_schema.cpp
        tests/core/test_text_settings.cpp
        tests/core/test_runtime_choices.cpp
        tests/core/test_options_row.cpp
        tests/core/test_exit_row.cpp
        tests/core/test_startup_paths.cpp
        tests/core/test_image_check.cpp
        tests/core/test_overlapped_receive.cpp
        tests/core/test_online_addressing.cpp
        tests/core/test_online_client.cpp
        tests/core/test_online_relay.cpp
        tests/core/test_online_sessions.cpp
        tests/core/test_online_upnp.cpp
        tests/input/test_script_input.cpp
        tests/input/test_vibration.cpp
        tests/input/test_vibration_mode.cpp
        tests/input/test_pulse_tiers.cpp
        tests/input/test_rumble_target.cpp
        tests/input/test_shapes.cpp
        tests/input/test_mat_impact.cpp
        tests/input/test_rumble_mixer.cpp
        tests/input/test_game_state.cpp
        tests/input/test_attacker_confirm.cpp
        tests/input/test_reversal.cpp
        tests/input/test_wind_up.cpp
        tests/audio/test_xma_stats.cpp
        tests/audio/test_xma_work_rules.cpp
        tests/audio/test_xma_frame_drain.cpp
        src/script_input.cpp
        src/settings_cvars.cpp
        src/startup_paths.cpp
        src/image_check.cpp
        src/vibration/mode.cpp
        src/vibration/rumble_mixer.cpp
        src/native/pipeline_parts.cpp
        src/native/pipeline_store.cpp
        src/native/prebuild_plan.cpp
        src/native/prepare_output.cpp
        src/native/live_topup.cpp
        src/native/shader_pack.cpp
        src/native/shader_topup.cpp
        src/native/texture_layout.cpp
        src/native/write_watch.cpp
        ${REXSDK_DIR}/src/graphics/pipeline/texture/util.cpp
        ${REXSDK_DIR}/thirdparty/catch2/extras/catch_amalgamated.cpp)
    target_include_directories(wweallstars_native_tests PRIVATE src
        ${REXSDK_DIR}/thirdparty/catch2/extras ${REXSDK_DIR}/thirdparty/tomlplusplus/include
        ${REXSDK_DIR}/thirdparty/inja/third_party/include ${REXSDK_DIR}/thirdparty/xxHash)
    target_link_libraries(wweallstars_native_tests PRIVATE rex::runtime wweallstars_native_deps)
    target_compile_definitions(wweallstars_native_tests PRIVATE
        WWE_EXPECTED_SHADER_CONVERTER_ID=0x${WWEALLSTARS_EXPECTED_CONVERTER_ID}ull
        WWE_DISC_MANIFEST_VERSION="${WWEALLSTARS_MANIFEST_VERSION}")
    add_test(NAME wweallstars_native_tests COMMAND wweallstars_native_tests)
endif()
