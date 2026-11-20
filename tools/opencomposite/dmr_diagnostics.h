#pragma once
#include <memory>
#include <cstdint>
#include <d3d11.h>

namespace dmr {
struct DiagnosticFrame { uint64_t generation, serial; unsigned eye; };
using DiagnosticLog = void (*)(const char*);
// Per compositor/eye. No resource allocation while disabled (the caller does
// not construct this object). All calls use the existing immediate context.
class DiagnosticProbe {
    struct Impl;
    std::unique_ptr<Impl> impl;
public:
    DiagnosticProbe();
    ~DiagnosticProbe();
    DiagnosticProbe(const DiagnosticProbe&) = delete;
    DiagnosticProbe& operator=(const DiagnosticProbe&) = delete;
    void process(ID3D11DeviceContext* context, ID3D11Texture2D* acquiredImage,
        DXGI_FORMAT viewFormat, DiagnosticFrame frame, double seconds, DiagnosticLog log) noexcept;
};
}
