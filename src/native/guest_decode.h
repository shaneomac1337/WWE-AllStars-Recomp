// Pure decoding of guest (Xbox 360) data for the native renderer: big-endian
// loads, CPU to physical addresses, the D3D device's register mirror, vertex
// declaration types, render-target formats and index buffers. No Vulkan calls
// and no state, so tests/native checks it offline.

#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

#include <rex/ui/vulkan/api.h>

namespace wwe::native::guest {

inline uint32_t LoadBE32(const uint8_t* p) {
  return uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
}

inline uint16_t LoadBE16(const uint8_t* p) { return uint16_t(p[0] << 8 | p[1]); }

inline float LoadBEFloat(const uint8_t* p) {
  const uint32_t bits = LoadBE32(p);
  float value;
  std::memcpy(&value, &bits, 4);
  return value;
}

inline uint32_t ByteSwap32(uint32_t v) {
  return (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
}

inline void SwapCopy32(uint32_t* dst, const uint8_t* src, size_t dwords) {
  for (size_t i = 0; i < dwords; ++i) {
    dst[i] = LoadBE32(src + i * 4);
  }
}

// Objects (index buffers, texture objects) hold CPU addresses; the GPU sees
// physical ones, with the 0xE0000000 range offset by 0x1000 as D3D computes
// it for the draw packet. Device fetch constants already hold physical ones.
constexpr uint32_t ToPhysical(uint32_t address) {
  return (address & 0x1FFFFFFF) + (address >= 0xE0000000 ? 0x1000 : 0);
}

// --- Register mirror --------------------------------------------------------

// Guest D3DDevice register mirror groups (SvR 2011's XDK, verified with
// --wwe_d3d_probe): first register, device offset, count.
struct MirrorGroup {
  uint32_t first;
  uint32_t offset;
  uint32_t count;
};
constexpr MirrorGroup kMirrorGroups[] = {
    {0x2000, 0x2880, 19}, {0x2100, 0x28CC, 21}, {0x2180, 0x2920, 5},  {0x2200, 0x2934, 12},
    {0x2280, 0x2964, 21}, {0x2300, 0x29B8, 38}, {0x2380, 0x2A50, 8},
};

// Device offset of a mirrored register, or 0 when the device keeps no copy.
constexpr uint32_t MirrorOffset(uint32_t reg) {
  for (const MirrorGroup& group : kMirrorGroups) {
    if (reg >= group.first && reg < group.first + group.count) {
      return group.offset + (reg - group.first) * 4;
    }
  }
  return 0;
}

inline uint32_t ReadReg(const uint8_t* d3d, uint32_t reg) {
  const uint32_t offset = MirrorOffset(reg);
  return offset ? LoadBE32(d3d + offset) : 0;
}

inline float ReadRegFloat(const uint8_t* d3d, uint32_t reg) {
  const uint32_t bits = ReadReg(d3d, reg);
  float value;
  std::memcpy(&value, &bits, 4);
  return value;
}

// --- Vertex declarations ----------------------------------------------------

// XenosRecomp's SPIR-V vertex input locations (non-re:Blue table), and whether
// the shader declares the input as uint4.
struct InputLocation {
  uint8_t usage;
  uint8_t index;
  uint8_t location;
  bool is_uint;
};
constexpr InputLocation kInputLocations[] = {
    {0, 0, 0, false},   // POSITION0
    {3, 0, 1, true},    // NORMAL0
    {6, 0, 2, true},    // TANGENT0
    {7, 0, 3, true},    // BINORMAL0
    {5, 0, 4, false},   // TEXCOORD0
    {5, 1, 5, false},   // TEXCOORD1
    {5, 2, 6, false},   // TEXCOORD2
    {5, 3, 7, false},   // TEXCOORD3
    {10, 0, 8, false},  // COLOR0
    {2, 0, 9, true},    // BLENDINDICES0
    {1, 0, 10, false},  // BLENDWEIGHT0
    {10, 1, 11, false}, // COLOR1
    {5, 4, 12, false},  // TEXCOORD4
    {5, 5, 13, false},  // TEXCOORD5
    {5, 6, 14, false},  // TEXCOORD6
    {5, 7, 15, false},  // TEXCOORD7
};

struct DeclType {
  uint32_t type;
  VkFormat float_format;
  VkFormat uint_format;
  bool sixteen_bit;
  bool packed_normal;  // 10_11_11 / 11_11_10, decoded by the shader
};
constexpr DeclType kDeclTypes[] = {
    {0x2C83A4, VK_FORMAT_R32_SFLOAT, VK_FORMAT_R32_UINT, false, false},
    {0x2C23A5, VK_FORMAT_R32G32_SFLOAT, VK_FORMAT_R32G32_UINT, false, false},
    {0x2A23B9, VK_FORMAT_R32G32B32_SFLOAT, VK_FORMAT_R32G32B32_UINT, false, false},
    {0x1A23A6, VK_FORMAT_R32G32B32A32_SFLOAT, VK_FORMAT_R32G32B32A32_UINT, false, false},
    {0x182886, VK_FORMAT_B8G8R8A8_UNORM, VK_FORMAT_B8G8R8A8_UINT, false, false},
    {0x1A2286, VK_FORMAT_R8G8B8A8_USCALED, VK_FORMAT_R8G8B8A8_UINT, false, false},
    {0x1A2386, VK_FORMAT_R8G8B8A8_USCALED, VK_FORMAT_R8G8B8A8_UINT, false, false},
    {0x1A2086, VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_UINT, false, false},
    {0x1A2186, VK_FORMAT_R8G8B8A8_SNORM, VK_FORMAT_R8G8B8A8_UINT, false, false},
    {0x2C2359, VK_FORMAT_R16G16_SSCALED, VK_FORMAT_R16G16_UINT, true, false},
    {0x1A235A, VK_FORMAT_R16G16B16A16_SSCALED, VK_FORMAT_R16G16B16A16_UINT, true, false},
    {0x2C2159, VK_FORMAT_R16G16_SNORM, VK_FORMAT_R16G16_UINT, true, false},
    {0x1A215A, VK_FORMAT_R16G16B16A16_SNORM, VK_FORMAT_R16G16B16A16_UINT, true, false},
    {0x2C2059, VK_FORMAT_R16G16_UNORM, VK_FORMAT_R16G16_UINT, true, false},
    {0x1A205A, VK_FORMAT_R16G16B16A16_UNORM, VK_FORMAT_R16G16B16A16_UINT, true, false},
    {0x2C82A1, VK_FORMAT_R32_SFLOAT, VK_FORMAT_R32_UINT, false, false},
    {0x2A2287, VK_FORMAT_A2B10G10R10_USCALED_PACK32, VK_FORMAT_R32_UINT, false, false},
    {0x2A2187, VK_FORMAT_A2B10G10R10_SNORM_PACK32, VK_FORMAT_R32_UINT, false, false},
    // Signed normalized 11_11_10 normals (X 10 bits, Y and Z 11): raw uint,
    // decoded by the shader. Its decoder knows no other packed layout.
    {0x2A2191, VK_FORMAT_R32_SFLOAT, VK_FORMAT_R32_UINT, false, true},
    {0x2C235F, VK_FORMAT_R16G16_SFLOAT, VK_FORMAT_R16G16_UINT, true, false},
    {0x1A2360, VK_FORMAT_R16G16B16A16_SFLOAT, VK_FORMAT_R16G16B16A16_UINT, true, false},
};

// Bytes one vertex attribute of a kDeclTypes host format takes; 0 for others.
constexpr uint32_t VertexFormatBytes(VkFormat format) {
  switch (format) {
    case VK_FORMAT_R32G32B32A32_SFLOAT:
    case VK_FORMAT_R32G32B32A32_UINT:
      return 16;
    case VK_FORMAT_R32G32B32_SFLOAT:
    case VK_FORMAT_R32G32B32_UINT:
      return 12;
    case VK_FORMAT_R32G32_SFLOAT:
    case VK_FORMAT_R32G32_UINT:
    case VK_FORMAT_R16G16B16A16_SSCALED:
    case VK_FORMAT_R16G16B16A16_SNORM:
    case VK_FORMAT_R16G16B16A16_UNORM:
    case VK_FORMAT_R16G16B16A16_SFLOAT:
    case VK_FORMAT_R16G16B16A16_UINT:
      return 8;
    case VK_FORMAT_R32_SFLOAT:
    case VK_FORMAT_R32_UINT:
    case VK_FORMAT_B8G8R8A8_UNORM:
    case VK_FORMAT_B8G8R8A8_UINT:
    case VK_FORMAT_R8G8B8A8_USCALED:
    case VK_FORMAT_R8G8B8A8_UNORM:
    case VK_FORMAT_R8G8B8A8_SNORM:
    case VK_FORMAT_R8G8B8A8_UINT:
    case VK_FORMAT_R16G16_SSCALED:
    case VK_FORMAT_R16G16_SNORM:
    case VK_FORMAT_R16G16_UNORM:
    case VK_FORMAT_R16G16_SFLOAT:
    case VK_FORMAT_R16G16_UINT:
    case VK_FORMAT_A2B10G10R10_USCALED_PACK32:
    case VK_FORMAT_A2B10G10R10_SNORM_PACK32:
      return 4;
    default:
      return 0;
  }
}

// A declaration may place an element past the stride the game draws with
// (the splash quads, issue #3: float4 colour at +16, DrawPrimitiveUP stride
// 16). Xenos then fetches the next vertex's bytes; Vulkan needs a stride that
// covers every attribute (VUID-vkCmdBindVertexBuffers2-pStrides-06209). The
// host stride is the guest one, or the widest attribute extent rounded to 4.
constexpr uint32_t HostVertexStride(uint32_t guest_stride, uint32_t extent) {
  return extent > guest_stride ? (extent + 3) & ~3u : guest_stride;
}

// Guest bytes count vertices fetch: the last one reads a full host stride.
constexpr uint32_t GuestVertexBytes(uint32_t count, uint32_t guest_stride, uint32_t host_stride) {
  return count ? (count - 1) * guest_stride + host_stride : 0;
}

// Copies count vertices to host_stride apart, each from its guest stride
// position, as 32-bit big-endian words. Both strides are multiples of 4.
inline void RepackVertices(uint32_t* dst, const uint8_t* src, uint32_t count,
                           uint32_t guest_stride, uint32_t host_stride) {
  for (uint32_t i = 0; i < count; ++i) {
    SwapCopy32(dst + i * (host_stride / 4), src + i * guest_stride, host_stride / 4);
  }
}

constexpr const DeclType* FindDeclType(uint32_t type) {
  for (const DeclType& t : kDeclTypes) {
    if (t.type == type) {
      return &t;
    }
  }
  return nullptr;
}

// The g_SwappedTexcoords bits that make the shader swap 16-bit pairs back
// after the host's 32-bit vertex byte swap. TEXCOORDn: bit n (the shader swaps
// .yxwz). POSITIONn: bit 16 + n swaps x/y, bit 24 + n also z/w, only for
// four-component data: a two-component fetch has the defaults (0, 1) in z/w,
// which must stay put (patches/xenosrecomp-wweallstars.patch, tfetchPosition).
constexpr uint32_t HalfSwapBit(uint8_t usage, uint8_t index, bool sixteen_bit,
                               bool four_component) {
  if (!sixteen_bit || index > 7) return 0;
  if (usage == 5) return 1u << index;  // TEXCOORD
  if (usage == 0) {                    // POSITION
    return 1u << (16 + index) | (four_component ? 1u << (24 + index) : 0u);
  }
  return 0;
}

// Whether a Xenos vertex format with 16-bit components has four of them
// (k_16_16_16_16, k_16_16_16_16_FLOAT) rather than two.
constexpr bool FourComponent16(uint32_t format) { return format == 26 || format == 32; }

constexpr const InputLocation* FindLocation(uint32_t usage, uint32_t index) {
  for (const InputLocation& l : kInputLocations) {
    if (l.usage == usage && l.index == index) {
      return &l;
    }
  }
  return nullptr;
}

// --- Vertex inputs from the shader's vfetch instructions -------------------

// A vertex shader element from the container header: {address:12, usage:4,
// usage_index:4}. address is the element's vfetch instruction slot (12 bytes)
// from the start of the shader code.
struct VertexElementRef {
  uint16_t address;
  uint8_t usage;
  uint8_t usage_index;
  uint32_t fetch_identity;
};

constexpr uint32_t VertexFetchIdentity(uint32_t w0, uint32_t w1) {
  uint32_t write_mask = 0;
  for (uint32_t i = 0; i < 4; ++i) {
    if (((w1 >> (i * 3)) & 7) != 7) write_mask |= 1u << i;
  }
  return (w0 & 0x0007F000) | write_mask;
}

// The elements of a vertex shader container and the offset of its code in the
// physical (microcode) part. False if the container is malformed.
inline bool DecodeVertexElements(const uint8_t* container, size_t size,
                                 std::vector<VertexElementRef>& out,
                                 uint32_t& code_offset, uint32_t& code_size) {
  out.clear();
  if (size < 0x24) return false;
  const uint32_t virtual_size = LoadBE32(container + 4);
  const uint32_t shader_offset = LoadBE32(container + 0x18);
  if (virtual_size > size || shader_offset + 0x24 > virtual_size) return false;
  const uint8_t* shader = container + shader_offset;
  code_offset = LoadBE32(shader);
  code_size = LoadBE32(shader + 4);
  if (uint64_t(virtual_size) + code_offset + code_size > size) return false;
  const uint32_t first = LoadBE32(shader + 0x18);
  const uint32_t count = LoadBE32(shader + 0x1C);
  if (count > 64 || shader_offset + 0x24 + (first + count) * 4 > virtual_size) return false;
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t element = LoadBE32(shader + 0x24 + (first + i) * 4);
    const uint32_t address = element & 0xFFF;
    if ((address + 1) * 12 > code_size) return false;
    const uint8_t* fetch = container + virtual_size + code_offset + address * 12;
    out.push_back({uint16_t(element & 0xFFF), uint8_t((element >> 12) & 0xF),
                   uint8_t((element >> 16) & 0xF),
                   VertexFetchIdentity(LoadBE32(fetch), LoadBE32(fetch + 4))});
  }
  return true;
}

// D3D may reschedule fetch instructions while binding a declaration, leaving
// the container's element addresses unchanged. Match the destination register
// and write mask, and search only slots marked as fetches by the live CF program.
// Component selectors can change when the declaration supplies default 0/1.
inline bool LocateVertexFetch(const uint8_t* code, uint32_t code_size,
                              VertexElementRef& element) {
  const auto matches = [&](uint32_t address) {
    if ((uint64_t(address) + 1) * 12 > code_size) return false;
    const uint8_t* fetch = code + address * 12;
    const uint32_t w0 = LoadBE32(fetch);
    return !(w0 & 0x1F) &&
           VertexFetchIdentity(w0, LoadBE32(fetch + 4)) == element.fetch_identity;
  };
  if (matches(element.address)) return true;
  uint32_t cf_end = code_size;
  for (uint32_t offset = 0; offset + 12 <= cf_end; offset += 12) {
    const uint32_t w0 = LoadBE32(code + offset);
    const uint32_t w1 = LoadBE32(code + offset + 4);
    const uint32_t w2 = LoadBE32(code + offset + 8);
    const uint64_t cf[] = {uint64_t(w0) | uint64_t(w1 & 0xFFFF) << 32,
                           uint64_t(w1 >> 16) | uint64_t(w2) << 16};
    for (uint64_t instruction : cf) {
      const uint32_t opcode = uint32_t(instruction >> 44) & 15;
      if (!((opcode >= 1 && opcode <= 6) || opcode == 13 || opcode == 14)) continue;
      const uint32_t address = uint32_t(instruction) & 0xFFF;
      const uint32_t count = (uint32_t(instruction) >> 12) & 7;
      if (count) cf_end = std::min(cf_end, address * 12);
      const uint32_t sequence = (uint32_t(instruction) >> 16) & 0xFFF;
      for (uint32_t i = 0; i < count; ++i) {
        if ((sequence & (1u << (i * 2))) && matches(address + i)) {
          element.address = uint16_t(address + i);
          return true;
        }
      }
    }
  }
  return false;
}

// One vertex shader input as the Xenos microcode fetches it. UE3 binds no D3D
// vertex declaration for most draws: D3D patches each shader's vfetch
// instructions (format, offset, stride) when the shader is bound to a
// declaration, so the live microcode, not the container, carries the layout.
struct VertexFetchInput {
  uint8_t usage;
  uint8_t usage_index;
  uint8_t fetch_constant;  // 0..95; D3D stream s uses constant 95 - s
  uint8_t format;          // xenos::VertexFormat; 0 = not bound yet
  bool is_signed;
  bool is_normalized;
  uint32_t offset_dwords;
  uint32_t stride_dwords;
  int8_t exp_adjust;  // the fetched value is scaled by 2^exp_adjust
};

// Decodes element's vfetch from live shader code (big-endian words). A mini
// fetch reuses the fetch constant and stride of the full fetch before it.
inline bool DecodeVertexFetch(const uint8_t* code, const VertexElementRef& element,
                              VertexFetchInput& input) {
  const uint8_t* fetch = code + element.address * 12;
  const uint32_t w0 = LoadBE32(fetch), w1 = LoadBE32(fetch + 4), w2 = LoadBE32(fetch + 8);
  if ((w0 & 0x1F) != 0) return false;  // not a vertex fetch
  uint32_t full0 = w0, full2 = w2;
  for (uint32_t a = element.address; (LoadBE32(code + a * 12 + 4) >> 30) & 1;) {
    if (a == 0) return false;
    --a;
    full0 = LoadBE32(code + a * 12);
    full2 = LoadBE32(code + a * 12 + 8);
    if ((full0 & 0x1F) != 0) return false;
  }
  input.usage = element.usage;
  input.usage_index = element.usage_index;
  input.fetch_constant = uint8_t(((full0 >> 20) & 0x1F) * 3 + ((full0 >> 25) & 3));
  input.format = uint8_t((w1 >> 16) & 0x3F);
  input.is_signed = (w1 >> 12) & 1;
  input.is_normalized = !((w1 >> 13) & 1);
  input.offset_dwords = uint32_t(int32_t(w2 << 1) >> 9);  // 23-bit signed at bit 8
  input.stride_dwords = full2 & 0xFF;
  input.exp_adjust = int8_t(int32_t(w1 << 2) >> 26);  // 6-bit signed at bit 24
  return true;
}

// The declaration type (low 6 bits: vertex format; bit 8: signed; bit 9:
// integer, i.e. not normalized) matching a vfetch, for the host format tables.
constexpr const DeclType* FindDeclTypeForFetch(const VertexFetchInput& input) {
  const DeclType* by_format = nullptr;
  for (const DeclType& t : kDeclTypes) {
    if ((t.type & 0x3F) != input.format) continue;
    // D3DCOLOR adds a declaration swizzle. A live vfetch already carries that
    // swizzle in the instruction; its format alone is ordinary RGBA.
    if (t.type == 0x182886) continue;
    if (((t.type >> 8) & 1) == uint32_t(input.is_signed) &&
        ((t.type >> 9) & 1) == uint32_t(!input.is_normalized)) {
      return &t;
    }
    if (!by_format) by_format = &t;
  }
  return by_format;
}

// --- Render targets ---------------------------------------------------------

// Host format of a Xenos ColorRenderTargetFormat (RB_COLOR_INFO bits 16..19).
constexpr VkFormat ColorTargetFormat(uint32_t color_format) {
  switch (color_format) {
    case 2:   // k_2_10_10_10
    case 10:  // k_2_10_10_10_AS_10_10_10_10
      return VK_FORMAT_A2B10G10R10_UNORM_PACK32;
    case 3:   // k_2_10_10_10_FLOAT
    case 7:   // k_16_16_16_16_FLOAT
    case 12:  // k_2_10_10_10_FLOAT_AS_16_16_16_16
      return VK_FORMAT_R16G16B16A16_SFLOAT;
    // k_16_16 and k_16_16_16_16 hold -32..32 on Xenos; SNORM would clamp to
    // -1..1. Half floats keep the range and blend correctly; Emulated divides
    // the output by 32 into SNORM instead, which breaks blending (ticket 40).
    case 4:
      return VK_FORMAT_R16G16_SFLOAT;
    case 5:
      return VK_FORMAT_R16G16B16A16_SFLOAT;
    case 6:
      return VK_FORMAT_R16G16_SFLOAT;
    case 14:
      return VK_FORMAT_R32_SFLOAT;
    case 15:
      return VK_FORMAT_R32G32_SFLOAT;
    default:  // k_8_8_8_8, k_8_8_8_8_GAMMA
      return VK_FORMAT_R8G8B8A8_UNORM;
  }
}

// Native colour target for an EDRAM surface: base (RB_COLOR_INFO bits 0..11),
// pitch (RB_SURFACE_INFO bits 0..13) and host format. Guest formats with the
// same host format are views of one surface (k_8_8_8_8 and k_8_8_8_8_GAMMA),
// so they share an image: a game may clear and resolve through one view and
// draw through the other.
constexpr uint64_t ColorTargetKey(uint32_t surface_info, uint32_t color_info) {
  const uint64_t host_format = uint32_t(ColorTargetFormat((color_info >> 16) & 0xF));
  return uint64_t(color_info & 0xFFF) | host_format << 12 | uint64_t(surface_info & 0x3FFF) << 44;
}

// Surface objects: packed size (width - 1 in bits 31:18, height - 1 in 17:3).
struct SurfaceSize {
  uint32_t width;
  uint32_t height;
};
constexpr SurfaceSize DecodeSurfaceSize(uint32_t packed) {
  return {(packed >> 18) + 1, ((packed >> 3) & 0x7FFF) + 1};
}

// --- Texture fetch constants ------------------------------------------------

// Whether a texture fetch constant (dwords 0 and 1) names a texture: type 2, or
// type 0 with an address, which the game samples in entrance post-processing.
constexpr bool IsTextureFetch(uint32_t dword0, uint32_t dword1) {
  const uint32_t type = dword0 & 3;
  return type == 2 || (type == 0 && (dword1 >> 12) != 0);
}

// --- Clip space -------------------------------------------------------------

// How a draw's positions reach the target. The converted vertex shaders end
// with oPos.xy = oPos.xy * ndc_scale + offset * oPos.w, then the viewport
// (x_scale, x_offset, y_scale, y_offset: D3D's form, in guest pixels) maps
// clip space to pixels. With PA_CL_VTE_CNTL's scales on, positions are in clip
// space and the guest viewport is used as is. With them off, positions are
// already in pixels (the game's full-screen passes); Vulkan clips before the
// viewport, so the shader maps them to clip space for a full-target viewport.
struct ClipTransform {
  float ndc_scale[2];
  float offset[2];
  float x_scale, x_offset, y_scale, y_offset;
};

// half_pixel: shift by half a guest pixel right and down (D3D9 pixel centres).
inline ClipTransform ComputeClipTransform(uint32_t vte, float x_scale, float x_offset,
                                          float y_scale, float y_offset, float target_width,
                                          float target_height, bool half_pixel) {
  const float sx = (vte & 1) ? x_scale : 1.0f;
  const float ox = (vte & 2) ? x_offset : 0.0f;
  const float sy = (vte & 4) ? y_scale : 1.0f;
  const float oy = (vte & 8) ? y_offset : 0.0f;
  ClipTransform t;
  if ((vte & 1) && (vte & 4)) {
    t.ndc_scale[0] = t.ndc_scale[1] = 1.0f;
    t.offset[0] = half_pixel && sx != 0.0f ? 0.5f / sx : 0.0f;
    t.offset[1] = half_pixel && sy != 0.0f ? 0.5f / sy : 0.0f;
    t.x_scale = sx;
    t.x_offset = ox;
    t.y_scale = sy;
    t.y_offset = oy;
    return t;
  }
  // pixel = position / w * s + o on each axis, into a viewport of the whole
  // target (clip x -1..1 left to right, y 1..-1 top to bottom).
  const float half_w = target_width * 0.5f, half_h = target_height * 0.5f;
  t.ndc_scale[0] = sx / half_w;
  t.offset[0] = (ox - half_w) / half_w + (half_pixel ? 1.0f / target_width : 0.0f);
  t.ndc_scale[1] = -sy / half_h;
  t.offset[1] = (half_h - oy) / half_h - (half_pixel ? 1.0f / target_height : 0.0f);
  t.x_scale = half_w;
  t.x_offset = half_w;
  t.y_scale = -half_h;
  t.y_offset = half_h;
  return t;
}

// --- Index buffers ----------------------------------------------------------

// The range of vertices a run of indices touches, skipping the restart index.
struct IndexRange {
  uint32_t min = UINT32_MAX;
  uint32_t max = 0;
  bool empty() const { return min > max; }
};
inline IndexRange ScanIndices(const uint8_t* big_endian, uint32_t count, bool index32,
                              uint32_t* out) {
  const uint32_t reset = index32 ? 0xFFFFFFFFu : 0xFFFFu;
  IndexRange range;
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t index = index32 ? LoadBE32(big_endian + i * 4) : LoadBE16(big_endian + i * 2);
    out[i] = index;
    if (index != reset) {
      range.min = index < range.min ? index : range.min;
      range.max = index > range.max ? index : range.max;
    }
  }
  return range;
}

