#include <catch_amalgamated.hpp>

#include "native/guest_decode.h"

using namespace wwe::native::guest;

TEST_CASE("big-endian loads") {
  const uint8_t bytes[] = {0x3F, 0x80, 0x00, 0x00, 0x12, 0x34};
  CHECK(LoadBE32(bytes) == 0x3F800000u);
  CHECK(LoadBE16(bytes + 4) == 0x1234u);
  CHECK(LoadBEFloat(bytes) == 1.0f);
  CHECK(ByteSwap32(0x11223344u) == 0x44332211u);
  uint32_t out[1];
  SwapCopy32(out, bytes, 1);
  CHECK(out[0] == 0x3F800000u);
}

TEST_CASE("CPU addresses translate to physical like the draw packet") {
  CHECK(ToPhysical(0xA0001000u) == 0x00001000u);
  CHECK(ToPhysical(0x40000000u) == 0x00000000u);
  CHECK(ToPhysical(0xBFFFF000u) == 0x1FFFF000u);
  // The 0xE0000000 range is offset by one page.
  CHECK(ToPhysical(0xE0000000u) == 0x00001000u);
  CHECK(ToPhysical(0xE0010000u) == 0x00011000u);
}

TEST_CASE("register mirror offsets match the verified device layout") {
  CHECK(MirrorOffset(0x2000) == 0x2880);  // RB_SURFACE_INFO
  CHECK(MirrorOffset(0x2001) == 0x2884);  // RB_COLOR_INFO
  CHECK(MirrorOffset(0x210F) == 0x2908);  // PA_CL_VPORT_XSCALE
  CHECK(MirrorOffset(0x2200) == 0x2934);  // RB_DEPTHCONTROL
  CHECK(MirrorOffset(0x2318) == 0x2A18);  // RB_COPY_CONTROL
  CHECK(MirrorOffset(0x2380) == 0x2A50);
  // Past the end of a group, and registers the device keeps no copy of.
  CHECK(MirrorOffset(0x2013) == 0);
  CHECK(MirrorOffset(0x4900) == 0);

  uint8_t device[0x3000] = {};
  device[0x2884] = 0x12;
  device[0x2887] = 0x34;
  CHECK(ReadReg(device, 0x2001) == 0x12000034u);
  CHECK(ReadReg(device, 0x4900) == 0u);
}

TEST_CASE("declaration types and input locations") {
  const DeclType* normal = FindDeclType(0x2A2191);
  REQUIRE(normal);
  CHECK(normal->packed_normal);
  CHECK(normal->uint_format == VK_FORMAT_R32_UINT);
  const DeclType* half2 = FindDeclType(0x2C235F);
  REQUIRE(half2);
  CHECK(half2->sixteen_bit);
  CHECK(FindDeclType(0x123456) == nullptr);

  const InputLocation* texcoord1 = FindLocation(5, 1);
  REQUIRE(texcoord1);
  CHECK(texcoord1->location == 5);
  CHECK(FindLocation(3, 0)->is_uint);  // NORMAL0
  CHECK(FindLocation(12, 0) == nullptr);
}

TEST_CASE("render target formats") {
  CHECK(ColorTargetFormat(0) == VK_FORMAT_R8G8B8A8_UNORM);
  CHECK(ColorTargetFormat(14) == VK_FORMAT_R32_SFLOAT);
  CHECK(ColorTargetFormat(7) == VK_FORMAT_R16G16B16A16_SFLOAT);
}

TEST_CASE("16-bit fixed render targets keep the -32..32 range") {
  // Xenos k_16_16 and k_16_16_16_16 targets hold -32..32 (ticket 40); SNORM
  // would clamp shader output to -1..1. Half floats hold the range unscaled,
  // so blending stays correct.
  CHECK(ColorTargetFormat(4) == VK_FORMAT_R16G16_SFLOAT);
  CHECK(ColorTargetFormat(5) == VK_FORMAT_R16G16B16A16_SFLOAT);
}

