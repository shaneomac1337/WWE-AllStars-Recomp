// UE3 object names read from guest memory, for hooks that match Kismet ops or assets by name
// (faster_starts.cpp, match_music_hooks.cpp). `base` is the guest's virtual_membase().

#pragma once

#include <cstdint>
#include <string>

#include "guest_memory.h"

namespace wwe::guest {

// GNames (the FName constructor 0x827522A0): a TArray of entry pointers at 0x83734554,
// its count after it; an entry's UTF-16 text starts at +0x10.
constexpr uint32_t kNamesData = 0x83734554;
constexpr uint32_t kNamesCount = 0x83734558;
// UObject: Outer at +0x20, Name (an FName's index, its number after it) at +0x24, Class at
// +0x2C. Read from live Matinees: SeqAct_Cinema_0 < Main_Sequence < PersistentLevel <
// TheWorld < Intro_Standard.
constexpr uint32_t kOuterOffset = 0x20;
constexpr uint32_t kNameOffset = 0x24;
constexpr uint32_t kClassOffset = 0x2C;
// Heap objects live at 0x40000000 and up.
constexpr uint32_t kHeapStart = 0x40000000;

inline std::string NameText(const uint8_t* base, uint32_t index) {
  if (index >= LoadU32(base, kNamesCount)) return {};
  const uint32_t entry = LoadU32(base, LoadU32(base, kNamesData) + 4 * index);
  if (entry < kHeapStart) return {};
  std::string text;
  for (uint32_t i = 0; i < 64; ++i) {
    const uint32_t at = entry + 0x10 + 2 * i;
    const uint16_t c = uint16_t(base[at] << 8 | base[at + 1]);
    if (c == 0) break;
    text += c < 0x80 ? char(c) : '?';
  }
  return text;
}

// The name of the package `object` was loaded from: its outermost Outer.
inline std::string PackageOf(const uint8_t* base, uint32_t object) {
  for (int depth = 0; depth < 16; ++depth) {
    const uint32_t outer = LoadU32(base, object + kOuterOffset);
    if (outer < kHeapStart) return NameText(base, LoadU32(base, object + kNameOffset));
    object = outer;
  }
  return {};
}

// The UObject's name, as UE3 prints it (Name_N for a numbered FName).
inline std::string ObjectName(const uint8_t* base, uint32_t object) {
  const uint32_t number = LoadU32(base, object + kNameOffset + 4);
  return NameText(base, LoadU32(base, object + kNameOffset)) +
         (number ? "_" + std::to_string(number - 1) : "");
}

inline std::string ClassName(const uint8_t* base, uint32_t object) {
  const uint32_t cls = LoadU32(base, object + kClassOffset);
  return cls >= kHeapStart ? ObjectName(base, cls) : std::string();
}

}  // namespace wwe::guest
