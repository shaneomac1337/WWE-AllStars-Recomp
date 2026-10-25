#include "crash_report.h"

#include <atomic>
#include <csignal>
#include <cstdlib>
#include <ctime>
#include <iterator>
#include <mutex>
#include <optional>
#include <vector>

#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/logging.h>

#include "crash_dump_request.h"
#include "crash_report_plan.h"
#include "crash_zip.h"
#include "native/gpu_failure.h"
#include "native/native_renderer.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
// clang-format off
#include <dbghelp.h>
#include <shellapi.h>
// clang-format on

REXCVAR_DEFINE_BOOL(wwe_debug_force_crash, false, "WWEAllStars",
                    "Debugging: crash at the first frame, to check the crash report");
REXCVAR_DEFINE_BOOL(wwe_debug_force_abort, false, "WWEAllStars",
                    "Debugging: call abort at the first frame, to check its crash report");

namespace wwe::crash {
namespace {

std::mutex g_mutex;
std::filesystem::path g_settings_file;
std::string g_gpu_name;
std::string g_driver_version;
const uint8_t* g_guest_views[2] = {};
std::atomic<HWND> g_window{nullptr};
// The thread writing the one report of this process; 0 while none is.
std::atomic<DWORD> g_reporting_thread{0};
// Set once the report is on disk (its message may stay up a long while).
std::atomic<bool> g_report_written{false};

enum class Claim { kOwn, kSameThread };

// One report per process: a second crash, or a GPU failure racing a crash,
// would share its stamp and overwrite it, or end the process while the first
// still writes. Another thread waits here for the first report to end the
// process; the same thread (a crash inside the report) gets kSameThread. A
// report still not written after a minute is stuck (the second crash may hold
// a lock it needs), so the waiter ends the process with `exit_code`.
Claim ClaimReport(uint32_t exit_code) {
  const DWORD self = GetCurrentThreadId();
  DWORD owner = 0;
  if (g_reporting_thread.compare_exchange_strong(owner, self)) return Claim::kOwn;
  if (owner == self) return Claim::kSameThread;
  for (int second = 0; second < 60 && !g_report_written; ++second) Sleep(1000);
  if (!g_report_written) TerminateProcess(GetCurrentProcess(), exit_code);
  Sleep(INFINITE);
  return Claim::kSameThread;
}

std::vector<uint8_t> ReadAll(const std::filesystem::path& path) {
  // Shared read: the logger still holds the session log open.
  HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  std::vector<uint8_t> data;
  if (file == INVALID_HANDLE_VALUE) return data;
  LARGE_INTEGER size{};
  if (GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart < (1ll << 30)) {
    data.resize(size_t(size.QuadPart));
    DWORD read = 0;
    if (!ReadFile(file, data.data(), DWORD(data.size()), &read, nullptr)) read = 0;
    data.resize(read);
  }
  CloseHandle(file);
  return data;
}

std::tm LocalNow() {
  const std::time_t now = std::time(nullptr);
  std::tm local{};
  localtime_s(&local, &now);
  return local;
}

std::string Stamp(const std::tm& local) {
  char text[32];
  std::strftime(text, sizeof(text), "%Y%m%d-%H%M%S", &local);
  return text;
}

// The guest views (4 GB each) hold game data, so the dump leaves them out. The
// callback's sizes are 32-bit, so they go out in 1 GB pieces.
struct RemoveGuest {
  std::vector<std::pair<uint64_t, uint32_t>> pieces;
  size_t next = 0;
};

BOOL CALLBACK DumpCallback(PVOID param, const PMINIDUMP_CALLBACK_INPUT input,
                           PMINIDUMP_CALLBACK_OUTPUT output) {
  auto* remove = static_cast<RemoveGuest*>(param);
  if (input->CallbackType == RemoveMemoryCallback) {
    if (remove->next >= remove->pieces.size()) return FALSE;
    output->MemoryBase = remove->pieces[remove->next].first;
    output->MemorySize = remove->pieces[remove->next].second;
    ++remove->next;
    return TRUE;
  }
  return TRUE;
}

// Runs in the helper: dumps the crashed process named in `request`.
bool WriteDump(const DumpRequest& request) {
  HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_DUP_HANDLE,
                               FALSE, request.process_id);
  if (!process) return false;
  HANDLE file = CreateFileW(request.dump_path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                            CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) {
    CloseHandle(process);
    return false;
  }
  // ClientPointers: the EXCEPTION_POINTERS address is in the crashed process.
  MINIDUMP_EXCEPTION_INFORMATION exception{
      request.thread_id,
      reinterpret_cast<EXCEPTION_POINTERS*>(uintptr_t(request.exception_pointers)), TRUE};
  RemoveGuest remove;
  for (const uint64_t view : request.guest_views) {
    if (!view) continue;
    for (uint64_t at = 0; at < (1ull << 32); at += (1ull << 30)) {
      remove.pieces.push_back({view + at, 1u << 30});
    }
  }
  MINIDUMP_CALLBACK_INFORMATION callback{DumpCallback, &remove};
  // Stacks, modules and the memory the stacks point at; never full memory.
  const auto type = MINIDUMP_TYPE(MiniDumpWithIndirectlyReferencedMemory |
                                  MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules);
  const BOOL ok =
      MiniDumpWriteDump(process, request.process_id, file, type,
                        request.exception_pointers ? &exception : nullptr, nullptr, &callback);
  CloseHandle(file);
  CloseHandle(process);
  return ok == TRUE;
}

std::wstring Wide(const std::string& utf8) {
  const int size = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), int(utf8.size()), nullptr, 0);
  std::wstring wide(size_t(size), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, utf8.data(), int(utf8.size()), wide.data(), size);
  return wide;
}

