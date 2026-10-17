// Crash report (CONTEXT.md, ADR-0025): the crashing game starts a second copy
// of itself with --wwe-write-crash-dump to write the dump. MiniDumpWriteDump
// called in the crashing process suspends the other threads and can then wait
// forever on a heap lock one of them holds; from another process it cannot.
// Pure: what goes on the helper's command line, and reading it back.

#pragma once

#include <charconv>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace wwe::crash {

inline constexpr std::string_view kWriteDumpSwitch = "--wwe-write-crash-dump";

struct DumpRequest {
  uint32_t process_id = 0;
  uint32_t thread_id = 0;
  uint64_t exception_pointers = 0;  // EXCEPTION_POINTERS* in the crashed process; 0 for none
  uint64_t guest_views[2] = {};     // left out of the dump (they hold game data)
  std::filesystem::path dump_path;
};

// The helper's arguments after the executable; the path is UTF-8.
inline std::vector<std::string> DumpRequestArgs(const DumpRequest& request) {
  const std::u8string path = request.dump_path.u8string();
  return {std::string(kWriteDumpSwitch),
          std::to_string(request.process_id),
          std::to_string(request.thread_id),
          std::to_string(request.exception_pointers),
          std::to_string(request.guest_views[0]),
          std::to_string(request.guest_views[1]),
          std::string(path.begin(), path.end())};
}

// The request in a process's arguments (UTF-8, the executable first), or none.
inline std::optional<DumpRequest> ParseDumpRequest(const std::vector<std::string>& args) {
  if (args.size() != 8 || args[1] != kWriteDumpSwitch) return std::nullopt;
  auto number = [](const std::string& text, auto& value) {
    const auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), value);
    return error == std::errc() && end == text.data() + text.size();
  };
  DumpRequest request;
  if (!number(args[2], request.process_id) || !number(args[3], request.thread_id) ||
      !number(args[4], request.exception_pointers) || !number(args[5], request.guest_views[0]) ||
      !number(args[6], request.guest_views[1]) || args[7].empty()) {
    return std::nullopt;
  }
  request.dump_path = std::filesystem::path(std::u8string(args[7].begin(), args[7].end()));
  return request;
}

}  // namespace wwe::crash
