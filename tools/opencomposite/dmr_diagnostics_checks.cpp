// Software GPU fixture only. Never starts SteamVR/OpenXR or opens a game.
#include "dmr_diagnostics.h"
#include "dmr_diagnostic_pattern.hpp"
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <stdexcept>
#include <vector>

using Microsoft::WRL::ComPtr;
static std::vector<std::string> lines;
static unsigned checks = 0;
static void require(bool value, const char* message) {
    ++checks; if (!value) { std::fprintf(stderr,"D3D11 diagnostic check failed: %s\n",message); std::exit(1); }
}
static void checked(HRESULT r) { require(SUCCEEDED(r),"D3D11 fixture operation"); }
static void logLine(const char* text) { lines.emplace_back(text); }
static void failingLog(const char*) { throw std::runtime_error("fixture logger unavailable"); }
static unsigned matching(const char* text) {
    unsigned n=0; for (const auto& line : lines) if (line.find(text) != std::string::npos) ++n; return n;
}
static std::vector<uint8_t> read(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture) {
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> copy; checked(device->CreateTexture2D(&desc,nullptr,&copy));
    context->CopyResource(copy.Get(),texture);
    // The fixture can block to compare GPU results. Production never Flushes,
    // waits, spins or performs a blocking Map.
    D3D11_MAPPED_SUBRESOURCE map{}; checked(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&map));
    std::vector<uint8_t> pixels(size_t(desc.Width)*desc.Height*4);
    for (unsigned y=0; y<desc.Height; ++y) std::memcpy(pixels.data()+size_t(y)*desc.Width*4,static_cast<const uint8_t*>(map.pData)+size_t(y)*map.RowPitch,desc.Width*4);
    context->Unmap(copy.Get(),0); return pixels;
}
int main() {
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    checked(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    for (auto format : { DXGI_FORMAT_R8G8B8A8_UNORM,DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_FORMAT_B8G8R8A8_UNORM_SRGB }) {
        lines.clear();
        const bool srgb = format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        const bool bgra = format == DXGI_FORMAT_B8G8R8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = 640; desc.Height = 480; desc.Format = format;
        desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        std::vector<uint8_t> original(size_t(desc.Width)*desc.Height*4);
        for (size_t p=0; p<original.size(); p+=4) { original[p]=17; original[p+1]=33; original[p+2]=77; original[p+3]=255; }
        D3D11_SUBRESOURCE_DATA data{ original.data(),desc.Width*4,0 };
        ComPtr<ID3D11Texture2D> source,image;
        checked(device->CreateTexture2D(&desc,&data,&source)); checked(device->CreateTexture2D(&desc,nullptr,&image));
        context->CopyResource(image.Get(),source.Get());
        dmr::DiagnosticProbe probe;
        probe.process(context.Get(),image.Get(),format,{7,41,1},10,logLine);
        auto actual = read(device.Get(),context.Get(),image.Get());
        require(read(device.Get(),context.Get(),source.Get()) == original,"game source remains byte-identical");
        const unsigned cell=53, strip=32, left=(640-6*cell)/2, top=360;
        bool exact=true;
        for (unsigned y=0; y<480; ++y) for (unsigned x=0; x<640; ++x) {
            const auto* pixel=actual.data()+(size_t(y)*640+x)*4;
            if (x>=left && x<left+6*cell && y>=top && y<top+strip) {
                auto expected=dmr::diagnosticPixel((x-left)/cell,(x-left)%cell,cell,srgb,bgra);
                exact &= std::memcmp(pixel,expected.data(),4)==0;
            } else exact &= std::memcmp(pixel,original.data()+(size_t(y)*640+x)*4,4)==0;
        }
        require(exact,"GPU output has exact premultiplied/additive pattern and untouched outside pixels in all formats");
        // Blocking fixture read above drained the query; production now polls
        // once, maps with DO_NOT_WAIT, and logs its independent staged snapshot.
        for (unsigned i=0; i<50 && matching("game-region=")==0; ++i) {
            context->Flush(); Sleep(1); probe.process(context.Get(),image.Get(),format,{7,42+i,1},10.1,logLine);
        }
        require(matching("game-region=")==4 && matching("swatch=")==6,"actual nonblocking GPU readback reports four game patches and six test patches");
        require(matching("synthetic_mismatch=0")==10 && matching("stopped")==0,"staged known pixels exactly match expected encoding, including additive alpha zero");
        require(matching("A=255/255.00/255")==6,"game alpha and two opaque control swatches are retained");
        unsigned first=matching("DMR alpha probe: sample=");
        probe.process(context.Get(),image.Get(),format,{7,99,1},11,logLine);
        require(matching("DMR alpha probe: sample=")==first,"five-second sampling cadence");
        // Exercise all 24 scheduled samples; use the same controlled source
        // before every frame, as the real compositor does.
        for (unsigned sample=1; sample<24; ++sample) {
            const double now=10+sample*5;
            context->CopyResource(image.Get(),source.Get());
            probe.process(context.Get(),image.Get(),format,{7,100+sample,1},now,logLine);
            read(device.Get(),context.Get(),image.Get());
            probe.process(context.Get(),image.Get(),format,{7,100+sample,1},now+.01,logLine);
        }
        require(matching("DMR alpha probe: sample=")==24,"readback budget is bounded to 24 snapshots per eye");
        context->CopyResource(image.Get(),source.Get());
        probe.process(context.Get(),image.Get(),format,{7,200,1},130,logLine);
        require(read(device.Get(),context.Get(),image.Get())==original,"120-second expiry removes test strip and leaves normal output byte-identical");
        require(matching("window complete")==1,"diagnostic expiry announced once");
        probe.process(context.Get(),image.Get(),format,{7,201,1},131,logLine);
        require(matching("window complete")==1,"expired diagnostic performs no further work");
    }
    // Unsupported data disables only this diagnostic and never throws into XR.
    lines.clear(); dmr::DiagnosticProbe failure;
    failure.process(nullptr,nullptr,DXGI_FORMAT_UNKNOWN,{0,0,0},0,logLine);
    failure.process(nullptr,nullptr,DXGI_FORMAT_UNKNOWN,{0,0,0},1,logLine);
    require(matching("probe stopped")==1,"optional diagnostic failure is contained and logged once");
    dmr::DiagnosticProbe logFailure;
    logFailure.process(nullptr,nullptr,DXGI_FORMAT_UNKNOWN,{0,0,0},0,failingLog);
    require(true,"a failing diagnostic logger cannot abort normal XR submission");
    std::printf("OpenXR diagnostics D3D11 WARP checks passed: %u (four formats, exact GPU pixels, nonblocking snapshots, expiry, no SteamVR/game/device deployment)\n",checks);
}