std::string Utf8(const wchar_t* wide) {
  const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
  std::string utf8(size_t(size > 0 ? size - 1 : 0), '\0');
  WideCharToMultiByte(CP_UTF8, 0, wide, -1, utf8.data(), size, nullptr, nullptr);
  return utf8;
}

// Runs in the crashing process: a second copy of the game writes the dump
// (crash_dump_request.h) while this thread waits, so a heap lock another
// thread holds can't stall it. A minute without a dump means none.
bool DumpThroughHelper(EXCEPTION_POINTERS* info, const std::filesystem::path& path) {
  DumpRequest request;
  request.process_id = GetCurrentProcessId();
  request.thread_id = GetCurrentThreadId();
  request.exception_pointers = uint64_t(uintptr_t(info));
  request.guest_views[0] = uint64_t(uintptr_t(g_guest_views[0]));
  request.guest_views[1] = uint64_t(uintptr_t(g_guest_views[1]));
  request.dump_path = path;
  wchar_t exe[MAX_PATH * 4];
  const DWORD length = GetModuleFileNameW(nullptr, exe, DWORD(std::size(exe)));
  if (length == 0 || length >= std::size(exe)) return false;
  std::wstring command = L"\"" + std::wstring(exe) + L"\"";
  for (const std::string& arg : DumpRequestArgs(request)) command += L" \"" + Wide(arg) + L"\"";
  STARTUPINFOW startup{sizeof(startup)};
  PROCESS_INFORMATION helper{};
  if (!CreateProcessW(exe, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                      nullptr, &startup, &helper)) {
    return false;
  }
  CloseHandle(helper.hThread);
  DWORD exit_code = 1;
  if (WaitForSingleObject(helper.hProcess, 60000) != WAIT_OBJECT_0) {
    TerminateProcess(helper.hProcess, 1);
  } else {
    GetExitCodeProcess(helper.hProcess, &exit_code);
  }
  CloseHandle(helper.hProcess);
  return exit_code == 0;
}

CrashFacts Facts(uint32_t code, const std::tm& now) {
  CrashFacts facts;
  facts.version = WWE_VERSION;
  facts.commit = WWE_COMMIT;
  facts.renderer = wwe::native::IsEnabled() ? "native"
                   : rex::cvar::Query<bool>("wwe_native_renderer")
                       ? "emulated (Native asked for, not running)"
                       : "emulated";
  facts.exception_status = code;
  facts.stamp = Stamp(now);
  const std::string log = rex::cvar::GetFlagByName("log_file");
  facts.session_log = std::filesystem::path(std::u8string(log.begin(), log.end()));
  // Launcher and direct starts log to <logs>/<session>/<file>.
  facts.logs_folder = facts.session_log.empty()
                          ? rex::filesystem::GetExecutableFolder() / "logs"
                          : facts.session_log.parent_path().parent_path();
  std::error_code error;
  if (!facts.session_log.empty() && !std::filesystem::exists(facts.session_log, error)) {
    facts.session_log.clear();
  }
  // The launcher logs to the same folder (Launcher.Core/LauncherLog.cs).
  facts.launcher_log = facts.logs_folder / "launcher.log";
  if (!std::filesystem::exists(facts.launcher_log, error)) facts.launcher_log.clear();
  std::lock_guard lock(g_mutex);
  facts.settings_file = g_settings_file;
  facts.settings_exists =
      !g_settings_file.empty() && std::filesystem::exists(g_settings_file, error);
  facts.gpu_name = g_gpu_name;
  facts.driver_version = g_driver_version;
  return facts;
}

