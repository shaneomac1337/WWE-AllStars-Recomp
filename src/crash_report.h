// Crash report (CONTEXT.md, ADR-0025), game side: an unhandled exception on
// any thread writes one zip in the logs folder (a small dump with no guest
// memory, the session log, the settings file and a manifest) and tells the
// player where it is. What goes in is crash_report_plan.h.

#pragma once

#include <cstdint>
#include <filesystem>
#include <string>

namespace wwe::crash {

// Installs the handler, and routes abort, pure virtual calls and bad CRT
// parameters through it; call as early as possible. In the dump helper
// (--wwe-write-crash-dump, crash_dump_request.h), writes the dump and exits.
void Install();

// Facts the report needs, known only later in startup.
void SetSettingsFile(const std::filesystem::path& path);
// The game window (an HWND), hidden before the report's message shows.
void SetWindow(void* native_window);
void SetGpu(const std::string& name, const std::string& driver_version);
// The guest's memory views, left out of the dump (they hold game data).
void SetGuestMemory(const uint8_t* virtual_membase, const uint8_t* physical_membase);

// A GPU failure (ADR-0025): writes a report whose reason is `failure`,
// "<VkResult> (<call>)", and shows the same message as a crash. If another
// thread is already writing a crash report, waits for it to end the process.
void WriteGpuFailureReport(const std::string& failure);

// --wwe_debug_force_crash / --wwe_debug_force_abort: crashes the calling thread.
void CrashIfAsked();

}  // namespace wwe::crash