// Indices rebased to the first vertex of range, restart indices to all ones,
// quad lists split into two triangles each. Returns the count written.
inline uint32_t RebaseIndices(const uint32_t* indices, uint32_t count, bool index32, bool quads,
                              uint32_t min_index, uint32_t* out) {
  const uint32_t reset = index32 ? 0xFFFFFFFFu : 0xFFFFu;
  auto rebase = [&](uint32_t index) { return index == reset ? 0xFFFFFFFFu : index - min_index; };
  if (!quads) {
    for (uint32_t i = 0; i < count; ++i) {
      out[i] = rebase(indices[i]);
    }
    return count;
  }
  uint32_t o = 0;
  for (uint32_t i = 0; i + 3 < count; i += 4, o += 6) {
    const uint32_t* q = indices + i;
    out[o + 0] = rebase(q[0]);
    out[o + 1] = rebase(q[1]);
    out[o + 2] = rebase(q[2]);
    out[o + 3] = rebase(q[0]);
    out[o + 4] = rebase(q[2]);
    out[o + 5] = rebase(q[3]);
  }
  return o;
}

// Triangle indices for a non-indexed quad list of vertex_count vertices: two
// triangles per whole quad. Returns the count written (6 per quad).
inline uint32_t QuadListIndices(uint32_t vertex_count, uint32_t* out) {
  uint32_t o = 0;
  for (uint32_t v = 0; v + 3 < vertex_count; v += 4, o += 6) {
    const uint32_t quad[6] = {v, v + 1, v + 2, v, v + 2, v + 3};
    for (uint32_t i = 0; i < 6; ++i) out[o + i] = quad[i];
  }
  return o;
}

