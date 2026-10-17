// The EXIT TO DESKTOP row in the game's main menu (gpu-robustness ticket 07).
// docs/research/main-menu-gfx.md has the evidence: fe_main's dormant 8th row
// (DownloadableContent_xbox360) is always shown, its A branch calls an
// appended WWEExitQuestion(), which asks the game's own IssueQuestion popup,
// and its yes answer sends FSCommand WWE_ExitToDesktop. All but
// SetExitToDesktop is pure, so it is tested on its own
// (tests/core/test_exit_row.cpp); src/settings/options_hooks.cpp wires it to
// the game and defines SetExitToDesktop.

#pragma once

#include <cstdint>
#include <cstring>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace wwe::settings {

// fe_main's frame-1 action buffer: 8556 bytes, End last.
constexpr uint32_t kMainMenuActionSize = 8556;
constexpr uint32_t kMainMenuPatchedSize = 8865;

// Returns fe_main's action buffer with the exit row, its question and the
// grown End, or empty (and the input unread past the checks) unless the buffer
// is fe_main's and still holds the original bytes.
inline std::vector<uint8_t> PatchMainMenu(const uint8_t* actions, uint32_t size) {
  // Not; If -> 0x15CE, around `if (GetVar("DLC", "DLCMenuItem") == "on")`.
  constexpr uint32_t kDlcIf = 0x15A7;
  static constexpr uint8_t kIf[] = {0x12, 0x9D, 0x02, 0x00, 0x21, 0x00};
  // getURL("FSCommand:online_ExecCmd", "system=showMarketplace"): the DLC branch.
  constexpr uint32_t kDlcBranch = 0x11A4;
  static constexpr char kBranch[] =
      "\x83\x30\x00"
      "FSCommand:online_ExecCmd\0system=showMarketplace";  // + the literal's own null
  // push 0, "WWEExitQuestion"; callFunction; pop; push "x"*18; pop (filler)
  static constexpr char kNewBranch[] =
      "\x96\x16\x00\x07\x00\x00\x00\x00\x00WWEExitQuestion\0"
      "\x3D\x17"
      "\x96\x14\x00\x00xxxxxxxxxxxxxxxxxx\0\x17";
  // function WWEExitQuestion() {
  //   _global.IssueQuestion("GAME", Localize("ExitToDesktop_Title", "MainMenu"),
  //     Localize("ExitToDesktop_Body", "MainMenu"),
  //     Localize("Accept", "Common", "SystemMessages"),
  //     function () { getURL("FSCommand:WWE_ExitToDesktop", ""); },
  //     Localize("Cancel", "Common", "SystemMessages"), undefined);
  // }
  static constexpr char kQuestion[] =
      "\x9B\x14\x00WWEExitQuestion\0\x00\x00\x1E\x01"
      "\x96\x30\x00\x03\x00SystemMessages\0\x00"
      "Common\0\x00"
      "Cancel\0\x07\x03\x00\x00\x00\x00Localize\0\x3D"
      "\x9B\x05\x00\0\x00\x00\x20\x00"
      "\x83\x1D\x00"
      "FSCommand:WWE_ExitToDesktop\0\0"
      "\x96\x2F\x00\x00SystemMessages\0\x00"
      "Common\0\x00"
      "Accept\0\x07\x03\x00\x00\x00\x00Localize\0\x3D"
      "\x96\x2D\x00\x00MainMenu\0\x00"
      "ExitToDesktop_Body\0\x07\x02\x00\x00\x00\x00Localize\0\x3D"
      "\x96\x2E\x00\x00MainMenu\0\x00"
      "ExitToDesktop_Title\0\x07\x02\x00\x00\x00\x00Localize\0\x3D"
      "\x96\x14\x00\x00GAME\0\x07\x07\x00\x00\x00\x00_global\0\x1C"
      "\x96\x0F\x00\x00IssueQuestion\0\x52\x17";
  static_assert(sizeof(kBranch) == 51 && sizeof(kNewBranch) - 1 == 51);
  static_assert(kMainMenuActionSize - 1 + sizeof(kQuestion) - 1 + 1 == kMainMenuPatchedSize);

  if (size != kMainMenuActionSize || actions[size - 1] != 0x00 ||
      std::memcmp(actions + kDlcIf, kIf, sizeof(kIf)) != 0 ||
      std::memcmp(actions + kDlcBranch, kBranch, sizeof(kBranch)) != 0) {
    return {};
  }
  std::vector<uint8_t> patched(actions, actions + size - 1);  // without End
  std::memcpy(patched.data() + kDlcBranch, kNewBranch, sizeof(kNewBranch) - 1);
  patched[kDlcIf + 4] = 0x00;  // If -> 0x15AD: the push runs either way
  patched[kDlcIf + 5] = 0x00;
  patched.insert(patched.end(), kQuestion, kQuestion + sizeof(kQuestion) - 1);
  patched.push_back(0x00);  // End
  return patched;
}

// The game asks Localize(section, key, package). Returns the exit row's text
// for its keys, or empty to let the game answer.
inline std::u16string ExitRowText(std::u16string_view section, std::u16string_view key,
                                  std::u16string_view package) {
  if (section != u"MainMenu" || package != u"Screens") {
    return {};
  }
  constexpr std::u16string_view kRow = u"DownloadableContent_";
  constexpr std::u16string_view kDesc = u"_Desc";
  const bool desc = key.size() > kDesc.size() && key.substr(key.size() - kDesc.size()) == kDesc;
  if (key.substr(0, kRow.size()) == kRow) {
    return desc ? u"Quit WWE All Stars and return to the desktop." : u"EXIT TO DESKTOP";
  }
  if (key == u"ExitToDesktop_Title") {
    return u"EXIT TO DESKTOP";
  }
  if (key == u"ExitToDesktop_Body") {
    return u"Quit the game? Progress that has not been saved will be lost.";
  }
  return {};
}

// An FSCommand: true for the question's yes answer.
inline bool IsExitToDesktop(std::string_view command) {
  return command == "WWE_ExitToDesktop";
}

// Closes the game as the window's close button does; set by the app
// (src/wweallstars_app.h), called from the FSCommand hook.
void SetExitToDesktop(std::function<void()> exit);

}  // namespace wwe::settings