TEST_CASE("live RGBA vertex fetches do not inherit the D3DCOLOR swizzle") {
  VertexFetchInput input = {};
  input.format = 6;
  input.is_normalized = true;
  const DeclType* weights = FindDeclTypeForFetch(input);
  REQUIRE(weights);
  CHECK(weights->float_format == VK_FORMAT_R8G8B8A8_UNORM);
  input.is_normalized = false;
  CHECK(FindDeclTypeForFetch(input)->uint_format == VK_FORMAT_R8G8B8A8_UINT);
  CHECK(FindDeclType(0x182886)->float_format == VK_FORMAT_B8G8R8A8_UNORM);
}

TEST_CASE("rescheduled vertex fetches retain their semantic binding") {
  CHECK(VertexFetchIdentity(0x05F81000, 0x642) ==
        VertexFetchIdentity(0x05F81000, 0xA0C));  // short2 defaults replace z/w
  uint8_t code[60] = {};
  const auto word = [&](size_t offset, uint32_t value) {
    for (size_t b = 0; b < 4; ++b) code[offset + b] = uint8_t(value >> (24 - b * 8));
  };
  // Execute slots 1..4: fetch, fetch, fetch, ALU, then end.
  word(0, 1 | (4 << 12) | (0x15 << 16));
  word(4, 2 << 12);
  word(12, 0x2DF88000);
  word(16, 0x00393E88);
  word(20, 10);
  // UV fetch now occupies the container's old weight-fetch slot.
  word(24, 0x1DF87000);
  word(28, 0x00253FC8);
  word(32, 0x60A);
  word(36, 0x05F83000);
  word(40, 0x40060688);
  word(44, 0x90A);
  VertexElementRef weights{2, 1, 0, VertexFetchIdentity(0x05F83000, 0x688)};
  REQUIRE(LocateVertexFetch(code, sizeof(code), weights));
  CHECK(weights.address == 3);
  VertexFetchInput input{};
  REQUIRE(DecodeVertexFetch(code, weights, input));
  CHECK(input.offset_dwords == 9);
  CHECK(input.format == 6);
  CHECK(input.is_normalized);
  CHECK(input.stride_dwords == 10);
  // An ALU word that happens to resemble a vertex fetch must not match.
  word(48, 0x05F84000);
  word(52, 0x688);
  VertexElementRef absent{2, 1, 0, VertexFetchIdentity(0x05F84000, 0x688)};
  CHECK_FALSE(LocateVertexFetch(code, sizeof(code), absent));
  VertexElementRef position{1, 0, 0, VertexFetchIdentity(0x2DF88000, 0xE88)};
  CHECK(LocateVertexFetch(code, sizeof(code), position));
  CHECK(position.address == 1);
}

TEST_CASE("surface sizes") {
  const SurfaceSize size = DecodeSurfaceSize((1279u << 18) | (719u << 3));
  CHECK(size.width == 1280);
  CHECK(size.height == 720);
}

TEST_CASE("index scan skips restart indices and rebases") {
  // 16-bit strip with a restart: 7 8 9 FFFF 5 6
  const uint8_t data[] = {0, 7, 0, 8, 0, 9, 0xFF, 0xFF, 0, 5, 0, 6};
  uint32_t indices[6];
  const IndexRange range = ScanIndices(data, 6, false, indices);
  CHECK(range.min == 5);
  CHECK(range.max == 9);
  uint32_t out[6];
  CHECK(RebaseIndices(indices, 6, false, false, range.min, out) == 6);
  CHECK(out[0] == 2);
  CHECK(out[3] == 0xFFFFFFFFu);
  CHECK(out[4] == 0);

  uint32_t none[1];
  const uint8_t only_restart[] = {0xFF, 0xFF};
  CHECK(ScanIndices(only_restart, 1, false, none).empty());
}

TEST_CASE("quad lists become two triangles each") {
  const uint32_t quad[8] = {10, 11, 12, 13, 14, 15, 16, 17};
  uint32_t out[12];
  CHECK(RebaseIndices(quad, 8, true, true, 10, out) == 12);
  const uint32_t expected[12] = {0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7};
  for (int i = 0; i < 12; ++i) {
    CHECK(out[i] == expected[i]);
  }
}

