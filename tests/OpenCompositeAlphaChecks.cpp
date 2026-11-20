#include "../tools/opencomposite/dmr_state.hpp"
#include <cassert>
#include <cstdio>

int main() {
    dmr::AlphaState state;
    auto initial = state.read();
    assert(initial.bytes == 24 && initial.version == 1 && initial.mode == 1);
    assert(!state.set(initial.generation, 3)); // no active session
    state.session(true, true); state.running(true);
    auto live = state.read();
    assert(state.set(live.generation, 3));
    assert(state.read().mode == 1); // request is not presentation
    state.beginFrame(); state.eye(0);
    auto partial = state.frame();
    assert(partial.alpha && !partial.pair); // room only, no stale other eye
    state.eye(1); auto both = state.frame();
    assert(both.alpha && both.pair);
    state.presented(both, false);
    assert(state.read().mode == 1); // rejected xrEndFrame is not acceptance
    state.presented(both, true);
    assert(state.read().mode == 3 && (state.read().flags & dmr::PresentedAlpha));
    state.beginFrame(); assert(!state.frame().pair); // no cached eye authority
    state.running(false);
    assert(!state.set(live.generation, 3) && state.read().mode == 1);
    state.presented(both, true); assert(state.read().mode == 1); // stale completion
    state.session(true, false); state.running(true);
    assert(!state.set(state.read().generation, 3)); // opaque-only runtime
    state.session(false, true); state.running(true);
    assert(!state.set(state.read().generation, 3)); // another runtime
    state.session(true, true); state.running(true);
    assert(state.set(state.read().generation, 3));
    assert(state.set(state.read().generation, 1)); // queued alpha cancelled
    state.beginFrame(); state.eye(0); state.eye(1);
    assert(!state.frame().alpha && state.frame().pair);
    std::puts("OpenComposite native alpha session/frame/eye policy checks passed");
}
