// Fault finder (intel-fault-finder ticket 01, --wwe_native_debug_fault_finder):
// for the first frames that draw, Native submits and waits after every draw,
// so a lost device names the draw it was lost in. This is the log those
// draws leave; the renderer does the submitting. Tested in
// tests/native/test_fault_finder.cpp.

#pragma once

#include <cstdint>
#include <string>

#include <fmt/format.h>

namespace wwe::native {

struct FaultDraw {
  uint64_t frame = 0;
  std::string pass;  // PassName of the active target
  uint64_t vertex_shader = 0;
  uint64_t pixel_shader = 0;
  std::string vertex_format;  // strides and attributes
};

class FaultFinder {
 public:
  // frames: how many frames that draw it covers; 0 is off.
  explicit FaultFinder(uint32_t frames) : frames_(frames) {}

  // Whether draws in this frame are submitted one by one: the frame drew
  // already, or fewer than N frames have.
  bool Active(uint64_t frame) const {
    return (begun_ && frame == current_.frame) || frames_drawn_ < frames_;
  }

  // The line to log before the draw is submitted.
  std::string Begin(FaultDraw draw) {
    const bool same_frame = begun_ && draw.frame == current_.frame;
    index_ = same_frame ? index_ + 1 : 1;
    frames_drawn_ += !same_frame;
    current_ = std::move(draw);
    begun_ = true;
    current_finished_ = false;
    current_text_ = Describe(current_, index_);
    return "native fault finder: submit " + current_text_;
  }

  // The line to log once the draw's fence has signalled.
  std::string Finish() {
    current_finished_ = true;
    last_finished_ = current_text_;
    return fmt::format("native fault finder: finished frame {} draw {}", current_.frame, index_);
  }

  // What a GPU failure adds to its log line and the crash manifest: the last
  // draw that finished and the one in flight. Empty if no draw was logged.
  std::string Note() const {
    if (!begun_) {
      return {};
    }
    const std::string faulting =
        current_finished_
            ? fmt::format("none, the loss came after the last finished draw (the fault finder "
                          "covered frames: {})",
                          frames_)
            : current_text_;
    return "last finished draw: " + (last_finished_.empty() ? "none" : last_finished_) +
           "; faulting draw: " + faulting;
  }

 private:
  static std::string Describe(const FaultDraw& draw, uint32_t index) {
    return fmt::format("frame {} draw {} {} vs {:016x} ps {:016x} {}", draw.frame, index,
                       draw.pass, draw.vertex_shader, draw.pixel_shader, draw.vertex_format);
  }

  uint32_t frames_;
  uint32_t frames_drawn_ = 0;
  FaultDraw current_;
  uint32_t index_ = 0;
  bool begun_ = false;
  bool current_finished_ = false;
  std::string current_text_;
  std::string last_finished_;
};

}  // namespace wwe::native