// What Native does with its frame for a window (graphics-remaster ticket 01).
// Every target draws at the Render resolution's multiple of the guest's 720p.
// A frame taller than the window's 16:9 area is filtered down to that area
// (supersampling at the display's resolution); otherwise it goes to the
// presenter, which scales it up and sharpens it.
enum class DownsampleFilter : uint8_t {
  kNone,
  kBox,       // power-of-two ratio: 2:1 steps only
  kBilinear,  // 2:1 steps while the ratio is 2 or more, then one bilinear step
};
struct OutputPlan {
  uint32_t target_scale = 1;
  // The image Native publishes.
  uint32_t output_width = 1280;
  uint32_t output_height = 720;
  bool downsample = false;
  DownsampleFilter filter = DownsampleFilter::kNone;
  uint32_t halvings = 0;  // 2:1 box steps before the final bilinear one
  float lod_bias = 0.0f;  // texture LOD bias for the render resolution
};
// Texture LOD bias for a render resolution: sharper mips as the frame grows.
constexpr float LodBiasFor(uint32_t render_scale) {
  return render_scale >= 3 ? -1.0f : render_scale == 2 ? -0.5f : 0.0f;
}
constexpr OutputPlan PlanOutput(uint32_t render_scale, uint32_t window_width,
                                uint32_t window_height) {
  OutputPlan plan;
  plan.target_scale = render_scale;
  plan.output_width = 1280 * render_scale;
  plan.output_height = 720 * render_scale;
  plan.lod_bias = LodBiasFor(render_scale);
  // The window's 16:9 area, where the presenter puts the frame.
  uint32_t fit_width = window_width, fit_height = window_height;
  if (uint64_t(window_width) * 9 > uint64_t(window_height) * 16) {
    fit_width = uint32_t(uint64_t(window_height) * 16 / 9);
  } else {
    fit_height = uint32_t(uint64_t(window_width) * 9 / 16);
  }
  // 720p goes to the presenter whatever the window: as before the remaster, and
  // scene runs keep their 1280x720 frames.
  if (render_scale <= 1 || fit_height == 0 || fit_width == 0 || plan.output_height <= fit_height) {
    return plan;
  }
  plan.downsample = true;
  while ((plan.output_height >> (plan.halvings + 1)) >= fit_height) {
    ++plan.halvings;
  }
  plan.filter = (plan.output_height >> plan.halvings) == fit_height &&
                        (plan.output_width >> plan.halvings) == fit_width
                    ? DownsampleFilter::kBox
                    : DownsampleFilter::kBilinear;
  plan.output_width = fit_width;
  plan.output_height = fit_height;
  return plan;
}

// CAS sharpening (image-quality ticket 03), an Enhancement: the SDK presenter's
// CAS applied to Native's output (default strength 0.5). Strength 0 leaves the
// presenter as configured; otherwise it is the CAS sharpness, clamped to [0, 1].
struct CasSetting {
  bool enabled = false;
  double sharpness = 0.0;
};
constexpr CasSetting CasEffect(double strength) {
  if (!(strength > 0.0)) {
    return {};
  }
  return {true, strength < 1.0 ? strength : 1.0};
}

}  // namespace wwe::native::guest
