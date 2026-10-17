// The PC SETTINGS row in the game's own OPTIONS menu (in-game settings ticket
// 07; evidence and addresses in docs/research/options-menu-gfx.md). Three guest
// hooks around src/settings/options_row.h:
// - GASActionBufferData::Read (0x83134B60): fe_options' row list gets a 4th
//   key, "Options", as GFx parses the movie;
// - UE3 Localize (0x82600608): that row is named PC SETTINGS, and the
//   description lookups tell which row the cursor is on;
// - the GFx FSCommand callback (0x825CB4F0): A on that row opens the page.
// The same three hooks carry the main menu's EXIT TO DESKTOP row
// (src/settings/exit_row.h, docs/research/main-menu-gfx.md): fe_main's buffer
// is grown and patched, the row and its question are named, and the question's
// yes answer (FSCommand WWE_ExitToDesktop) closes the game.

#include "generated/default/wweallstars_pch.h"

#include <cstdint>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

#include <rex/hook.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>

#include "settings/exit_row.h"
#include "settings/options_row.h"
#include "settings/settings_page.h"

namespace {

std::mutex g_mutex;
wwe::settings::OptionsRow g_row;
// Guest copies of the row's texts (UTF-16BE, null-terminated), allocated once.
std::unordered_map<std::u16string, uint32_t> g_guest_texts;
std::function<void()> g_exit_to_desktop;

void StoreBE32(uint8_t* p, uint32_t value) {
  p[0] = uint8_t(value >> 24);
  p[1] = uint8_t(value >> 16);
  p[2] = uint8_t(value >> 8);
  p[3] = uint8_t(value);
}

uint32_t LoadBE32(const uint8_t* p) {
  return uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
}

// A guest TCHAR string (UTF-16BE), at most 128 characters.
std::u16string GuestWide(const uint8_t* base, uint32_t address) {
  std::u16string text;
  if (!address) {
    return text;
  }
  for (uint32_t i = 0; i < 128; ++i) {
    const uint8_t* c = base + address + i * 2;
    const char16_t ch = char16_t(c[0] << 8 | c[1]);
    if (!ch) break;
    text += ch;
  }
  return text;
}

std::string_view GuestString(const uint8_t* base, uint32_t address) {
  if (!address) {
    return {};
  }
  const char* text = reinterpret_cast<const char*>(base + address);
  return {text, strnlen(text, 256)};
}

uint32_t GuestText(uint8_t* base, const std::u16string& text) {
  auto it = g_guest_texts.find(text);
  if (it != g_guest_texts.end()) {
    return it->second;
  }
  const uint32_t size = uint32_t(text.size() + 1) * 2;
  const uint32_t address = REX_KERNEL_MEMORY()->SystemHeapAlloc(size);
  if (!address) {
    return 0;
  }
  for (size_t i = 0; i <= text.size(); ++i) {
    const char16_t ch = i < text.size() ? text[i] : u'\0';
    base[address + i * 2] = uint8_t(ch >> 8);
    base[address + i * 2 + 1] = uint8_t(ch);
  }
  g_guest_texts.emplace(text, address);
  return address;
}

}  // namespace

void wwe::settings::SetExitToDesktop(std::function<void()> exit) {
  std::lock_guard lock(g_mutex);
  g_exit_to_desktop = std::move(exit);
}

// GFx ArrayLH<UByte>::Resize(array, n): sets the count, reallocating the data
// as needed (the call GASActionBufferData::Read grows its buffer with).
DECLARE_REX_FUNC(sub_83102D20);

// GASActionBufferData::Read(this, stream): copies a DoAction's records into
// the array at this+0x10 (data) / this+0x14 (count).
DECLARE_REX_FUNC(sub_83134B60);
REX_HOOK_RAW(sub_83134B60) {
  const uint32_t self = ctx.r3.u32;
  __imp__sub_83134B60(ctx, base);
  const uint32_t data = LoadBE32(base + self + 0x10);
  const uint32_t count = LoadBE32(base + self + 0x14);
  if (data && wwe::settings::PatchOptionsRows(base + data, count)) {
    REXLOG_INFO("settings page: OPTIONS menu gets a PC SETTINGS row");
    return;
  }
  if (!data || count != wwe::settings::kMainMenuActionSize) {
    return;
  }
  const std::vector<uint8_t> patched = wwe::settings::PatchMainMenu(base + data, count);
  if (patched.empty()) {
    return;
  }
  const uint64_t result = ctx.r3.u64;
  ctx.r3.u64 = self + 0x10;
  ctx.r4.u64 = uint32_t(patched.size());
  sub_83102D20(ctx, base);
  ctx.r3.u64 = result;
  const uint32_t grown = LoadBE32(base + self + 0x10);
  if (!grown || LoadBE32(base + self + 0x14) != patched.size()) {
    REXLOG_WARN("main menu: action buffer could not grow; no EXIT TO DESKTOP row");
    StoreBE32(base + self + 0x14, count);  // the original bytes are still in place
    return;
  }
  std::memcpy(base + grown, patched.data(), patched.size());
  REXLOG_INFO("main menu gets an EXIT TO DESKTOP row");
}

// FString(const TCHAR*) constructor.
DECLARE_REX_FUNC(sub_82413660);

// UE3 Localize(Section, Key, Package, LangExt, bOptional), FString returned in
// the slot at r3.
DECLARE_REX_FUNC(sub_82600608);
REX_HOOK_RAW(sub_82600608) {
  const uint32_t slot = ctx.r3.u32;
  std::u16string text;
  {
    const std::u16string section = GuestWide(base, ctx.r4.u32);
    const std::u16string key = GuestWide(base, ctx.r5.u32);
    const std::u16string package = GuestWide(base, ctx.r6.u32);
    std::lock_guard lock(g_mutex);
    text = g_row.Text(section, key, package);
    if (text.empty()) {
      text = wwe::settings::ExitRowText(section, key, package);
    }
    if (!text.empty()) {
      const uint32_t guest_text = GuestText(base, text);
      if (guest_text) {
        ctx.r3.u64 = slot;
        ctx.r4.u64 = guest_text;
        sub_82413660(ctx, base);
        ctx.r3.u64 = slot;
        return;
      }
    }
  }
  __imp__sub_82600608(ctx, base);
}

// GFxFSCommandHandler::Callback(this, movieView, command, args).
DECLARE_REX_FUNC(sub_825CB4F0);
REX_HOOK_RAW(sub_825CB4F0) {
  const std::string_view command = GuestString(base, ctx.r5.u32);
  if (wwe::settings::IsExitToDesktop(command)) {
    std::function<void()> exit;
    {
      std::lock_guard lock(g_mutex);
      exit = g_exit_to_desktop;
    }
    REXLOG_INFO("main menu: EXIT TO DESKTOP confirmed, closing the game");
    if (exit) {
      exit();
    }
    return;  // the game has no handler for this command
  }
  bool open = false;
  {
    std::lock_guard lock(g_mutex);
    open = g_row.OnCommand(command, GuestString(base, ctx.r6.u32));
  }
  if (open) {
    wwe::settings::RequestOpen();
  }
  __imp__sub_825CB4F0(ctx, base);  // the select sound still plays
}
