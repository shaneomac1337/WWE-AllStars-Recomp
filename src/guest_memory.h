// Big-endian reads and writes of guest memory for the midasm and function hooks
// (game_tick_log.cpp, unlock_everything.cpp, online/step_rate_hooks.cpp,
// online/syncsim_stats_hook.cpp). `base` is the guest's virtual_membase().

#pragma once

#include <bit>
#include <cstdint>
#include <cstring>

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

}  // namespace wwe::guest
