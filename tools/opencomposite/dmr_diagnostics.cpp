#include "dmr_diagnostics.h"
#include "dmr_diagnostic_pattern.hpp"
#include <wrl/client.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <vector>

namespace dmr {
using Microsoft::WRL::ComPtr;
namespace {
constexpr unsigned patch = 32, regions = 10, maxSamples = 24;
constexpr double duration = 120, cadence = 5;
void checked(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D11 diagnostic resource/copy unavailable"); }
bool srgbFormat(DXGI_FORMAT f) { return f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB; }
bool bgraFormat(DXGI_FORMAT f) { return f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB; }
bool supported(DXGI_FORMAT f) { return srgbFormat(f) || f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM; }
unsigned family(DXGI_FORMAT f) {
    if (f == DXGI_FORMAT_R8G8B8A8_TYPELESS || f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) return 1;
    if (f == DXGI_FORMAT_B8G8R8A8_TYPELESS || f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) return 2;
    return 0;
}
}
struct DiagnosticProbe::Impl {
    ComPtr<ID3D11Texture2D> pattern, staging;
    ComPtr<ID3D11Query> query;
    ComPtr<ID3D11Device> device;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    UINT width = 0, height = 0, cell = 0, stripHeight = 0, left = 0, top = 0;
    bool pending = false, failed = false, announced = false, expired = false;
    unsigned samples = 0, pendingCalls = 0;
    double start = -1, next = 0;
    DiagnosticFrame captured{};
    void prepare(ID3D11Texture2D* image, DXGI_FORMAT view) {
        D3D11_TEXTURE2D_DESC desc{}; image->GetDesc(&desc);
        if (!supported(view) || family(desc.Format) != family(view) || desc.SampleDesc.Count != 1 || desc.ArraySize != 1 || desc.Width < 384 || desc.Height < 256)
            throw std::runtime_error("diagnostic needs single-sampled 2D RGBA8/BGRA8 image at least 384x256");
        ComPtr<ID3D11Device> current; image->GetDevice(&current);
        if (pattern && device.Get() == current.Get() && width == desc.Width && height == desc.Height && format == view) return;
        // An outstanding snapshot is never reinterpreted using a new layout.
        pending = false; staging.Reset(); query.Reset(); pattern.Reset(); device = current;
        width = desc.Width; height = desc.Height; format = view;
        cell = (std::min)(128u, (std::max)(patch, width/12));
        stripHeight = (std::min)(64u, (std::max)(patch,height/16));
        left = (width - cell*6)/2; top = height*3/4;
        std::vector<uint8_t> pixels(size_t(cell)*6*stripHeight*4);
        for (unsigned y=0; y<stripHeight; ++y) for (unsigned x=0; x<cell*6; ++x) {
            auto value = diagnosticPixel(x/cell,x%cell,cell,srgbFormat(view),bgraFormat(view));
            std::memcpy(pixels.data()+(size_t(y)*cell*6+x)*4,value.data(),4);
        }
        D3D11_TEXTURE2D_DESC pd{};
        pd.Width = cell*6; pd.Height = stripHeight; pd.MipLevels = pd.ArraySize = pd.SampleDesc.Count = 1;
        pd.Format = view; pd.Usage = D3D11_USAGE_IMMUTABLE; pd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{ pixels.data(), cell*6*4, 0 };
        checked(device->CreateTexture2D(&pd,&data,&pattern));
        pd.Width = patch; pd.Height = patch*regions; pd.Usage = D3D11_USAGE_STAGING;
        pd.BindFlags = 0; pd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        checked(device->CreateTexture2D(&pd,nullptr,&staging));
        D3D11_QUERY_DESC q{ D3D11_QUERY_EVENT,0 }; checked(device->CreateQuery(&q,&query));
    }
    void poll(ID3D11DeviceContext* context, DiagnosticLog log) {
        if (!pending) return;
        BOOL complete = FALSE;
        HRESULT ready = context->GetData(query.Get(),&complete,sizeof(complete),D3D11_ASYNC_GETDATA_DONOTFLUSH);
        checked(ready);
        if (ready != S_OK || !complete) {
            if (++pendingCalls > 600) throw std::runtime_error("diagnostic GPU query exceeded bounded poll count; stopping diagnostic only");
            return;
        }
        D3D11_MAPPED_SUBRESOURCE map{};
        HRESULT mapped = context->Map(staging.Get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&map);
        if (mapped == DXGI_ERROR_WAS_STILL_DRAWING) {
            if (++pendingCalls > 600) throw std::runtime_error("diagnostic readback remained busy; stopping diagnostic only");
            return;
        }
        checked(mapped);
        DiagnosticStats stats[regions];
        unsigned mismatch[6]{};
        for (unsigned region=0; region<regions; ++region) for (unsigned y=0; y<patch; ++y) for (unsigned x=0; x<patch; ++x) {
            const auto* pixel = static_cast<const uint8_t*>(map.pData)+(region*patch+y)*map.RowPitch+x*4;
            stats[region].add(pixel,bgraFormat(format));
            if (region >= 4) {
                const auto expected = diagnosticPixel(region-4,(cell-patch)/2+x,cell,srgbFormat(format),bgraFormat(format));
                if (std::memcmp(pixel,expected.data(),4) != 0) ++mismatch[region-4];
            }
        }
        context->Unmap(staging.Get(),0);
        pending = false;
        char line[768];
        std::snprintf(line,sizeof(line),"DMR alpha probe: sample=%u generation=%llu frame=%llu eye=%u image=%ux%u format=%u; BEFORE xrReleaseSwapchainImage; game regions precede test injection; premultiplied E+(1-A)*camera",
            samples,static_cast<unsigned long long>(captured.generation),static_cast<unsigned long long>(captured.serial),captured.eye,width,height,static_cast<unsigned>(format));
        log(line);
        for (unsigned r=0; r<regions; ++r) {
            const auto& s = stats[r];
            std::snprintf(line,sizeof(line),"DMR alpha probe: eye=%u frame=%llu %s=%u n=%u R=%u/%.2f/%u G=%u/%.2f/%u B=%u/%.2f/%u A=%u/%.2f/%u green_excess=%u synthetic_mismatch=%u",
                captured.eye,static_cast<unsigned long long>(captured.serial),r<4?"game-region":"swatch",r<4?r:r-4,s.count,
                s.lo[0],double(s.sum[0])/s.count,s.hi[0],s.lo[1],double(s.sum[1])/s.count,s.hi[1],
                s.lo[2],double(s.sum[2])/s.count,s.hi[2],s.lo[3],double(s.sum[3])/s.count,s.hi[3],s.greenExcess,r<4?0:mismatch[r-4]);
            log(line);
        }
    }
    void process(ID3D11DeviceContext* context, ID3D11Texture2D* image, DXGI_FORMAT view, DiagnosticFrame frame, double seconds, DiagnosticLog log) {
        if (failed || expired) return;
        if (!context || !image || !log || frame.eye > 1 || !std::isfinite(seconds)) throw std::runtime_error("invalid diagnostic context");
        if (start < 0) start = seconds;
        poll(context,log);
        if (seconds-start >= duration) {
            if (!pending) { expired = true; log("DMR alpha probe: 120-second diagnostic window complete; test strip removed, no further samples."); }
            return;
        }
        prepare(image,view);
        if (!announced) {
            log("DMR alpha probe ENABLED: 120-second strip, cells 0=clear 1=opaque grey 2=opaque green 3=half white 4=additive light A0 5=soft grey edge. Max 24 readbacks/eye, 5-second cadence; summaries only, no image files.");
            announced = true;
        }
        const bool capture = !pending && samples < maxSamples && seconds >= next;
        auto copyPatch = [&](unsigned region, unsigned x, unsigned y) {
            D3D11_BOX box{ x,y,0,x+patch,y+patch,1 };
            context->CopySubresourceRegion(staging.Get(),0,0,region*patch,0,image,0,&box);
        };
        if (capture) {
            copyPatch(0,(width-patch)/2,(height-patch)/2);
            copyPatch(1,width/4-patch/2,(height-patch)/2);
            copyPatch(2,width*3/4-patch/2,(height-patch)/2);
            copyPatch(3,(width-patch)/2,height/4-patch/2);
        }
        // GPU copy only: no render targets, shaders, blend/sampler state or
        // source eye texture is changed. The image is still acquired by us.
        context->CopySubresourceRegion(image,0,left,top,0,pattern.Get(),0,nullptr);
        if (capture) {
            for (unsigned i=0; i<6; ++i) copyPatch(4+i,left+i*cell+(cell-patch)/2,top+(stripHeight-patch)/2);
            context->End(query.Get()); pending = true; pendingCalls = 0; captured = frame;
            ++samples; next = seconds+cadence;
        }
    }
};
DiagnosticProbe::DiagnosticProbe() : impl(std::make_unique<Impl>()) {}
DiagnosticProbe::~DiagnosticProbe() = default;
void DiagnosticProbe::process(ID3D11DeviceContext* c, ID3D11Texture2D* i, DXGI_FORMAT f, DiagnosticFrame frame, double seconds, DiagnosticLog log) noexcept {
    try { impl->process(c,i,f,frame,seconds,log); }
    catch (const std::exception& error) {
        impl->failed = true;
        if (log) { log("DMR alpha probe stopped; normal game submission remains active."); log(error.what()); }
    }
    catch (...) { impl->failed = true; if (log) log("DMR alpha probe stopped after unexpected failure; normal submission remains active."); }
}
}
