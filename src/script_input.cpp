// Scripted controller (see script_input.h). Ported and trimmed from
// wwe-svr2011-pc (KaikoClanworth1, port/src/script_input.cpp at de85161),
// reused with the author's permission.

#include "script_input.h"

#include <algorithm>
#include <charconv>
#include <cctype>
#include <cstring>
#include <fstream>
#include <sstream>

#include <rex/cvar.h>
#include <rex/input/device_assignment.h>
#include <rex/input/input_system.h>
#include <rex/logging.h>

REXCVAR_DEFINE_STRING(wwe_input_script, "", "WWEAllStars",
                      "Drive the game from pad commands appended to this file "
                      "(src/script_input.h); it becomes the only controller");
REXCVAR_DEFINE_BOOL(wwe_keyboard_player2, false, "WWEAllStars",
                    "With mnk_mode: the keyboard is player 2's controller instead of joining "
                    "player 1's, so one person with one pad can have a human player 2");
REXCVAR_DEFINE_STRING(wwe_input_script_p2, "", "WWEAllStars",
                      "With wwe_input_script: a second scripted pad (player 2) driven from this "
                      "file, so a scripted run can have a human player 2 who stands still");
REXCVAR_DEFINE_STRING(wwe_input_script_p3, "", "WWEAllStars",
                      "With wwe_input_script: a third scripted pad (player 3), as "
                      "wwe_input_script_p2");
REXCVAR_DEFINE_STRING(wwe_input_script_p4, "", "WWEAllStars",
                      "With wwe_input_script: a fourth scripted pad (player 4), as "
                      "wwe_input_script_p2");

