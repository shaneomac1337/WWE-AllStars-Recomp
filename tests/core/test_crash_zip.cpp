// Crash report zip (gpu-robustness review 2026-10-17): UTF-8 names, the
// report's own time on each entry, and a failed write that the writer reports.

#include <catch_amalgamated.hpp>

#include <cstdint>
#include <ctime>
#include <string>
#include <vector>

#include "crash_zip.h"

using wwe::crash::StoredZipWriter;

namespace {

uint32_t U16(const std::vector<uint8_t>& v, size_t at) { return v[at] | v[at + 1] << 8; }

std::tm Time() {
  std::tm time{};
  time.tm_year = 2026 - 1900;
  time.tm_mon = 10 - 1;
  time.tm_mday = 17;
  time.tm_hour = 14;
  time.tm_min = 5;
  time.tm_sec = 31;
  return time;
}

}  // namespace

TEST_CASE("a crash report zip flags its names as UTF-8 and dates them with the report's time") {
  std::vector<uint8_t> out;
  StoredZipWriter zip([&](const uint8_t* data, size_t size) {
    out.insert(out.end(), data, data + size);
    return true;
  }, Time());

  REQUIRE(zip.Add("nastaven\xC3\xAD.toml", {'a', 'b'}));
  REQUIRE(zip.Add("manifest.txt", {'c'}));
  REQUIRE(zip.Finish());

  CHECK(U16(out, 0) == 0x4B50);
  CHECK(U16(out, 6) == 0x0800);                                // UTF-8 names
  CHECK(U16(out, 10) == (14u << 11 | 5u << 5 | 31u / 2));      // time
  CHECK(U16(out, 12) == ((2026u - 1980u) << 9 | 10u << 5 | 17u));  // date
  CHECK(std::string(out.begin() + 30, out.begin() + 30 + 15) == "nastaven\xC3\xAD.toml");
  // The end record counts both entries.
  const size_t end = out.size() - 22;
  CHECK(U16(out, end) == 0x4B50);
  CHECK(U16(out, end + 10) == 2);
}

TEST_CASE("a crash report zip reports a write that failed, and every write after it") {
  int writes = 0;
  // The entry's data (the second write) does not fit.
  StoredZipWriter zip([&](const uint8_t*, size_t) { return ++writes != 2; }, Time());

  CHECK_FALSE(zip.Add("crash.dmp", std::vector<uint8_t>(64, 0x5A)));
  CHECK_FALSE(zip.Add("manifest.txt", {'c'}));
  CHECK_FALSE(zip.Finish());
}
