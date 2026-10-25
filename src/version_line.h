// The first runtime.log line (match-music ticket 01): the release and commit
// that wrote the log, the values the crash report manifest records. Tested in
// tests/core/test_version_line.cpp.

#pragma once

#include <string>
#include <string_view>

namespace wwe {

// "WWE All Stars Recomp 1.6.1, commit 7631c78"
inline std::string VersionLine(std::string_view version, std::string_view commit) {
  auto or_unknown = [](std::string_view value) {
    return std::string(value.empty() ? "unknown" : value);
  };
  return "WWE All Stars Recomp " + or_unknown(version) + ", commit " + or_unknown(commit);
}

}  // namespace wwe
