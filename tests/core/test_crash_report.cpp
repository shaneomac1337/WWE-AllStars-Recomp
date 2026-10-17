// Crash report (gpu-robustness ticket 02): what goes into the zip, and the
// manifest text the owner sorts reports by.

#include <catch_amalgamated.hpp>

#include <string>

#include "crash_report_plan.h"

using wwe::crash::CrashFacts;
using wwe::crash::PlanCrashReport;

namespace {

CrashFacts Crash() {
  CrashFacts facts;
  facts.version = "1.3.0";
  facts.commit = "15a52d9";
  facts.renderer = "native";
  facts.gpu_name = "Intel(R) Iris(R) Xe Graphics";
  facts.driver_version = "0x194B1A";
  facts.exception_status = 0xC0000005;
  facts.stamp = "20261016-233000";
  facts.logs_folder = "C:/wwe/logs";
  facts.session_log = "C:/wwe/logs/20261016-232900/runtime.log";
  facts.settings_file = "C:/wwe/userdata/wweallstars_settings.toml";
  facts.settings_exists = true;
  return facts;
}

}  // namespace

TEST_CASE("a crash report holds the dump, the session log, the settings file and a manifest") {
  const auto plan = PlanCrashReport(Crash());

  CHECK(plan.zip_path == std::filesystem::path("C:/wwe/logs/crash-20261016-233000.zip"));
  CHECK(plan.dump_name == "crash.dmp");
  REQUIRE(plan.files.size() == 2);
  CHECK(plan.files[0].name == "runtime.log");
  CHECK(plan.files[0].source == std::filesystem::path("C:/wwe/logs/20261016-232900/runtime.log"));
  CHECK(plan.files[1].name == "wweallstars_settings.toml");
  CHECK(plan.files[1].source ==
        std::filesystem::path("C:/wwe/userdata/wweallstars_settings.toml"));
  CHECK(plan.manifest_name == "manifest.txt");
  CHECK(plan.manifest ==
        "WWE All Stars Recomp crash report\n"
        "version: 1.3.0\n"
        "commit: 15a52d9\n"
        "renderer: native\n"
        "gpu: Intel(R) Iris(R) Xe Graphics\n"
        "driver: 0x194B1A\n"
        "reason: exception 0xC0000005\n"
        "session log: runtime.log\n"
        "settings file: wweallstars_settings.toml\n"
        "launcher log: none\n");
}

TEST_CASE("a crash report without a settings file says so and leaves it out") {
  CrashFacts facts = Crash();
  facts.settings_exists = false;

  const auto plan = PlanCrashReport(facts);

  REQUIRE(plan.files.size() == 1);
  CHECK(plan.files[0].name == "runtime.log");
  CHECK(plan.manifest.find("settings file: none\n") != std::string::npos);
}

TEST_CASE("a crash report from a start without a session log or GPU says so") {
  CrashFacts facts = Crash();
  facts.session_log.clear();
  facts.gpu_name.clear();
  facts.driver_version.clear();

  const auto plan = PlanCrashReport(facts);

  REQUIRE(plan.files.size() == 1);
  CHECK(plan.files[0].name == "wweallstars_settings.toml");
  CHECK(plan.manifest.find("gpu: unknown\ndriver: unknown\n") != std::string::npos);
  CHECK(plan.manifest.find("session log: none\n") != std::string::npos);
}

TEST_CASE("a GPU failure report gives the Vulkan result as its reason") {
  CrashFacts facts = Crash();
  facts.exception_status = 0;
  facts.gpu_failure = "VK_ERROR_DEVICE_LOST (vkQueueSubmit)";
  const auto plan = PlanCrashReport(facts);

  CHECK(plan.manifest.find("reason: gpu failure VK_ERROR_DEVICE_LOST (vkQueueSubmit)\n") !=
        std::string::npos);
  CHECK(plan.manifest.find("exception") == std::string::npos);
  CHECK(plan.dump_name == "crash.dmp");
}

TEST_CASE("a crash report holds the launcher log when the launcher wrote one") {
  CrashFacts facts = Crash();
  facts.launcher_log = "C:/wwe/logs/launcher.log";

  const auto plan = PlanCrashReport(facts);

  REQUIRE(plan.files.size() == 3);
  CHECK(plan.files[2].name == "launcher.log");
  CHECK(plan.files[2].source == std::filesystem::path("C:/wwe/logs/launcher.log"));
  CHECK(plan.manifest.find("launcher log: launcher.log\n") != std::string::npos);
  CHECK(PlanCrashReport(Crash()).manifest.find("launcher log: none\n") != std::string::npos);
}

TEST_CASE("a crash report names its files in UTF-8, whatever the code page") {
  CrashFacts facts = Crash();
  facts.settings_file = std::filesystem::path(u8"C:/hra/nastaven\u00ed \u5168.toml");

  const auto plan = PlanCrashReport(facts);

  REQUIRE(plan.files.size() == 2);
  CHECK(plan.files[1].name == "nastaven\xC3\xAD \xE5\x85\xA8.toml");
  CHECK(plan.manifest.find("settings file: nastaven\xC3\xAD \xE5\x85\xA8.toml\n") !=
        std::string::npos);
}

TEST_CASE("the logs in a crash report are capped, the settings file is not") {
  const auto plan = PlanCrashReport(Crash());

  REQUIRE(plan.files.size() == 2);
  CHECK(plan.files[0].max_bytes == wwe::crash::kMaxLogBytes);
  CHECK(plan.files[1].max_bytes == 0);
}

TEST_CASE("a capped log keeps its start and its end, with a note between") {
  std::vector<uint8_t> log;
  for (int i = 0; i < 1000; ++i) log.push_back(uint8_t('a' + i % 26));

  CHECK(wwe::crash::CapLog(log, 0) == log);
  CHECK(wwe::crash::CapLog(log, 1000) == log);

  const std::vector<uint8_t> capped = wwe::crash::CapLog(log, 400);
  const std::string text(capped.begin(), capped.end());
  const std::string original(log.begin(), log.end());
  CHECK(text.starts_with(original.substr(0, 100)));
  CHECK(text.ends_with(original.substr(700)));
  CHECK(text.find("\n[crash report: 600 bytes left out]\n") == 100);
}

TEST_CASE("a report from abort, a pure call or a bad CRT parameter names it") {
  CrashFacts facts = Crash();
  facts.exception_status = wwe::crash::kAbortCode;
  CHECK(PlanCrashReport(facts).manifest.find("reason: abort (exception 0xE0575701)\n") !=
        std::string::npos);
  facts.exception_status = wwe::crash::kPureCallCode;
  CHECK(PlanCrashReport(facts).manifest.find("reason: pure virtual call (exception 0xE0575702)\n") !=
        std::string::npos);
  facts.exception_status = wwe::crash::kInvalidParameterCode;
  CHECK(PlanCrashReport(facts).manifest.find(
            "reason: invalid CRT parameter (exception 0xE0575703)\n") != std::string::npos);
}
