#include "native/topup_converter.h"

#include <chrono>
#include <condition_variable>
#include <fstream>
#include <iterator>
#include <mutex>
#include <optional>
#include <string>
#include <system_error>
#include <thread>
#include <utility>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

#include <rex/cvar.h>
#include <rex/logging.h>

#include "native/pipelines.h"
#include "native/shader_convert_mode.h"
#include "native/shader_library.h"
#include "native/shader_topup.h"

REXCVAR_DEFINE_BOOL(wwe_live_shader_topups, true, "WWEAllStars",
                    "Convert shaders the shader pack lacks (the autosave icon, the splash) with "
                    "XenosRecomp during the session, so they show without a restart");
REXCVAR_DEFINE_STRING(wwe_shader_common, "", "WWEAllStars",
                      "shader_common.h for live shader top-ups (default: the release's xenosrecomp "
                      "folder beside game, then the dev build's)");

namespace wwe::native::topup_converter {

namespace {

namespace fs = std::filesystem;

// A conversion takes ~0.1 s; one that runs this long is stuck.
constexpr DWORD kTimeoutMs = 60'000;
constexpr char kWorkFolder[] = "live-work";

struct State {
  std::mutex mutex;
  std::condition_variable work;
  live_topup::Queue queue;
  bool enabled = false;
  bool started = false;
  fs::path folder;  // <cache_root>/shader-topup
  uint64_t converter_id = 0;
  live_topup::Converter converter;
#ifdef _WIN32
  HANDLE job = nullptr;  // children end with the game
#endif
};

State& S() {
  static State* state = new State;  // never destroyed: the thread is detached
  return *state;
}

fs::path GameExecutable() {
#ifdef _WIN32
  wchar_t path[MAX_PATH * 4];
  const DWORD length = GetModuleFileNameW(nullptr, path, DWORD(std::size(path)));
  return length ? fs::path(std::wstring(path, length)) : fs::path();
#else
  return {};
#endif
}

std::string ReadText(const fs::path& path) {
  std::ifstream file(path, std::ios::binary);
  return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

// Runs XenosRecomp on one folder; empty on success, else why not.
std::string RunConverter(const live_topup::Converter& converter, const fs::path& input,
                         const fs::path& output) {
#ifdef _WIN32
  std::error_code error;
  fs::create_directories(output / "hlsl", error);
  std::wstring command =
      L"\"" + converter.executable.wstring() + L"\" " + kConvertShadersFlag + L" ";
  for (const fs::path& argument :
       {input, output / "shader_cache.cpp", converter.include,
        output / "hlsl"}) {
    command += L"\"" + argument.wstring() + L"\" ";
  }
  SECURITY_ATTRIBUTES inherit = {sizeof(inherit), nullptr, TRUE};
  const HANDLE log = CreateFileW((output / "run.log").c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                                 &inherit, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  STARTUPINFOEXW startup = {};
  startup.StartupInfo.cb = sizeof(startup);
  startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
  startup.StartupInfo.hStdOutput = log;
  startup.StartupInfo.hStdError = log;
  startup.StartupInfo.hStdInput = nullptr;
  // The process starts inside the job (it ends with the game), and only the log handle is
  // inherited. Starting it suspended, joining the job and resuming is the pattern of process
  // injection, which Windows Defender's model reads as a trojan (Trojan:Win32/Bearfoos.B!ml).
  HANDLE job = S().job;
  HANDLE inherited[] = {log};
  SIZE_T attributes_size = 0;
  InitializeProcThreadAttributeList(nullptr, 2, 0, &attributes_size);
  std::vector<uint8_t> attributes(attributes_size);
  auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
  bool have_list = InitializeProcThreadAttributeList(list, 2, 0, &attributes_size);
  if (have_list && job) {
    have_list = UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job,
                                          sizeof(job), nullptr, nullptr);
  }
  if (have_list && log != INVALID_HANDLE_VALUE) {
    have_list = UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited,
                                          sizeof(inherited), nullptr, nullptr);
  }
  startup.lpAttributeList = have_list ? list : nullptr;
  PROCESS_INFORMATION process = {};
  const fs::path directory = converter.include.parent_path();
  const BOOL started = CreateProcessW(
      converter.executable.c_str(), command.data(), nullptr, nullptr, TRUE,
      CREATE_NO_WINDOW | (have_list ? EXTENDED_STARTUPINFO_PRESENT : 0), nullptr,
      directory.c_str(), &startup.StartupInfo, &process);
  const DWORD start_error = GetLastError();
  if (have_list) {
    DeleteProcThreadAttributeList(list);
  }
  if (log != INVALID_HANDLE_VALUE) {
    CloseHandle(log);
  }
  if (!started) {
    return "XenosRecomp did not start (error " + std::to_string(start_error) + ")";
  }
  if (job && !have_list) {
    AssignProcessToJobObject(job, process.hProcess);
  }
  CloseHandle(process.hThread);
  std::string why;
  if (WaitForSingleObject(process.hProcess, kTimeoutMs) != WAIT_OBJECT_0) {
    // XenosRecomp, not the game: ending it is safe.
    TerminateProcess(process.hProcess, 1);
    WaitForSingleObject(process.hProcess, 5'000);
    why = "XenosRecomp did not finish within " + std::to_string(kTimeoutMs / 1000) + " s";
  } else {
    DWORD code = 0;
    GetExitCodeProcess(process.hProcess, &code);
    if (code != 0) {
      why = "XenosRecomp stopped with exit code " + std::to_string(code);
    }
  }
  CloseHandle(process.hProcess);
  return why;
#else
  return "live conversion runs on Windows only";
#endif
}

// Converts one container; empty on success, else why not.
std::string Convert(const live_topup::Item& item) {
  State& s = S();
  const auto start = std::chrono::steady_clock::now();
  const fs::path work = s.folder / kWorkFolder / fs::path(item.name).stem();
  std::error_code error;
  fs::remove_all(work, error);
  fs::create_directories(work / "in", error);
  {
    std::ofstream file(work / "in" / item.name, std::ios::binary);
    file.write(reinterpret_cast<const char*>(item.container.data()),
               std::streamsize(item.container.size()));
  }
  std::string why = RunConverter(s.converter, work / "in", work / "out");
  live_topup::PackBuild built;
  if (why.empty()) {
    live_topup::ConverterOutput output{ReadText(work / "out" / "shader_cache.cpp"), {}};
    for (fs::directory_iterator it(work / "out" / "hlsl", error), end; !error && it != end;
         it.increment(error)) {
      output.hlsl.emplace_back(it->path().filename().string(), ReadText(it->path()));
    }
    built = live_topup::BuildPack(output, s.converter_id);
    why = built.error;
  }
  ShaderPackLoad load;
  if (why.empty()) {
    load = LoadShaderPack(built.bytes, s.converter_id);
    if (load.fallback != Fallback::kNone || load.pack.entries.size() != 1) {
      why = "the converted shader does not load: " + load.detail;
    }
  }
  uint64_t hash = 0;
  if (why.empty()) {
    hash = shader_library::AddLive(load.pack, item.container.data(), item.container.size(),
                                   item.is_pixel_shader);
    if (!hash) {
      why = "its shader module could not be built";
    }
  }
  if (why.empty()) {
    const size_t libraries = pipelines::PrepareShader(hash);
    shader_library::Publish(hash);
    // For the next session and the launcher: converted, nothing left to do.
    const bool saved = live_topup::SaveConverted(s.folder, item.name, built.bytes);
    REXLOG_INFO("shader top-up: converted {} live in {} ms ({} shader libraries{})", item.name,
                std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start)
                    .count(),
                libraries, saved ? "" : "; not saved, the launcher converts it again");
  }
  if (why.empty()) {
    fs::remove_all(work, error);
    fs::remove(work.parent_path(), error);  // only when no other work is left
  }
  return why;
}

void Work() {
  State& s = S();
  for (;;) {
    std::optional<live_topup::Item> item;
    {
      std::unique_lock lock(s.mutex);
      s.work.wait(lock, [&] { return (item = s.queue.Next()).has_value(); });
    }
    const std::string why = Convert(*item);
    if (!why.empty()) {
      REXLOG_WARN("shader top-up: could not convert {} live: {}; the launcher converts it after "
                  "the session",
                  item->name, why);
    }
    std::lock_guard lock(s.mutex);
    s.queue.Finish(item->name, why.empty());
    const live_topup::Counts counts = s.queue.counts();
    REXLOG_INFO("shader top-up: live conversions {} converted, {} failed, {} waiting",
                counts.converted, counts.failed, counts.queued);
  }
}

}  // namespace

void Configure(const fs::path& cache_root, uint64_t converter_id) {
  State& s = S();
  if (!REXCVAR_GET(wwe_live_shader_topups) || cache_root.empty()) {
    return;
  }
  const fs::path dev_include =
#ifdef WWE_DEV_XENOSRECOMP_INCLUDE
      WWE_DEV_XENOSRECOMP_INCLUDE;
#else
      fs::path();
#endif
  const auto converter = live_topup::FindConverter(
      GameExecutable(), fs::path(REXCVAR_GET(wwe_shader_common)), dev_include);
  if (!converter) {
    REXLOG_WARN("shader top-up: shader_common.h not found, so shaders the pack lacks wait for the "
                "launcher's next shader step");
    return;
  }
  std::lock_guard lock(s.mutex);
  s.folder = cache_root / ShaderTopUp::kFolderName;
  s.converter_id = converter_id;
  s.converter = *converter;
  std::error_code error;
  fs::remove_all(s.folder / kWorkFolder, error);  // left by a session that ended mid-conversion
#ifdef _WIN32
  s.job = CreateJobObjectW(nullptr, nullptr);
  if (s.job) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    SetInformationJobObject(s.job, JobObjectExtendedLimitInformation, &limits, sizeof(limits));
  }
#endif
  s.enabled = true;
  REXLOG_INFO("shader top-up: live conversion with {}", s.converter.include.string());
}

void Offer(const uint8_t* container, size_t size, bool is_pixel_shader) {
  State& s = S();
  std::lock_guard lock(s.mutex);
  if (!s.enabled) {
    return;
  }
  live_topup::Item item{ShaderTopUp::FileName(container, size, is_pixel_shader),
                        std::vector<uint8_t>(container, container + size), is_pixel_shader};
  if (!s.queue.Offer(std::move(item))) {
    return;
  }
  if (!s.started) {
    s.started = true;
    std::thread(Work).detach();
  }
  s.work.notify_one();
}

live_topup::Counts Counts() {
  State& s = S();
  std::lock_guard lock(s.mutex);
  return s.queue.counts();
}

}  // namespace wwe::native::topup_converter
