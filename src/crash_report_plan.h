// Crash report (CONTEXT.md, ADR-0025): what goes into the one zip the player
// attaches to an issue. Pure; the writer (crash_report.cpp) gathers the facts,
// writes the dump and the zip.

#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include <fmt/format.h>

namespace wwe::crash {

struct CrashFacts {
  std::string version;
  std::string commit;
  std::string renderer;        // "native" or "emulated"
  std::string gpu_name;        // empty before the GPU is known
  std::string driver_version;  // empty before the GPU is known
  uint32_t exception_status = 0;
  std::string gpu_failure;     // "<VkResult> (<call>)" for a GPU failure, else empty
  std::string stamp;           // local time, YYYYMMDD-HHMMSS
  std::filesystem::path logs_folder;
  std::filesystem::path session_log;    // empty without one
  std::filesystem::path launcher_log;   // empty without one
  std::filesystem::path settings_file;
  bool settings_exists = false;
};

// The exception codes the handler raises for exits that bypass the unhandled
// exception filter (abort, a pure virtual call, a bad CRT parameter), so they
// make a report too. The launcher names them (GameExitReport.CrashCodes).
inline constexpr uint32_t kAbortCode = 0xE0575701;
inline constexpr uint32_t kPureCallCode = 0xE0575702;
inline constexpr uint32_t kInvalidParameterCode = 0xE0575703;

inline std::string CrashReason(uint32_t code) {
  const char* what = code == kAbortCode              ? "abort"
                     : code == kPureCallCode         ? "pure virtual call"
                     : code == kInvalidParameterCode ? "invalid CRT parameter"
                                                     : nullptr;
  return what ? fmt::format("{} (exception 0x{:08X})", what, code)
              : fmt::format("exception 0x{:08X}", code);
}

// A log in the report keeps at most this much (story 11: a few MB, not hundreds).
inline constexpr uint64_t kMaxLogBytes = 4ull << 20;

// A file copied into the zip under its name (UTF-8); a log is capped with CapLog.
struct ReportFile {
  std::string name;
  std::filesystem::path source;
  uint64_t max_bytes = 0;  // 0: the whole file
};

// A log over `max_bytes` keeps its first quarter and its last three quarters
// (startup facts, and what led up to the crash) with a note between.
inline std::vector<uint8_t> CapLog(const std::vector<uint8_t>& log, uint64_t max_bytes) {
  if (max_bytes == 0 || log.size() <= max_bytes) return log;
  const size_t head = size_t(max_bytes / 4);
  const size_t tail = size_t(max_bytes) - head;
  const std::string note =
      fmt::format("\n[crash report: {} bytes left out]\n", log.size() - head - tail);
  std::vector<uint8_t> capped(log.begin(), log.begin() + head);
  capped.insert(capped.end(), note.begin(), note.end());
  capped.insert(capped.end(), log.end() - tail, log.end());
  return capped;
}

// A file name in UTF-8: path::string() goes through the ANSI code page and
// throws on a name it cannot map, which must never happen in a crash handler.
inline std::string Utf8Name(const std::filesystem::path& path) {
  const std::u8string name = path.filename().u8string();
  return std::string(name.begin(), name.end());
}

struct CrashReportPlan {
  std::filesystem::path zip_path;
  std::string dump_name;
  std::vector<ReportFile> files;
  std::string manifest_name;
  std::string manifest;
};

inline CrashReportPlan PlanCrashReport(const CrashFacts& facts) {
  auto or_unknown = [](const std::string& value) { return value.empty() ? "unknown" : value; };
  CrashReportPlan plan;
  plan.zip_path = facts.logs_folder / ("crash-" + facts.stamp + ".zip");
  plan.dump_name = "crash.dmp";
  plan.manifest_name = "manifest.txt";
  std::string log_name = "none";
  if (!facts.session_log.empty()) {
    log_name = Utf8Name(facts.session_log);
    plan.files.push_back({log_name, facts.session_log, kMaxLogBytes});
  }
  std::string settings_name = "none";
  if (facts.settings_exists) {
    settings_name = Utf8Name(facts.settings_file);
    plan.files.push_back({settings_name, facts.settings_file});
  }
  std::string launcher_log_name = "none";
  if (!facts.launcher_log.empty()) {
    launcher_log_name = Utf8Name(facts.launcher_log);
    plan.files.push_back({launcher_log_name, facts.launcher_log, kMaxLogBytes});
  }
  plan.manifest = fmt::format(
      "WWE All Stars Recomp crash report\n"
      "version: {}\ncommit: {}\nrenderer: {}\ngpu: {}\ndriver: {}\n"
      "reason: {}\nsession log: {}\nsettings file: {}\nlauncher log: {}\n",
      or_unknown(facts.version), or_unknown(facts.commit), or_unknown(facts.renderer),
      or_unknown(facts.gpu_name), or_unknown(facts.driver_version),
      facts.gpu_failure.empty() ? CrashReason(facts.exception_status)
                                : "gpu failure " + facts.gpu_failure,
      log_name, settings_name, launcher_log_name);
  return plan;
}

}  // namespace wwe::crash