void ShowSaved(const std::filesystem::path& zip, const wchar_t* what) {
  if (rex::cvar::Query<bool>("headless")) return;
  // A frozen game window behind the message reads as a hang (story 1). Async:
  // the UI thread may be the one waiting on this thread.
  if (HWND window = g_window.load()) ShowWindowAsync(window, SW_HIDE);
  const std::wstring text = std::wstring(what) + L" A crash report was saved:\n\n" +
                            zip.wstring() +
                            L"\n\nAttach it to your GitHub issue. Open its folder now?";
  if (MessageBoxW(nullptr, text.c_str(), L"WWE All Stars",
                  MB_YESNO | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST) == IDYES) {
    const std::wstring args = L"/select,\"" + zip.wstring() + L"\"";
    ShellExecuteW(nullptr, L"open", L"explorer.exe", args.c_str(), nullptr, SW_SHOWNORMAL);
  }
}

// The plan's path, or "<stem>-2.zip" and on when a report of the same second
// is already there (one from an earlier run).
std::filesystem::path FreeZipPath(const std::filesystem::path& planned) {
  std::error_code error;
  std::filesystem::path path = planned;
  for (int n = 2; std::filesystem::exists(path, error) && n < 100; ++n) {
    path = planned.parent_path() / (planned.stem().wstring() + L"-" + std::to_wstring(n) + L".zip");
  }
  return path;
}

// Writes the zip and tells the player; `info` is null for a GPU failure (the
// dump then holds every thread with no exception record). The zip goes out
// entry by entry; when a write fails, the dump stays beside it instead.
void WriteReport(const CrashFacts& facts, const std::tm& now, EXCEPTION_POINTERS* info,
                 const wchar_t* what) {
  const CrashReportPlan plan = PlanCrashReport(facts);
  std::error_code error;
  std::filesystem::create_directories(plan.zip_path.parent_path(), error);
  const std::filesystem::path zip_path = FreeZipPath(plan.zip_path);
  std::filesystem::path dump = zip_path;
  dump.replace_extension(".dmp");
  const bool dumped = DumpThroughHelper(info, dump);

  HANDLE file = CreateFileW(zip_path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                            FILE_ATTRIBUTE_NORMAL, nullptr);
  const bool created = file != INVALID_HANDLE_VALUE;
  bool written = created;
  if (created) {
    StoredZipWriter zip(
        [file](const uint8_t* data, size_t size) {
          DWORD done = 0;
          return WriteFile(file, data, DWORD(size), &done, nullptr) && done == size;
        },
        now);
    if (dumped) {
      const std::vector<uint8_t> dump_data = ReadAll(dump);
      written = !dump_data.empty() && zip.Add(plan.dump_name, dump_data);
    }
    for (const ReportFile& report_file : plan.files) {
      written = written && zip.Add(report_file.name,
                                   CapLog(ReadAll(report_file.source), report_file.max_bytes));
    }
    written = written && zip.Add(plan.manifest_name, {plan.manifest.begin(), plan.manifest.end()});
    written = zip.Finish() && written;
    CloseHandle(file);
  }
  if (!written) {
    if (created) std::filesystem::remove(zip_path, error);
    const std::u8string kept = dump.u8string();
    REXLOG_ERROR("crash report: could not be written{}{}", dumped ? "; dump kept at " : "",
                 dumped ? std::string(kept.begin(), kept.end()) : std::string());
    rex::FlushLogging();
    return;
  }
  g_report_written = true;
  std::filesystem::remove(dump, error);
  // The launcher links the report from this line; keep the wording
  // (GameExitReport.CrashReportMarker).
  const std::u8string path = zip_path.u8string();
  REXLOG_ERROR("crash report: saved {}", std::string(path.begin(), path.end()));
  rex::FlushLogging();
  ShowSaved(zip_path, what);
}

