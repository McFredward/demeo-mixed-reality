#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include "dmr_state.hpp"

static void require(bool condition, const char* message) {
    if (!condition) { std::fprintf(stderr, "OpenXR adapter DLL check failed: %s (Win32=%lu)\n", message, GetLastError()); ExitProcess(1); }
}
int wmain(int argc, wchar_t** argv) {
    require(argc == 3, "expected adapter path and legacy flag");
    HMODULE dll = LoadLibraryExW(argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    require(dll != nullptr, "DLL loads with system libraries only");
    const char* imports[] = { "VR_GetGenericInterface", "VRHeadsetView", "VR_ShutdownInternal", "VR_InitInternal2",
        "VR_GetInitToken", "VR_IsInterfaceVersionValid", "VR_GetVRInitErrorAsSymbol", "VR_GetRuntimePath" };
    for (auto name : imports) require(GetProcAddress(dll, name) != nullptr, name);
    auto get = reinterpret_cast<int(__cdecl*)(uint32_t,dmr::Snapshot*)>(GetProcAddress(dll,"DMR_GetState"));
    auto set = reinterpret_cast<int(__cdecl*)(uint64_t,uint32_t)>(GetProcAddress(dll,"DMR_SetBlendMode"));
    require(get && set, "versioned mod control exports");
    dmr::Snapshot state;
    require(get(0,&state) == 0 && get(24,nullptr) == 0, "invalid ABI requests refused");
    require(get(24,&state) == 1 && state.version == 1 && state.bytes == 24 && state.generation != 0 && state.mode == 1, "initial state ABI");
    bool legacy = argv[2][0] == L'1';
    require(state.flags == (legacy ? dmr::Legacy : dmr::Native), "startup config selects exact transport");
    require(set(state.generation,3) == 0, "no native alpha permission before a real session");
    auto token = reinterpret_cast<uint32_t(__cdecl*)()>(GetProcAddress(dll,"VR_GetInitToken"));
    require(token() == (legacy ? 777u : 1u), "legacy forwards to the game's original API; native retains its own API");
    std::puts(legacy ? "OpenXR adapter DLL legacy forwarding PASS (no XR startup)" : "OpenXR adapter DLL native ABI/imports PASS (no XR startup)");
    FreeLibrary(dll);
}
