// Scripted controller for automated runs.
//
// Ported and trimmed from wwe-svr2011-pc (KaikoClanworth1,
// port/src/script_input.h at de85161), reused with the author's permission
// (docs/research/svr2011-pc-port.md §1).
//
// With --wwe_input_script=<path>, the game's only controller is a virtual pad
// driven by commands appended to that file, one per line, while the game runs:
//
//   press <buttons> [t]         hold buttons (default 120 ms), then release
//   hold <buttons> <t>          same, explicit duration
//   stick <L|R> <x> <y> <t> [buttons|-] [rt]
//                               hold a stick at x,y (-32768..32767), with
//                               optional buttons and right trigger meanwhile
//   trigger <L|R> <0-255> <t>   hold a trigger
//   wait <t>                    neutral pad
//   at <frame>                  neutral pad until the guest has presented
//                               <frame> frames; nothing if that is past
//
// A duration <t> is milliseconds (120) or guest frames with an 'f' suffix
// (6f). A frame step ends when the Present count reaches its start + n; the
// release after a frame-timed press lasts 7f instead of 120 ms. The frame
// count is the one --wwe_native_dump_frame numbers dumps by, so `at` anchors a
// scene to absolute frames and waits do not build up drift.
//
// Frame caveat for scene authors: the guest presents and polls the pad on
// different threads, so a frame step can end one frame early or late (±1).
// Capture a few frames after the input that leads to a shot.
//
// <buttons> is one or more of A B X Y START BACK LB RB LS RS UP DOWN LEFT RIGHT
// joined with '+'. Lines starting with '#' are ignored, unknown verbs are
// logged and skipped. Each step replaces the whole pad state of the one
// before. A step starts at the first guest poll after the previous one ends.

#pragma once

#include <chrono>
#include <deque>
#include <filesystem>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include <rex/input/input_driver.h>
#include <rex/runtime.h>

namespace wwe {

// With --wwe_input_script set, replace the runtime's input system with one
// whose only device is a ScriptInputDriver reading that file (user 0), timing
// frame steps by `frames`. Without it, leave the config alone.
void InstallScriptInput(rex::RuntimeConfig& config, std::function<uint64_t()> frames);

class ScriptInputDriver final : public rex::input::InputDriver {
 public:
  // Monotonic time; tests pass a hand-advanced one.
  using Clock = std::function<std::chrono::milliseconds()>;
  // Frames the guest has presented (wwe::PresentCount in the game).
  using FrameSource = std::function<uint64_t()>;

  static constexpr auto kDevice = static_cast<rex::input::DeviceId>(0x53435254);  // 'SCRT'
  // Players 2-4's pads (--wwe_input_script_p2..p4), each from its own command file.
  static constexpr rex::input::DeviceId PlayerDevice(uint32_t player) {
    return static_cast<rex::input::DeviceId>(0x53435230 + player);  // 'SCR2'..'SCR4'
  }

  ScriptInputDriver(std::filesystem::path command_file, Clock clock, FrameSource frames,
                    rex::input::DeviceId id = kDevice);

  static std::chrono::milliseconds SteadyClock();

  rex::X_STATUS Setup() override;
  std::string_view vibration_backend() const override { return "script"; }
  void EnumerateDevices(std::vector<rex::input::DeviceInfo>& out) override;
  rex::X_RESULT GetDeviceState(rex::input::DeviceId id,
                               rex::input::X_INPUT_STATE* out_state) override;
  rex::X_RESULT GetDeviceCapabilities(rex::input::DeviceId id, uint32_t flags,
                                      rex::input::X_INPUT_CAPABILITIES* out_caps) override;
  rex::X_RESULT SetDeviceVibration(rex::input::DeviceId id,
                                   rex::input::X_INPUT_VIBRATION* vibration) override;
  rex::X_RESULT GetDeviceKeystroke(rex::input::DeviceId id, uint32_t flags,
                                   rex::input::X_INPUT_KEYSTROKE* out_keystroke) override;

 private:
  struct Step {
    uint16_t buttons = 0;
    uint8_t left_trigger = 0, right_trigger = 0;
    int16_t lx = 0, ly = 0, rx = 0, ry = 0;
    int ms = 0;
    // Frame timing instead of ms: a length, or with `at`, an absolute frame.
    std::optional<uint64_t> frames;
    bool at = false;
    std::string source;  // the command line, logged when the step starts
  };

  // Starts current_ at now / frame; Over says whether its time is up.
  void Start(std::chrono::milliseconds now, uint64_t frame);
  bool Over(std::chrono::milliseconds now, uint64_t frame) const;
  void PollFile(std::chrono::milliseconds now);
  void ParseLine(const std::string& line);
  static void ReadDuration(std::istream& in, Step& step);
  // The step in force now, and the next packet number, under the lock.
  Step CurrentStep(uint32_t* packet);

  std::filesystem::path file_;
  rex::input::DeviceId id_;
  Clock clock_;
  FrameSource frames_;
  size_t consumed_ = 0;  // bytes of the file already parsed
  std::optional<std::chrono::milliseconds> last_poll_;
  std::mutex mutex_;
  std::deque<Step> queue_;
  bool running_ = false;
  Step current_{};
  std::chrono::milliseconds step_end_{0};
  uint64_t frame_end_ = 0;
  uint32_t packet_ = 0;
};

}  // namespace wwe
