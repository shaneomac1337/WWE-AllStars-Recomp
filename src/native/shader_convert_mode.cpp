#include "native/shader_convert_mode.h"

#ifdef _WIN32
#include <windows.h>

#include <shellapi.h>
#endif

#include <cstdio>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

// XenosRecomp's main (renamed in CMakeLists.txt).
int xenos_recomp_main(int argc, char** argv);

namespace wwe::native {

void RunShaderConversionIfAsked() {
#ifdef _WIN32
  int count = 0;
  wchar_t** wide = CommandLineToArgvW(GetCommandLineW(), &count);
  if (!wide || count < 2 || std::wstring_view(wide[1]) != kConvertShadersFlag) {
    if (wide) {
      LocalFree(wide);
    }
    return;
  }
  // XenosRecomp opens its paths with fopen, so they stay in the ANSI code page, as a standalone
  // exe's argv is.
  std::vector<std::string> arguments;
  arguments.emplace_back("XenosRecomp");
  for (int i = 2; i < count; ++i) {
    const int size = WideCharToMultiByte(CP_ACP, 0, wide[i], -1, nullptr, 0, nullptr, nullptr);
    std::string argument(size_t(size > 0 ? size - 1 : 0), '\0');
    WideCharToMultiByte(CP_ACP, 0, wide[i], -1, argument.data(), size, nullptr, nullptr);
    arguments.push_back(std::move(argument));
  }
  // dxcompiler.dll ships beside shader_common.h.
  if (count > 4) {
    SetDllDirectoryW(std::filesystem::path(wide[4]).parent_path().c_str());
  }
  LocalFree(wide);
  std::vector<char*> argv;
  for (std::string& argument : arguments) {
    argv.push_back(argument.data());
  }
  argv.push_back(nullptr);
  // The launcher reads the progress lines as they come.
  setvbuf(stdout, nullptr, _IONBF, 0);
  setvbuf(stderr, nullptr, _IONBF, 0);
  const int code = xenos_recomp_main(int(argv.size()) - 1, argv.data());
  // Its files are closed by now. ExitProcess would run the game runtime's teardown for a runtime
  // this mode never started, which ends in 0xC0000409.
  fflush(nullptr);
  TerminateProcess(GetCurrentProcess(), UINT(code));
#endif
}

}  // namespace wwe::native