LONG WINAPI OnUnhandledException(EXCEPTION_POINTERS* info) {
  // A crash inside the report itself ends the process at once.
  const uint32_t code = info && info->ExceptionRecord ? info->ExceptionRecord->ExceptionCode : 0;
  if (ClaimReport(code) != Claim::kOwn) return EXCEPTION_EXECUTE_HANDLER;
  REXLOG_ERROR("crash: unhandled exception 0x{:08X}; writing a crash report", code);
  rex::FlushLogging();
  const std::tm now = LocalNow();
  WriteReport(Facts(code, now), now, info, L"WWE All Stars crashed.");
  // The process ends with the exception code, which the launcher reports.
  return EXCEPTION_EXECUTE_HANDLER;
}

// abort, a pure virtual call and a bad CRT parameter end the process without
// reaching the filter; raising an exception sends them through it.
[[noreturn]] void RaiseCrash(DWORD code) {
  RaiseException(code, EXCEPTION_NONCONTINUABLE, 0, nullptr);
  std::_Exit(int(code));
}

void OnAbort(int) { RaiseCrash(kAbortCode); }
void OnPureCall() { RaiseCrash(kPureCallCode); }
void OnInvalidParameter(const wchar_t*, const wchar_t*, const wchar_t*, unsigned int, uintptr_t) {
  RaiseCrash(kInvalidParameterCode);
}

}  // namespace

void Install() {
  {
    int count = 0;
    wchar_t** wide = CommandLineToArgvW(GetCommandLineW(), &count);
    std::vector<std::string> args;
    for (int i = 0; wide && i < count; ++i) args.push_back(Utf8(wide[i]));
    LocalFree(wide);
    if (const std::optional<DumpRequest> request = ParseDumpRequest(args)) {
      std::_Exit(WriteDump(*request) ? 0 : 1);
    }
  }
  SetUnhandledExceptionFilter(OnUnhandledException);
  // __fastfail cannot be caught in process; these can.
  std::signal(SIGABRT, OnAbort);
  _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
  _set_purecall_handler(OnPureCall);
  _set_invalid_parameter_handler(OnInvalidParameter);
}

void SetWindow(void* native_window) { g_window = static_cast<HWND>(native_window); }

void SetSettingsFile(const std::filesystem::path& path) {
  std::lock_guard lock(g_mutex);
  g_settings_file = path;
}

void SetGpu(const std::string& name, const std::string& driver_version) {
  std::lock_guard lock(g_mutex);
  g_gpu_name = name;
  g_driver_version = driver_version;
}

void SetGuestMemory(const uint8_t* virtual_membase, const uint8_t* physical_membase) {
  g_guest_views[0] = virtual_membase;
  g_guest_views[1] = physical_membase;
  REXLOG_INFO("crash report: guest memory at {} and {}, left out of dumps",
              static_cast<const void*>(virtual_membase), static_cast<const void*>(physical_membase));
}

void WriteGpuFailureReport(const std::string& failure, const std::string& fault_finder) {
  if (ClaimReport(wwe::native::kGpuFailureExitCode) != Claim::kOwn) return;
  const std::tm now = LocalNow();
  CrashFacts facts = Facts(0, now);
  facts.gpu_failure = failure;
  facts.fault_finder = fault_finder;
  WriteReport(facts, now, nullptr,
              L"The GPU stopped working while WWE All Stars ran on Native, so the game ended "
              L"the session. Switch the renderer to Emulated in the launcher if this repeats.");
}

void CrashIfAsked() {
  if (REXCVAR_GET(wwe_debug_force_crash)) {
    REXLOG_ERROR("crash: --wwe_debug_force_crash");
    *static_cast<volatile int*>(nullptr) = 0;
  }
  if (REXCVAR_GET(wwe_debug_force_abort)) {
    REXLOG_ERROR("crash: --wwe_debug_force_abort");
    std::abort();
  }
}

}  // namespace wwe::crash
