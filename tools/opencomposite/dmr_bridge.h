#pragma once
#include "dmr_state.hpp"
#include <openxr/openxr.h>

extern dmr::AlphaState dmrAlpha;
void dmrSessionCreated(XrInstance instance, XrSystemId system);
void* dmrLegacyFunction(const char* name);
const char* dmrRuntimePath();
template<class T> T dmrLegacy(const char* name) { return reinterpret_cast<T>(dmrLegacyFunction(name)); }
