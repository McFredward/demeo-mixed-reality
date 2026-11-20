#include "../tools/opencomposite/dmr_diagnostic_pattern.hpp"
#include <cassert>
#include <cstdio>
int main() {
    using dmr::diagnosticPixel;
    for (bool srgb : {false,true}) for (bool bgra : {false,true}) {
        auto clear=diagnosticPixel(0,0,32,srgb,bgra);
        assert((clear==std::array<uint8_t,4>{0,0,0,0}));
        auto grey=diagnosticPixel(1,0,32,srgb,bgra), green=diagnosticPixel(2,0,32,srgb,bgra);
        auto half=diagnosticPixel(3,0,32,srgb,bgra), light=diagnosticPixel(4,0,32,srgb,bgra);
        assert(grey[0]==(srgb?137:64) && grey[3]==255);
        assert((green==std::array<uint8_t,4>{0,255,0,255}));
        assert(half[0]==(srgb?188:128) && half[3]==128);
        assert(light[0]==grey[0] && light[3]==0); // catches accidental alpha multiplication
        assert(diagnosticPixel(5,0,32,srgb,bgra)==clear);
        assert(diagnosticPixel(5,31,32,srgb,bgra)==grey);
        assert(diagnosticPixel(5,100,32,srgb,bgra)==grey);
        dmr::DiagnosticStats stats; stats.add(green.data(),bgra); stats.add(light.data(),bgra);
        assert(stats.count==2 && stats.greenExcess==1 && stats.lo[3]==0 && stats.hi[3]==255);
        const uint8_t asymmetric[4]{10,20,70,128}; stats={}; stats.add(asymmetric,bgra);
        assert(stats.lo[0]==(bgra?70u:10u) && stats.lo[2]==(bgra?10u:70u));
    }
    dmr::AlphaState state;
    state.session(true,true); state.running(true);
    assert(!dmr::diagnosticAllowed(true,state.read(),state.frame()));
    state.set(state.read().generation,3); state.beginFrame(); state.eye(0); state.eye(1);
    auto first=state.frame(); assert(first.serial==1);
    assert(!dmr::diagnosticAllowed(true,state.read(),first)); // requested alone is not active
    state.presented(first,true); state.beginFrame(); auto frame=state.frame();
    assert(frame.serial==2);
    assert(dmr::diagnosticAllowed(true,state.read(),frame));
    assert(!dmr::diagnosticAllowed(false,state.read(),frame));
    state.set(state.read().generation,1);
    assert(!dmr::diagnosticAllowed(true,state.read(),state.frame()));
    state.running(false);
    assert(!dmr::diagnosticAllowed(true,state.read(),frame));
    std::puts("OpenXR diagnostic encoding/statistics/opt-in/current-session policy checks passed");
}
