// Match music after Restart Match (GitHub #9, .scratch/match-music/issues/04-restart-match-
// silences-music.md). The music sequence (AUDIO_Music_*) stops the track on a restart and,
// 0.4 s of game time later, unloads its "current track" variable through a
// SeqAct_FmodEventDynamicLoading. The restarted match picks and loads its own track into that
// variable 0.1 s after the restart, so the late Unload hits the new track and the match plays
// without music. Such an Unload is told apart by its track: loaded, and not played since.

#pragma once

#include <cstdint>
#include <vector>

namespace wwe::match_music {

// Which UFmodEvents have their sound data loaded but have not been played since.
class TrackLoads {
 public:
  void Loaded(uint32_t event);
  void Played(uint32_t event);
  void Unloaded(uint32_t event);

  // A music-sequence Unload of `event` is the previous track's stop chain: the restart has
  // already unloaded that track itself, so this one would only take the new track away.
  bool IsStaleUnload(uint32_t event) const;

 private:
  std::vector<uint32_t> fresh_;
};

}  // namespace wwe::match_music
