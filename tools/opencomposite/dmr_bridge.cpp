#include "stdafx.h"
#include "dmr_bridge.h"
#include <algorithm>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>

dmr::AlphaState dmrAlpha;
namespace {
namespace fs = std::filesystem;
std::string trim(std::string value) {
    auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    value = value.substr(first, value.find_last_not_of(" \t\r\n") - first + 1);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}
fs::path modulePath(HMODULE module) {
    wchar_t path[32768]; DWORD count = GetModuleFileNameW(module, path, 32768);
    if (count == 0 || count >= 32768) throw std::runtime_error("Demeo MR: module path unavailable");
    return fs::path(path);
}
bool legacyRequested() {
    const auto game = modulePath(nullptr).parent_path();
    fs::path config = game / L"BepInEx/config/DemeoMixedReality.cfg";
    if (!fs::exists(config)) {
        config = game / L"BepInEx/config/566784.cfg";
        if (!fs::exists(config)) {
            config.clear();
            for (const auto& root : { game / L"BepInEx/plugins", game / L"Mods" }) {
                if (!fs::is_directory(root)) continue;
                for (const auto& name : { L"greenscreen.ini", L"custom_color.ini" }) {
                    for (const auto& item : fs::recursive_directory_iterator(root, fs::directory_options::skip_permission_denied)) {
                        if (item.is_regular_file() && item.path().filename() == name) { config = item.path(); break; }
                    }
                    if (!config.empty()) break;
                }
                if (!config.empty()) break;
            }
        }
    }
    if (config.empty()) return false;
    std::ifstream input(config);
    if (!input) throw std::runtime_error("Demeo MR: cannot read transport selection config");
    std::string line, section;
    bool native = true, legacy = false;
    while (std::getline(input, line)) {
        line = trim(line);
        if (line.empty() || line[0] == '#' || line[0] == ';') continue;
        if (line.front() == '[' && line.back() == ']') { section = trim(line.substr(1, line.size()-2)); continue; }
        auto equal = line.find('=');
        if (section != "steamlink" || equal == std::string::npos) continue;
        auto key = trim(line.substr(0, equal)), value = trim(line.substr(equal+1));
        if (key == "backend") legacy = value == "legacycompanion";
        if (key == "nativepassthrough") native = value == "true" || value == "1";
    }
    return legacy || !native;
}
bool useLegacy() {
    // Transport is selected once, before the game's first OpenVR call. Alpha
    // itself can toggle live; switching XR transports requires a game restart.
    static const bool legacy = [] {
        try { return legacyRequested(); }
        catch (const std::exception& error) { OOVR_LOG(error.what()); return true; }
    }();
    return legacy;
}
}

void* dmrLegacyFunction(const char* name) {
    if (!useLegacy()) return nullptr;
    // Keep exactly this game's original API, never a system-wide override.
    static HMODULE original = [] {
        const auto path = modulePath(openovr_module_id).parent_path() / L"openvr_api.dmr-original.dll";
        HMODULE result = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!result) OOVR_ABORT("Demeo MR: original OpenVR backup is missing. Run Install-OpenXR.ps1 again.");
        return result;
    }();
    auto function = GetProcAddress(original, name);
    if (!function) OOVR_ABORTF("Demeo MR: original OpenVR export missing: %s", name);
    return reinterpret_cast<void*>(function);
}
const char* dmrRuntimePath() {
    static const std::string path = modulePath(openovr_module_id).parent_path().string();
    return path.c_str();
}

void dmrSessionCreated(XrInstance instance, XrSystemId system) {
    XrInstanceProperties properties{ XR_TYPE_INSTANCE_PROPERTIES };
    bool steamVR = xrGetInstanceProperties(instance, &properties) == XR_SUCCESS &&
        std::strcmp(properties.runtimeName, "SteamVR/OpenXR") == 0;
    uint32_t count = 0;
    bool alpha = false;
    if (xrEnumerateEnvironmentBlendModes(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &count, nullptr) == XR_SUCCESS
        && count > 0 && count <= 16) {
        XrEnvironmentBlendMode modes[16]{}; uint32_t written = 0;
        if (xrEnumerateEnvironmentBlendModes(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, count, &written, modes) == XR_SUCCESS
            && written > 0 && written <= count) {
            bool valid = true;
            for (uint32_t i=0; i<written; ++i) { valid &= modes[i] > 0; alpha |= modes[i] == XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND; }
            alpha &= valid;
        }
    }
    dmrAlpha.session(steamVR, alpha);
    OOVR_LOGF("Demeo MR OpenXR adapter: SteamVR=%d; ALPHA_BLEND=%d; no companion or runtime patch", steamVR, alpha);
}

extern "C" __declspec(dllexport) int __cdecl DMR_GetState(uint32_t bytes, dmr::Snapshot* output) {
    if (bytes != sizeof(dmr::Snapshot) || !output) return 0;
    *output = dmrAlpha.read();
    if (useLegacy()) output->flags = dmr::Legacy;
    return 1;
}
extern "C" __declspec(dllexport) int __cdecl DMR_SetBlendMode(uint64_t generation, uint32_t mode) {
    if (useLegacy()) return 0;
    return dmrAlpha.set(generation, mode) ? 1 : 0;
}
