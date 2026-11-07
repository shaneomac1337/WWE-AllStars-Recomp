#pragma once

namespace wwe::native {

// The command line flag that makes this exe the shader converter:
// wweallstars.exe --wwe_convert_shaders <input> <output> <shader_common.h> <hlsl folder>
inline constexpr wchar_t kConvertShadersFlag[] = L"--wwe_convert_shaders";

// With that flag first on the command line, runs XenosRecomp in this process and exits with its
// code; otherwise returns. XenosRecomp is linked into the game exe rather than shipped as its own
// exe: Windows Defender convicts the standalone one as Trojan:Win32/Bearfoos.B!ml (ADR-0029).
void RunShaderConversionIfAsked();

}  // namespace wwe::native