TEST_CASE("non-indexed quad lists get generated triangle indices") {
  uint32_t out[12];
  CHECK(QuadListIndices(9, out) == 12);  // the incomplete ninth vertex is dropped
  const uint32_t expected[12] = {0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7};
  for (int i = 0; i < 12; ++i) {
    CHECK(out[i] == expected[i]);
  }
  CHECK(QuadListIndices(3, out) == 0);
}

// Where a clip-space position lands in pixels through a ClipTransform.
static void Project(const ClipTransform& t, float x, float y, float w, float& px, float& py) {
  const float cx = x * t.ndc_scale[0] + t.offset[0] * w;
  const float cy = y * t.ndc_scale[1] + t.offset[1] * w;
  px = cx / w * t.x_scale + t.x_offset;
  py = cy / w * t.y_scale + t.y_offset;
}

TEST_CASE("clip transform keeps the guest viewport for clip-space draws") {
  const ClipTransform t = ComputeClipTransform(0x43F, 640, 640, -360, 360, 1280, 720, false);
  float px, py;
  Project(t, -1, 1, 1, px, py);
  CHECK(px == Catch::Approx(0));
  CHECK(py == Catch::Approx(0));
  Project(t, 0.5f, -0.5f, 2, px, py);  // w = 2: (0.25, -0.25) in NDC
  CHECK(px == Catch::Approx(800));
  CHECK(py == Catch::Approx(450));
  const ClipTransform h = ComputeClipTransform(0x43F, 640, 640, -360, 360, 1280, 720, true);
  Project(h, -1, 1, 1, px, py);
  CHECK(px == Catch::Approx(0.5f).margin(1e-4));
  CHECK(py == Catch::Approx(0.5f).margin(1e-4));
}

TEST_CASE("pre-transformed positions land on their pixels") {
  // The game's full-screen passes: VTE scales off, positions in pixels.
  const ClipTransform t = ComputeClipTransform(0x400, 640, 640, -360, 360, 1280, 720, false);
  float px, py;
  for (const float x : {0.0f, 100.0f, 1280.0f}) {
    for (const float y : {0.0f, 333.0f, 720.0f}) {
      Project(t, x, y, 1, px, py);
      CHECK(px == Catch::Approx(x).margin(1e-3));
      CHECK(py == Catch::Approx(y).margin(1e-3));
    }
  }
  // Inside Vulkan's clip volume, so nothing is clipped.
  CHECK(1280 * t.ndc_scale[0] + t.offset[0] == Catch::Approx(1));
  CHECK(720 * t.ndc_scale[1] + t.offset[1] == Catch::Approx(-1));
  const ClipTransform h = ComputeClipTransform(0x400, 0, 0, 0, 0, 1280, 720, true);
  Project(h, 10, 20, 1, px, py);
  CHECK(px == Catch::Approx(10.5f).margin(1e-4));
  CHECK(py == Catch::Approx(20.5f).margin(1e-4));
}

TEST_CASE("texture fetch constants the game samples") {
  // Slot 0 of an entrance bloom pass (type 2).
  CHECK(IsTextureFetch(0x81404802u, 0x0F1FE086u));
  // The same texture typed 0 ("invalid texture").
  CHECK(IsTextureFetch(0x81404800u, 0x0F1FE086u));
  // An unset slot, and vertex fetch types.
  CHECK_FALSE(IsTextureFetch(0, 0));
  CHECK_FALSE(IsTextureFetch(0x81404800u, 0x00000086u));
  CHECK_FALSE(IsTextureFetch(0x81404801u, 0x0F1FE086u));
  CHECK_FALSE(IsTextureFetch(0x81404803u, 0x0F1FE086u));
}

TEST_CASE("gamma and linear views of one EDRAM surface share a render target") {
  // WWE All Stars clears and resolves a 1280x720 surface as k_8_8_8_8_GAMMA
  // (RB_COLOR_INFO 000102D0) and draws into it as k_8_8_8_8 (000002D0).
  const uint32_t surface_info = 1280;
  CHECK(ColorTargetKey(surface_info, 0x000102D0) == ColorTargetKey(surface_info, 0x000002D0));
}