namespace wwe {

using namespace rex::input;
using rex::X_RESULT;
using rex::X_STATUS;

namespace {

constexpr std::chrono::milliseconds kPollInterval{100};
// The neutral step after each press, so a repeated press registers; 7 frames
// is the frame-timed equivalent of 120 ms at 60 presents a second.
constexpr int kReleaseMs = 120;
constexpr uint64_t kReleaseFrames = 7;

std::string Upper(std::string text) {
  std::transform(text.begin(), text.end(), text.begin(),
                 [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
  return text;
}

bool IsRight(const std::string& side) { return Upper(side) == "R"; }

uint16_t ParseButtons(const std::string& spec) {
  static const struct {
    const char* name;
    uint16_t bit;
  } kNames[] = {
      {"UP", X_INPUT_GAMEPAD_DPAD_UP},       {"DOWN", X_INPUT_GAMEPAD_DPAD_DOWN},
      {"LEFT", X_INPUT_GAMEPAD_DPAD_LEFT},   {"RIGHT", X_INPUT_GAMEPAD_DPAD_RIGHT},
      {"START", X_INPUT_GAMEPAD_START},      {"BACK", X_INPUT_GAMEPAD_BACK},
      {"LS", X_INPUT_GAMEPAD_LEFT_THUMB},    {"RS", X_INPUT_GAMEPAD_RIGHT_THUMB},
      {"LB", X_INPUT_GAMEPAD_LEFT_SHOULDER}, {"RB", X_INPUT_GAMEPAD_RIGHT_SHOULDER},
      {"A", X_INPUT_GAMEPAD_A},              {"B", X_INPUT_GAMEPAD_B},
      {"X", X_INPUT_GAMEPAD_X},              {"Y", X_INPUT_GAMEPAD_Y},
  };
  uint16_t bits = 0;
  std::stringstream ss(spec);
  std::string name;
  while (std::getline(ss, name, '+')) {
    name = Upper(name);
    bool found = false;
    for (const auto& n : kNames) {
      if (name == n.name) {
        bits |= n.bit;
        found = true;
      }
    }
    if (!found) {
      REXLOG_WARN("script input: unknown button '{}'", name);
    }
  }
  return bits;
}

}  // namespace

// Reads an optional duration, "120" (ms) or "6f" (frames); without one the
// step keeps its default.
void ScriptInputDriver::ReadDuration(std::istream& in, Step& step) {
  std::string token;
  if (!(in >> token)) {
    return;
  }
  const bool frames = token.back() == 'f' || token.back() == 'F';
  const std::string_view number(token.data(), token.size() - (frames ? 1 : 0));
  int value = 0;
  const auto [end, error] = std::from_chars(number.data(), number.data() + number.size(), value);
  if (error != std::errc{} || end != number.data() + number.size() || value < 0) {
    REXLOG_WARN("script input: bad duration '{}'", token);
    return;
  }
  if (frames) {
    step.frames = static_cast<uint64_t>(value);
  } else {
    step.ms = value;
  }
}

namespace {

// SlotAssignment, except the keyboard (MnK, a synthetic device) is player 2.
class KeyboardPlayer2Assignment final : public DeviceAssignment {
 public:
  void OnDevicesChanged(const std::vector<DeviceInfo>& devices) override {
    for (auto& user : users_) {
      user.clear();
    }
    for (const auto& device : devices) {
      if (device.synthetic && device.name == "Keyboard and Mouse") {
        users_[1].push_back(device.id);
      } else if (device.synthetic) {
        users_[0].push_back(device.id);
      } else if (device.ordinal < kMaxGuestUsers) {
        users_[device.ordinal].push_back(device.id);
      }
    }
  }
  void DevicesForUser(uint32_t user_index, std::vector<DeviceId>& out) const override {
    out.clear();
    if (user_index < kMaxGuestUsers) {
      out = users_[user_index];
    }
  }

 private:
  std::vector<std::vector<DeviceId>> users_ = std::vector<std::vector<DeviceId>>(kMaxGuestUsers);
};

}  // namespace

void InstallScriptInput(rex::RuntimeConfig& config, std::function<uint64_t()> frames) {
  const std::filesystem::path file = REXCVAR_GET(wwe_input_script);
  if (file.empty()) {
    if (REXCVAR_GET(wwe_keyboard_player2)) {
      config.input_factory = [](bool tool_mode) -> std::unique_ptr<rex::system::IInputSystem> {
        auto input = CreateDefaultInputSystem(tool_mode);
        input->SetDeviceAssignment(std::make_unique<KeyboardPlayer2Assignment>());
        return input;
      };
    }
    return;
  }
  // XInput, SDL and MnK stay out, so a real pad or keyboard cannot disturb a
  // scripted run. The script is the only non-synthetic device: ordinal 0, user 0.
  config.input_factory = [file, frames](bool) -> std::unique_ptr<rex::system::IInputSystem> {
    auto input = std::make_unique<InputSystem>(nullptr);
    auto driver = std::make_unique<ScriptInputDriver>(file, ScriptInputDriver::SteadyClock,
                                                      frames);
    driver->Setup();
    input->AddDriver(std::move(driver));
    // Players 2-4 in order, so each pad's ordinal is its player's.
    const std::filesystem::path others[] = {REXCVAR_GET(wwe_input_script_p2),
                                            REXCVAR_GET(wwe_input_script_p3),
                                            REXCVAR_GET(wwe_input_script_p4)};
    for (uint32_t i = 0; i < std::size(others) && !others[i].empty(); ++i) {
      auto other = std::make_unique<ScriptInputDriver>(others[i], ScriptInputDriver::SteadyClock,
                                                       frames, ScriptInputDriver::PlayerDevice(i + 2));
      other->Setup();
      input->AddDriver(std::move(other));
    }
    input->SetDeviceAssignment(std::make_unique<SlotAssignment>());
    return input;
  };
}

ScriptInputDriver::ScriptInputDriver(std::filesystem::path command_file, Clock clock,
                                     FrameSource frames, DeviceId id)
    : InputDriver(/*window=*/nullptr, /*window_z_order=*/0),
      file_(std::move(command_file)),
      id_(id),
      clock_(std::move(clock)),
      frames_(std::move(frames)) {}

std::chrono::milliseconds ScriptInputDriver::SteadyClock() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
      std::chrono::steady_clock::now().time_since_epoch());
}

X_STATUS ScriptInputDriver::Setup() {
  REXLOG_INFO("script input: reading commands from {}", file_.string());
  return X_STATUS_SUCCESS;
}

void ScriptInputDriver::EnumerateDevices(std::vector<DeviceInfo>& out) {
  DeviceInfo info;
  info.id = id_;
  info.name = "Script";
  out.push_back(info);
}

void ScriptInputDriver::ParseLine(const std::string& line) {
  std::istringstream in(line);
  std::string verb;
  if (!(in >> verb) || verb[0] == '#') {
    return;
  }
  verb = Upper(verb);
  Step step{.source = line};
  if (verb == "PRESS" || verb == "HOLD") {
    std::string buttons;
    in >> buttons;
    step.buttons = ParseButtons(buttons);
    step.ms = kReleaseMs;
    ReadDuration(in, step);
    queue_.push_back(step);
    Step release{.ms = kReleaseMs};
    if (step.frames) {
      release.frames = kReleaseFrames;
    }
    queue_.push_back(release);
  } else if (verb == "STICK") {
    std::string side;
    int x = 0, y = 0;
    in >> side >> x >> y;
    ReadDuration(in, step);
    auto clamp = [](int v) { return static_cast<int16_t>(std::clamp(v, -32768, 32767)); };
    const bool right = IsRight(side);
    (right ? step.rx : step.lx) = clamp(x);
    (right ? step.ry : step.ly) = clamp(y);
    // Optional: buttons held meanwhile, and the right trigger ("stick L 0 32000 2000 LB 255").
    std::string buttons;
    if (in >> buttons && buttons != "-") {
      step.buttons = ParseButtons(buttons);
    }
    int rt = 0;
    if (in >> rt) {
      step.right_trigger = static_cast<uint8_t>(std::clamp(rt, 0, 255));
    }
    queue_.push_back(step);
  } else if (verb == "TRIGGER") {
    std::string side;
    int v = 0;
    in >> side >> v;
    ReadDuration(in, step);
    (IsRight(side) ? step.right_trigger : step.left_trigger) =
        static_cast<uint8_t>(std::clamp(v, 0, 255));
    queue_.push_back(step);
  } else if (verb == "WAIT") {
    ReadDuration(in, step);
    queue_.push_back(step);
  } else if (verb == "AT") {
    ReadDuration(in, step);
    if (!step.frames) {
      step.frames = static_cast<uint64_t>(step.ms);  // "at 600" means frame 600
    }
    step.at = true;
    queue_.push_back(step);
  } else {
    REXLOG_WARN("script input: unknown command '{}'", line);
    return;
  }
  REXLOG_INFO("script input: {}", line);
}

void ScriptInputDriver::PollFile(std::chrono::milliseconds now) {
  if (last_poll_ && now - *last_poll_ < kPollInterval) {
    return;
  }
  last_poll_ = now;
  std::ifstream f(file_, std::ios::binary);
  if (!f) {
    return;
  }
  f.seekg(0, std::ios::end);
  const size_t size = static_cast<size_t>(f.tellg());
  if (size < consumed_) {
    consumed_ = 0;  // the file was truncated: start over
  }
  if (size == consumed_) {
    return;
  }
  f.seekg(static_cast<std::streamoff>(consumed_));
  std::string text(size - consumed_, '\0');
  f.read(text.data(), static_cast<std::streamsize>(text.size()));
  // Only whole lines; a partly written last line waits for the next poll.
  const size_t end = text.rfind('\n');
  if (end == std::string::npos) {
    return;
  }
  consumed_ += end + 1;
  std::istringstream lines(text.substr(0, end));
  std::string line;
  while (std::getline(lines, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    ParseLine(line);
  }
}

ScriptInputDriver::Step ScriptInputDriver::CurrentStep(uint32_t* packet) {
  std::lock_guard lock(mutex_);
  *packet = ++packet_;
  const auto now = clock_();
  const uint64_t frame = frames_();
  PollFile(now);
  if (!running_ || Over(now, frame)) {
    running_ = !queue_.empty();
    if (running_) {
      current_ = queue_.front();
      queue_.pop_front();
      Start(now, frame);
      // An `at` whose frame is past holds nothing: go straight on.
      while (current_.at && Over(now, frame) && !queue_.empty()) {
        current_ = queue_.front();
        queue_.pop_front();
        Start(now, frame);
      }
    } else {
      current_ = Step{};
    }
  }
  return current_;
}

void ScriptInputDriver::Start(std::chrono::milliseconds now, uint64_t frame) {
  step_end_ = now + std::chrono::milliseconds(current_.ms);
  if (current_.frames) {
    frame_end_ = current_.at ? *current_.frames : frame + *current_.frames;
  }
  // The guest frame each command starts on, to compare runs (releases are
  // not logged).
  if (!current_.source.empty()) {
    REXLOG_INFO("script input: frame {}: {}", frame, current_.source);
  }
}

bool ScriptInputDriver::Over(std::chrono::milliseconds now, uint64_t frame) const {
  return current_.frames ? frame >= frame_end_ : now >= step_end_;
}

X_RESULT ScriptInputDriver::GetDeviceState(DeviceId id, X_INPUT_STATE* out_state) {
  if (id != id_) {
    return X_ERROR_DEVICE_NOT_CONNECTED;
  }
  uint32_t packet = 0;
  const Step s = CurrentStep(&packet);
  if (out_state) {
    std::memset(out_state, 0, sizeof(*out_state));
    out_state->packet_number = packet;
    out_state->gamepad.buttons = s.buttons;
    out_state->gamepad.left_trigger = s.left_trigger;
    out_state->gamepad.right_trigger = s.right_trigger;
    out_state->gamepad.thumb_lx = s.lx;
    out_state->gamepad.thumb_ly = s.ly;
    out_state->gamepad.thumb_rx = s.rx;
    out_state->gamepad.thumb_ry = s.ry;
  }
  return X_ERROR_SUCCESS;
}

X_RESULT ScriptInputDriver::GetDeviceCapabilities(DeviceId id, uint32_t,
                                                  X_INPUT_CAPABILITIES* out_caps) {
  if (id != id_) {
    return X_ERROR_DEVICE_NOT_CONNECTED;
  }
  if (out_caps) {
    std::memset(out_caps, 0, sizeof(*out_caps));
    out_caps->type = 0x01;      // XINPUT_DEVTYPE_GAMEPAD
    out_caps->sub_type = 0x01;  // XINPUT_DEVSUBTYPE_GAMEPAD
    out_caps->gamepad.buttons = 0xFFFF;
    out_caps->gamepad.left_trigger = 0xFF;
    out_caps->gamepad.right_trigger = 0xFF;
    out_caps->gamepad.thumb_lx = static_cast<int16_t>(0x7FFF);
    out_caps->gamepad.thumb_ly = static_cast<int16_t>(0x7FFF);
    out_caps->gamepad.thumb_rx = static_cast<int16_t>(0x7FFF);
    out_caps->gamepad.thumb_ry = static_cast<int16_t>(0x7FFF);
  }
  return X_ERROR_SUCCESS;
}

X_RESULT ScriptInputDriver::SetDeviceVibration(DeviceId id, X_INPUT_VIBRATION*) {
  return id == id_ ? X_ERROR_SUCCESS : X_ERROR_DEVICE_NOT_CONNECTED;
}

// Pad keystrokes do nothing in All Stars: its keystroke loop drops every
// virtual key above 0xFF (docs/research/scripted-input.md §3).
X_RESULT ScriptInputDriver::GetDeviceKeystroke(DeviceId id, uint32_t, X_INPUT_KEYSTROKE*) {
  return id == id_ ? X_ERROR_EMPTY : X_ERROR_DEVICE_NOT_CONNECTED;
}

}  // namespace wwe
