// Force-included in XenosRecomp's sources inside the game exe (CMakeLists.txt). dxcompiler.dll is
// loaded on the first conversion, so a normal game start does not need it.
#pragma once

#include <windows.h>

#include <dxcapi.h>

HRESULT WweDxcCreateInstance(REFCLSID clsid, REFIID iid, LPVOID* out);
#define DxcCreateInstance WweDxcCreateInstance
