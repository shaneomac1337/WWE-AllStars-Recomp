// Fault finder (intel-fault-finder ticket 01): per-draw log lines and the note
// a GPU failure carries into runtime.log and the crash manifest.

#include <catch_amalgamated.hpp>

#include "native/fault_finder.h"

using wwe::native::FaultDraw;
using wwe::native::FaultFinder;

namespace {

FaultDraw Draw(uint64_t frame, uint64_t pixel_shader = 0xBBBB) {
  FaultDraw draw;
  draw.frame = frame;
  draw.pass = "pass 1280x720 RGBA8 +depth";
  draw.vertex_shader = 0xAAAA;
  draw.pixel_shader = pixel_shader;
  draw.vertex_format = "strides 32 loc0=fmt38@s0+0";
  return draw;
}

}  // namespace

TEST_CASE("a draw is logged with its frame, index, pass, shaders and vertex format") {
  FaultFinder finder(3);
  CHECK(finder.Begin(Draw(7)) ==
        "native fault finder: submit frame 7 draw 1 pass 1280x720 RGBA8 +depth "
        "vs 000000000000aaaa ps 000000000000bbbb strides 32 loc0=fmt38@s0+0");
  CHECK(finder.Finish() == "native fault finder: finished frame 7 draw 1");
}

TEST_CASE("the finder covers the first N frames that draw, then stops") {
  FaultFinder finder(2);
  CHECK(finder.Active(5));
  finder.Begin(Draw(5));
  finder.Finish();
  CHECK(finder.Active(5));
  CHECK(finder.Active(9));  // frames 6 to 8 drew nothing
  finder.Begin(Draw(9));
  CHECK(finder.Begin(Draw(9)).starts_with("native fault finder: submit frame 9 draw 2 "));
  CHECK(finder.Active(9));
  CHECK_FALSE(finder.Active(10));
}

TEST_CASE("an off finder covers no frame") {
  CHECK_FALSE(FaultFinder(0).Active(0));
}

TEST_CASE("a loss during a draw names the last finished draw and the faulting one") {
  FaultFinder finder(3);
  finder.Begin(Draw(4, 0x11));
  finder.Finish();
  finder.Begin(Draw(4, 0x22));
  CHECK(finder.Note() ==
        "last finished draw: frame 4 draw 1 pass 1280x720 RGBA8 +depth vs 000000000000aaaa "
        "ps 0000000000000011 strides 32 loc0=fmt38@s0+0; faulting draw: frame 4 draw 2 "
        "pass 1280x720 RGBA8 +depth vs 000000000000aaaa ps 0000000000000022 strides 32 "
        "loc0=fmt38@s0+0");
}

TEST_CASE("a loss in the first draw has no finished draw") {
  FaultFinder finder(3);
  finder.Begin(Draw(4));
  CHECK(finder.Note().starts_with("last finished draw: none; faulting draw: frame 4 draw 1 "));
}

TEST_CASE("a loss after every logged draw finished points past the last one") {
  FaultFinder finder(1);
  finder.Begin(Draw(4));
  finder.Finish();
  CHECK(finder.Note().ends_with("; faulting draw: none, the loss came after the last finished "
                                "draw (the fault finder covered frames: 1)"));
}

TEST_CASE("without a logged draw there is no note") {
  CHECK(FaultFinder(3).Note().empty());
  CHECK(FaultFinder(0).Note().empty());
}