TEST_CASE("render target keys still separate base, pitch and host format") {
  CHECK(ColorTargetKey(1280, 0x000002D0) != ColorTargetKey(1280, 0x000002E0));  // EDRAM base
  CHECK(ColorTargetKey(1280, 0x000002D0) != ColorTargetKey(640, 0x000002D0));   // pitch
  CHECK(ColorTargetKey(1280, 0x000002D0) != ColorTargetKey(1280, 0x000702D0));  // 16_16_16_16_FLOAT
}

TEST_CASE("16-bit pairs get their halves swapped back for texcoords and positions") {
  // An 8-in-32 vertex fetch of 16-bit data leaves each pair as (y, x) after
  // the host's 32-bit byte swap; the shader swaps them back per mask bit.
  // Arguments: usage, usage index, sixteen-bit, four components.
  CHECK(HalfSwapBit(5, 0, true, true) == 1u << 0);    // TEXCOORD0
  CHECK(HalfSwapBit(5, 3, true, false) == 1u << 3);   // TEXCOORD3
  CHECK(HalfSwapBit(0, 0, false, false) == 0u);       // 32-bit floats need no swap
  CHECK(HalfSwapBit(10, 0, true, true) == 0u);        // COLOR0 is not swapped
}

TEST_CASE("a 2-component 16-bit position swaps x/y only, keeping w = 1") {
  // Swapping zw as well would turn the default (z, w) = (0, 1) into w = 0 and
  // send every vertex to infinity (the native black screen).
  CHECK(HalfSwapBit(0, 0, true, false) == 1u << 16);
  CHECK(HalfSwapBit(0, 0, true, true) == ((1u << 16) | (1u << 24)));
  CHECK(HalfSwapBit(0, 1, true, false) == 1u << 17);
}

TEST_CASE("720p into a 1440p window goes to the presenter to be scaled up") {
  const OutputPlan plan = PlanOutput(1, 2560, 1440);
  CHECK(plan.target_scale == 1);
  CHECK_FALSE(plan.downsample);
  CHECK(plan.output_width == 1280);
  CHECK(plan.output_height == 720);
}

TEST_CASE("1440p into a 1440p window is drawn at the window's size, nothing filtered") {
  const OutputPlan plan = PlanOutput(2, 2560, 1440);
  CHECK(plan.target_scale == 2);
  CHECK_FALSE(plan.downsample);
  CHECK(plan.output_width == 2560);
  CHECK(plan.output_height == 1440);
}

TEST_CASE("2880p into a 1440p window is a box downsample") {
  const OutputPlan plan = PlanOutput(4, 2560, 1440);
  CHECK(plan.target_scale == 4);
  CHECK(plan.downsample);
  CHECK(plan.filter == DownsampleFilter::kBox);
  CHECK(plan.halvings == 1);
  CHECK(plan.output_width == 2560);
  CHECK(plan.output_height == 1440);
}

TEST_CASE("2160p into a 1440p window uses the non-integer filter") {
  const OutputPlan plan = PlanOutput(3, 2560, 1440);
  CHECK(plan.downsample);
  CHECK(plan.filter == DownsampleFilter::kBilinear);
  CHECK(plan.halvings == 0);
  CHECK(plan.output_height == 1440);
}

TEST_CASE("1440p into a 1080p window downsamples") {
  const OutputPlan plan = PlanOutput(2, 1920, 1080);
  CHECK(plan.downsample);
  CHECK(plan.filter == DownsampleFilter::kBilinear);
  CHECK(plan.output_width == 1920);
  CHECK(plan.output_height == 1080);
}

TEST_CASE("2880p into a 1080p window halves once, then filters the rest") {
  const OutputPlan plan = PlanOutput(4, 1920, 1080);
  CHECK(plan.filter == DownsampleFilter::kBilinear);
  CHECK(plan.halvings == 1);
}

TEST_CASE("a window wider than 16:9 is filtered to its 16:9 area") {
  const OutputPlan plan = PlanOutput(3, 3440, 1440);
  CHECK(plan.downsample);
  CHECK(plan.output_width == 2560);
  CHECK(plan.output_height == 1440);
  const OutputPlan tall = PlanOutput(4, 1920, 1200);
  CHECK(tall.output_width == 1920);
  CHECK(tall.output_height == 1080);
}

