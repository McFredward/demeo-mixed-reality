#pragma once
#include "dmr_state.hpp"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace dmr {
inline bool diagnosticAllowed(bool enabled, const Snapshot& state, const AlphaState::Frame& frame) {
    return enabled && frame.alpha && frame.generation == state.generation && state.mode == 3
        && (state.flags & (Native | Running | SteamVR | SupportsAlpha | PresentedAlpha))
            == (Native | Running | SteamVR | SupportsAlpha | PresentedAlpha);
}
// Values are linear-light scene contribution E, not straight RGB. Alpha is
// coverage A. In particular, cell 4 deliberately has nonzero E with A=0.
inline std::array<uint8_t, 4> diagnosticPixel(unsigned cell, unsigned x, unsigned width, bool srgb, bool bgra) {
    double a = 0, r = 0, g = 0, b = 0;
    switch (cell) {
        case 0: break; // clear room
        case 1: a = 1; r = g = b = .25; break; // opaque grey
        case 2: a = 1; g = 1; break; // genuine opaque green
        case 3: a = .5; r = g = b = .5; break; // half-opacity white
        case 4: r = g = b = .25; break; // additive light, zero coverage
        case 5: a = width > 1 ? double((std::min)(x, width-1)) / (width-1) : 0;
                r = g = b = .25 * a; break; // soft grey coverage edge
        default: break;
    }
    auto encode = [srgb](double v) {
        if (srgb) v = v <= .0031308 ? 12.92*v : 1.055*std::pow(v, 1/2.4)-.055;
        return static_cast<uint8_t>(std::lround(255*v));
    };
    std::array<uint8_t, 4> value{ encode(r), encode(g), encode(b), static_cast<uint8_t>(std::lround(255*a)) };
    if (bgra) std::swap(value[0], value[2]);
    return value;
}
struct DiagnosticStats {
    std::array<unsigned, 4> lo{255,255,255,255}, hi{}, sum{};
    unsigned count = 0, greenExcess = 0;
    void add(const uint8_t* pixel, bool bgra) {
        unsigned rgba[4]{ pixel[bgra ? 2 : 0], pixel[1], pixel[bgra ? 0 : 2], pixel[3] };
        for (unsigned c=0; c<4; ++c) {
            lo[c] = (std::min)(lo[c],rgba[c]); hi[c] = (std::max)(hi[c],rgba[c]); sum[c] += rgba[c];
        }
        ++count;
        if (rgba[1] > (std::max)(rgba[0],rgba[2]) + 32) ++greenExcess;
    }
};
}
