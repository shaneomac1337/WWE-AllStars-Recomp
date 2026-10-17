#include <catch_amalgamated.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "settings/exit_row.h"

using wwe::settings::ExitRowText;
using wwe::settings::IsExitToDesktop;
using wwe::settings::PatchMainMenu;

namespace {

std::vector<uint8_t> Hex(const std::string& hex) {
  std::vector<uint8_t> bytes;
  for (size_t i = 0; i + 1 < hex.size(); i += 2) {
    bytes.push_back(uint8_t(std::stoi(hex.substr(i, 2), nullptr, 16)));
  }
  return bytes;
}

std::vector<uint8_t> Text(const std::string& text) {
  return {text.begin(), text.end()};
}

// The two original regions of fe_main's frame-1 action buffer, as
// docs/research/main-menu-gfx.md "The movie patch" lists them.
const std::vector<uint8_t> kDlcIf = Hex("129d02002100");
std::vector<uint8_t> DlcBranch() {
  std::vector<uint8_t> branch = Hex("833000");
  const auto text = Text(std::string("FSCommand:online_ExecCmd") + '\0' +
                         "system=showMarketplace" + '\0');
  branch.insert(branch.end(), text.begin(), text.end());
  return branch;
}

// Every other byte is a pattern, so a stray write shows.
std::vector<uint8_t> FeMainBuffer() {
  std::vector<uint8_t> buffer(8556);
  for (size_t i = 0; i < buffer.size(); ++i) buffer[i] = uint8_t(0x40 + i % 61);
  buffer.back() = 0x00;  // End
  const auto branch = DlcBranch();
  std::copy(branch.begin(), branch.end(), buffer.begin() + 0x11A4);
  std::copy(kDlcIf.begin(), kDlcIf.end(), buffer.begin() + 0x15A7);
  return buffer;
}

// From the research doc: the 51-byte branch and the 309-byte appended function.
const std::vector<uint8_t> kNewBranch = Hex(
    "961600070000000000575745457869745175657374696f6e00"
    "3d17"
    "961400007878787878787878787878787878787878780017");
const std::vector<uint8_t> kQuestion = Hex(
    "9b1400575745457869745175657374696f6e0000001e01963000030053797374656d4d6573"
    "73616765730000436f6d6d6f6e000043616e63656c000703000000004c6f63616c697a6500"
    "3d9b05000000002000831d004653436f6d6d616e643a5757455f45786974546f4465736b74"
    "6f700000962f000053797374656d4d657373616765730000436f6d6d6f6e00004163636570"
    "74000703000000004c6f63616c697a65003d962d00004d61696e4d656e7500004578697454"
    "6f4465736b746f705f426f6479000702000000004c6f63616c697a65003d962e00004d6169"
    "6e4d656e75000045786974546f4465736b746f705f5469746c65000702000000004c6f6361"
    "6c697a65003d9614000047414d45000707000000005f676c6f62616c001c960f0000497373"
    "75655175657374696f6e005217");

}  // namespace

TEST_CASE("fe_main gets the exit row, its question, and nothing else changes") {
  REQUIRE(kNewBranch.size() == 51);
  REQUIRE(kQuestion.size() == 309);
  const std::vector<uint8_t> original = FeMainBuffer();
  const std::vector<uint8_t> patched = PatchMainMenu(original.data(), uint32_t(original.size()));
  REQUIRE(patched.size() == 8865);

  std::vector<uint8_t> expected(original.begin(), original.end() - 1);  // without End
  std::copy(kNewBranch.begin(), kNewBranch.end(), expected.begin() + 0x11A4);
  expected[0x15AB] = 0x00;  // If -> 0x15CE becomes If -> 0x15AD: the DLC row always shows
  expected[0x15AC] = 0x00;
  expected.insert(expected.end(), kQuestion.begin(), kQuestion.end());
  expected.push_back(0x00);  // End at 0x22A0
  CHECK(patched == expected);
  CHECK(patched[0x22A0] == 0x00);
}

TEST_CASE("other action buffers are not patched") {
  std::vector<uint8_t> other(4587, 0x00);
  CHECK(PatchMainMenu(other.data(), uint32_t(other.size())).empty());

  std::vector<uint8_t> changed_if = FeMainBuffer();
  changed_if[0x15A9] = 0x03;
  CHECK(PatchMainMenu(changed_if.data(), uint32_t(changed_if.size())).empty());

  std::vector<uint8_t> changed_branch = FeMainBuffer();
  changed_branch[0x11A4 + 20] = 'X';  // a different FSCommand
  CHECK(PatchMainMenu(changed_branch.data(), uint32_t(changed_branch.size())).empty());

  std::vector<uint8_t> no_end = FeMainBuffer();
  no_end.back() = 0x07;
  CHECK(PatchMainMenu(no_end.data(), uint32_t(no_end.size())).empty());

  const std::vector<uint8_t> once = FeMainBuffer();
  const auto patched = PatchMainMenu(once.data(), uint32_t(once.size()));
  CHECK(PatchMainMenu(patched.data(), uint32_t(patched.size())).empty());  // once only
}

TEST_CASE("the DLC row reads EXIT TO DESKTOP, in the main menu only") {
  CHECK(ExitRowText(u"MainMenu", u"DownloadableContent_xbox360", u"Screens") == u"EXIT TO DESKTOP");
  CHECK(ExitRowText(u"MainMenu", u"DownloadableContent_win32", u"Screens") == u"EXIT TO DESKTOP");
  CHECK_FALSE(ExitRowText(u"MainMenu", u"DownloadableContent_xbox360_Desc", u"Screens").empty());
  CHECK(ExitRowText(u"MainMenu", u"ExitToDesktop_Title", u"Screens") == u"EXIT TO DESKTOP");
  CHECK_FALSE(ExitRowText(u"MainMenu", u"ExitToDesktop_Body", u"Screens").empty());
  CHECK(ExitRowText(u"MainMenu", u"Exhibition", u"Screens").empty());  // the game's own rows
  CHECK(ExitRowText(u"MainMenu", u"Exhibition_Desc", u"Screens").empty());
  CHECK(ExitRowText(u"Options", u"DownloadableContent_xbox360", u"Screens").empty());
  CHECK(ExitRowText(u"MainMenu", u"DownloadableContent_xbox360", u"Other").empty());
}

TEST_CASE("only the question's yes answer exits") {
  CHECK(IsExitToDesktop("WWE_ExitToDesktop"));
  CHECK_FALSE(IsExitToDesktop("online_ExecCmd"));
  CHECK_FALSE(IsExitToDesktop("Call_Kismet"));
  CHECK_FALSE(IsExitToDesktop("WWE_ExitToDesktopX"));
  CHECK_FALSE(IsExitToDesktop(""));
}