TEST_CASE("720p is never filtered, even into a smaller window: the presenter fits it as before") {
  const OutputPlan plan = PlanOutput(1, 960, 540);
  CHECK_FALSE(plan.downsample);
  CHECK(plan.output_width == 1280);
  CHECK(plan.output_height == 720);
}

TEST_CASE("texture LOD bias follows the render resolution") {
  CHECK(LodBiasFor(1) == 0.0f);
  CHECK(LodBiasFor(4) == -1.0f);
  CHECK(PlanOutput(1, 1920, 1080).lod_bias == 0.0f);
  CHECK(PlanOutput(2, 1920, 1080).lod_bias == -0.5f);
  CHECK(PlanOutput(3, 1920, 1080).lod_bias == -1.0f);
  CHECK(PlanOutput(4, 1920, 1080).lod_bias == -1.0f);
}

TEST_CASE("CAS strength 0 leaves the presenter as it is; others sharpen, clamped to 1") {
  CHECK_FALSE(CasEffect(0.0).enabled);
  CHECK_FALSE(CasEffect(-1.0).enabled);
  CHECK(CasEffect(0.4).enabled);
  CHECK(CasEffect(0.4).sharpness == 0.4);
  CHECK(CasEffect(3.0).sharpness == 1.0);
}

TEST_CASE("host vertex formats have their byte sizes") {
  CHECK(VertexFormatBytes(VK_FORMAT_R32_SFLOAT) == 4);
  CHECK(VertexFormatBytes(VK_FORMAT_R32G32_SFLOAT) == 8);
  CHECK(VertexFormatBytes(VK_FORMAT_R32G32B32_SFLOAT) == 12);
  CHECK(VertexFormatBytes(VK_FORMAT_R32G32B32A32_SFLOAT) == 16);
  CHECK(VertexFormatBytes(VK_FORMAT_B8G8R8A8_UNORM) == 4);
  CHECK(VertexFormatBytes(VK_FORMAT_R16G16_SSCALED) == 4);
  CHECK(VertexFormatBytes(VK_FORMAT_R16G16B16A16_SFLOAT) == 8);
  CHECK(VertexFormatBytes(VK_FORMAT_A2B10G10R10_SNORM_PACK32) == 4);
  // Every declaration type the tables know has a size.
  for (const DeclType& t : kDeclTypes) {
    CHECK(VertexFormatBytes(t.float_format) > 0);
    CHECK(VertexFormatBytes(t.uint_format) > 0);
  }
}

TEST_CASE("an element past the draw's stride widens the host stride (issue #3)") {
  // The splash quads: declaration 40052100 has a float4 colour at +16, and
  // DrawPrimitiveUP draws it with stride 16.
  CHECK(HostVertexStride(16, 32) == 32);
  CHECK(HostVertexStride(20, 20) == 20);  // its D3DCOLOR twin fits
  CHECK(HostVertexStride(20, 16) == 20);
  CHECK(HostVertexStride(16, 30) == 32);
}

TEST_CASE("repacked vertices hold the guest bytes Xenos fetches for each one") {
  // Three 16-byte vertices plus the 16 bytes the last one's colour reaches.
  uint8_t guest[64];
  for (uint32_t i = 0; i < sizeof(guest); ++i) {
    guest[i] = uint8_t(i);
  }
  uint32_t host[3 * 8] = {};
  RepackVertices(host, guest, 3, 16, 32);
  // Vertex 1 starts at guest byte 16 and runs to byte 48, byte-swapped per dword.
  CHECK(host[8] == 0x10111213u);
  CHECK(host[15] == 0x2C2D2E2Fu);
  // The last vertex's colour comes from past its own 16 bytes.
  CHECK(host[23] == 0x3C3D3E3Fu);
  CHECK(GuestVertexBytes(3, 16, 32) == 64);
  CHECK(GuestVertexBytes(3, 20, 20) == 60);
}
