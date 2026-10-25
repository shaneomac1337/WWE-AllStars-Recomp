#include <catch_amalgamated.hpp>

#include "match_music.h"

using wwe::match_music::TrackLoads;

// Issue #9 (.scratch/match-music/issues/04-restart-match-silences-music.md): the music
// sequence's stop chain unloads the "current track" variable 0.4 s after the music stops.
// After Restart Match the new match has already written its own track there and loaded it,
// so the chain's Unload lands on the new track.

namespace {
constexpr uint32_t kOldTrack = 0x4DA12080;  // MX_Gameplay_16, playing at the restart
constexpr uint32_t kNewTrack = 0x4DA12800;  // MX_Gameplay_08, picked by the restarted match
}  // namespace

TEST_CASE("the old stop chain's unload of the restarted match's new track is stale") {
  TrackLoads loads;
  loads.Loaded(kOldTrack);
  loads.Played(kOldTrack);
  loads.Unloaded(kOldTrack);  // the restart itself unloads the playing track
  loads.Loaded(kNewTrack);    // the new match's load runs before the 0.4 s delay ends
  CHECK(loads.IsStaleUnload(kNewTrack));
}

TEST_CASE("a track that has played is unloaded as normal") {
  TrackLoads loads;
  loads.Loaded(kNewTrack);
  loads.Played(kNewTrack);
  CHECK_FALSE(loads.IsStaleUnload(kNewTrack));
}

TEST_CASE("a track that was never loaded is not held back") {
  TrackLoads loads;
  CHECK_FALSE(loads.IsStaleUnload(kOldTrack));
}

TEST_CASE("an unload by any other path forgets the track") {
  TrackLoads loads;
  loads.Loaded(kNewTrack);
  loads.Unloaded(kNewTrack);  // match teardown, before the bell
  CHECK_FALSE(loads.IsStaleUnload(kNewTrack));
}

TEST_CASE("loading a track again after it played makes it fresh again") {
  TrackLoads loads;
  loads.Loaded(kOldTrack);
  loads.Played(kOldTrack);
  loads.Unloaded(kOldTrack);
  loads.Loaded(kOldTrack);  // the restarted match picks the same track
  CHECK(loads.IsStaleUnload(kOldTrack));
}
