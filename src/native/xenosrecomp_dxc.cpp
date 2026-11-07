#undef DxcCreateInstance

HRESULT WweDxcCreateInstance(REFCLSID clsid, REFIID iid, LPVOID* out) {
  using Create = HRESULT(__stdcall*)(REFCLSID, REFIID, LPVOID*);
  const HMODULE library = LoadLibraryW(L"dxcompiler.dll");
  if (!library) {
    return HRESULT_FROM_WIN32(GetLastError());
  }
  const auto create = reinterpret_cast<Create>(GetProcAddress(library, "DxcCreateInstance"));
  return create ? create(clsid, iid, out) : HRESULT_FROM_WIN32(GetLastError());
}
