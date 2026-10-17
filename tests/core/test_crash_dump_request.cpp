// Crash report (gpu-robustness review 2026-10-17): the crashing game asks a
// second copy of itself to write the dump, because a dump written in process
// can deadlock on a heap lock a suspended thread holds.

#include <catch_amalgamated.hpp>

#include <string>
#include <vector>

#include "crash_dump_request.h"

using wwe::crash::DumpRequest;
using wwe::crash::DumpRequestArgs;
using wwe::crash::ParseDumpRequest;

TEST_CASE("a dump request survives the helper's command line") {
  DumpRequest request;
  request.process_id = 1234;
  request.thread_id = 5678;
  request.exception_pointers = 0x7FF612345678;
  request.guest_views[0] = 0x100000000;
  request.guest_views[1] = 0x200000000;
  request.dump_path = std::filesystem::path(u8"C:/hra/logs/crash-20261017-120000 \u00e9.dmp");

  std::vector<std::string> args = {"wweallstars.exe"};
  for (const std::string& arg : DumpRequestArgs(request)) args.push_back(arg);
  const auto parsed = ParseDumpRequest(args);

  CHECK(args[1] == "--wwe-write-crash-dump");
  REQUIRE(parsed);
  CHECK(parsed->process_id == 1234);
  CHECK(parsed->thread_id == 5678);
  CHECK(parsed->exception_pointers == 0x7FF612345678);
  CHECK(parsed->guest_views[0] == 0x100000000);
  CHECK(parsed->guest_views[1] == 0x200000000);
  CHECK(parsed->dump_path == request.dump_path);
}

TEST_CASE("no dump request without the switch, or with a malformed one") {
  CHECK_FALSE(ParseDumpRequest({"wweallstars.exe", "--headless"}));
  CHECK_FALSE(ParseDumpRequest({"wweallstars.exe", "--wwe-write-crash-dump", "12", "34"}));
  CHECK_FALSE(ParseDumpRequest(
      {"wweallstars.exe", "--wwe-write-crash-dump", "x", "1", "0", "0", "0", "C:/a.dmp"}));
}
