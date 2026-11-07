// Big-endian reads and writes of guest memory for the midasm and function hooks
// (game_tick_log.cpp, unlock_everything.cpp, game_bug_fixes.cpp, settings/options_hooks.cpp,
// online/step_rate_hooks.cpp, online/syncsim_stats_hook.cpp). `base` is the guest's virtual_membase().

#pragma once

#include <bit>
#include <cstdint>
#include <cstring>
#include <string_view>

namespace wwe::guest {

inline uint32_t LoadU32(const uint8_t* base, uint32_t address) {
  uint32_t value;
  std::memcpy(&value, base + address, sizeof(value));
  return std::byteswap(value);
}

inline void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  value = std::byteswap(value);
  std::memcpy(base + address, &value, sizeof(value));
}

// Writes the text and its terminator: (text.size() + 1) * 2 bytes.
inline void StoreU16String(uint8_t* base, uint32_t address, std::u16string_view text) {
  for (size_t i = 0; i <= text.size(); ++i) {
    const char16_t ch = i < text.size() ? text[i] : u'\0';
    base[address + i * 2] = uint8_t(ch >> 8);
    base[address + i * 2 + 1] = uint8_t(ch);
  }
}

}  // namespace wwe::guest
