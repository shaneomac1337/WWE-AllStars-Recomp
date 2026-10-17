// Native Vulkan renderer: draws the game from its own XDK Direct3D calls on the
// GPU plugin's device and hands each finished frame to the plugin's present
// path (rex/system/external_frame.h), replacing Xenos emulation for drawing.
// On by default (ADR-0020); --wwe_native_renderer=false runs the Emulated
// renderer instead. See the Native Renderer PRD.
//
// All entry points run on the guest render thread. Each is called after the
// original D3D function, so the device's register mirror is up to date.

#pragma once

#include <cstdint>

namespace rex {
class Runtime;
}

namespace wwe::native {

// Creates the renderer's resources on the runtime's Vulkan device when the
// cvar is set. Call after the graphics system is set up.
void Configure(rex::Runtime* runtime);

bool IsEnabled();

// The SDK lost the device (presenter or GPU plugin, any thread): under Native,
// ends the session as a GPU failure (ADR-0025) and never returns; otherwise
// returns and the SDK ends the game as before.
void OnHostGpuLoss();

// --wwe_prepare_pipelines: the launcher's headless setup step.
bool PrepareRequested();

// After Configure, instead of running the game: waits for the workers to
// build the pack's modules and the planned pipelines, saves the store and the
// driver cache, removes the "pipelines stale" marker, prints progress and the
// result to standard output (prepare_output.h) and exits with its code.
[[noreturn]] void PreparePipelinesAndExit();

// Stops background work and saves the pipeline cache; call while the Vulkan
// device still exists.
void Shutdown();

// Before the presenter is created (the app's OnPreSetup): applies
// --wwe_native_cas to the presenter's effect settings when Native is requested.
void ApplyPresenterSettings();

// At session end (window closed or guest exit; runs once): prints the draw
// skip summary and writes --wwe_native_report.
void OnExit();

// base is guest memory, device the guest D3DDevice.

// D3DDevice_Present: front_buffer is the texture object shown.
// --wwe_native_dump_frame without the native renderer: writes the guest's
// front buffer (resolved by the emulated GPU, with --readback_resolve=full)
// for comparison with native dumps; without readback, the presenter's guest
// output (F12 in the launcher's sessions).
void DumpGuestFrontBuffer(const uint8_t* base, uint32_t front_buffer, uint64_t frame);

void OnPresent(const uint8_t* base, uint32_t device, uint32_t front_buffer);

// D3DDevice_SetRenderTarget: index and surface object.
void OnSetRenderTarget(const uint8_t* base, uint32_t index, uint32_t surface);

// D3DDevice_Clear: flags (0xF colour, 0x10 depth, 0x20 stencil), rect in
// pixels, colour as a guest float4 pointer (may be 0), depth and stencil.
void OnClear(const uint8_t* base, uint32_t device, uint32_t flags, const int32_t rect[4],
             uint32_t color, float depth, uint32_t stencil);

// D3DDevice_Resolve: flags (0..3 colour target, 4 depth-stencil) and the
// destination texture object (0 for a clear-only resolve).
void OnResolve(const uint8_t* base, uint32_t device, uint32_t flags, uint32_t destination);

// User-memory draws completed by EndVertices or the inline-commit wrappers.
// Indices, when present, address the vertex allocation with base_vertex added.
void OnDrawVertices(const uint8_t* base, uint32_t device, uint32_t primitive,
                    uint32_t vertex_count, uint32_t stride, uint32_t vertices,
                    uint32_t indices = 0, uint32_t index_count = 0,
                    bool index32 = false, int32_t base_vertex = 0);

// D3DDevice_SetStreamSource: the stride, which the device does not keep.
void OnSetStreamSource(uint32_t stream, uint32_t stride);

// D3DDevice_DrawVertices (0x82DB1C48): a non-indexed draw of vertex_count
// vertices from start_vertex over the bound streams.
void OnDrawNonIndexed(const uint8_t* base, uint32_t device, uint32_t primitive,
                      uint32_t start_vertex, uint32_t vertex_count);

// D3DDevice_DrawIndexedVertices: indices from the device's index buffer.
void OnDrawIndexed(const uint8_t* base, uint32_t device, uint32_t primitive,
                   int32_t base_vertex, uint32_t start_index, uint32_t index_count);

}  // namespace wwe::native
