#include "match_music.h"

#include <algorithm>

namespace wwe::match_music {

void TrackLoads::Loaded(uint32_t event) {
  if (!IsStaleUnload(event)) fresh_.push_back(event);
}

void TrackLoads::Played(uint32_t event) { std::erase(fresh_, event); }

void TrackLoads::Unloaded(uint32_t event) { std::erase(fresh_, event); }

bool TrackLoads::IsStaleUnload(uint32_t event) const {
  return std::find(fresh_.begin(), fresh_.end(), event) != fresh_.end();
}

}  // namespace wwe::match_music
