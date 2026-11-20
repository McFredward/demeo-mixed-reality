#pragma once
#include <cstdint>
#include <mutex>

namespace dmr {
enum : uint32_t { None = 0, Native = 1, Running = 2, SupportsAlpha = 4, SteamVR = 8, PresentedAlpha = 16, Legacy = 32, SystemKnown = 64 };
struct Snapshot {
    uint32_t version = 1, bytes = 24;
    uint64_t generation = 1;
    uint32_t flags = Native, mode = 1;
};
static_assert(sizeof(Snapshot) == 24);

// One game-owned OpenXR session. Mod control never grants another process or
// supplies a guessed eye, pose, texture or frame. No wall-clock mask reuse.
class AlphaState {
    std::mutex gate;
    Snapshot value;
    uint32_t requested = 1, eyes = 0;
    uint64_t serial = 0;
public:
    Snapshot read() { std::lock_guard lock(gate); return value; }
    void session(bool steamVR, bool alpha) {
        std::lock_guard lock(gate);
        value.generation++; value.flags = Native | SystemKnown | (steamVR ? SteamVR : None) | (alpha ? SupportsAlpha : None);
        value.mode = requested = 1; eyes = 0;
    }
    void running(bool active) {
        std::lock_guard lock(gate);
        value.generation++; value.flags &= ~(Running | PresentedAlpha);
        if (active) value.flags |= Running;
        value.mode = requested = 1; eyes = 0;
    }
    bool set(uint64_t generation, uint32_t mode) {
        std::lock_guard lock(gate);
        if (generation != value.generation || !(value.flags & Running) || (mode != 1 && mode != 3)) return false;
        if (mode == 3 && (value.flags & (SteamVR | SupportsAlpha)) != (SteamVR | SupportsAlpha)) return false;
        requested = mode;
        return true;
    }
    void beginFrame() { std::lock_guard lock(gate); eyes = 0; ++serial; }
    void eye(uint32_t index) { std::lock_guard lock(gate); if (index < 2) eyes |= 1u << index; }
    struct Frame { uint64_t generation; bool alpha, pair; uint64_t serial; };
    Frame frame() {
        std::lock_guard lock(gate);
        bool alpha = requested == 3 && (value.flags & (Running | SteamVR | SupportsAlpha)) == (Running | SteamVR | SupportsAlpha);
        return { value.generation, alpha, eyes == 3, serial };
    }
    void presented(Frame frame, bool success) {
        std::lock_guard lock(gate);
        if (frame.generation != value.generation || !success) return;
        value.mode = frame.alpha ? 3 : 1;
        value.flags = (value.flags & ~PresentedAlpha) | (frame.alpha ? PresentedAlpha : None);
        eyes = 0;
    }
};
}
