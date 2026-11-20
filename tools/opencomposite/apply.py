#!/usr/bin/env python3
"""Apply the reviewed Demeo adapter to the pinned, otherwise unchanged upstream."""
from pathlib import Path
import shutil
import sys

UPSTREAM = "cff07db75c4823afe93ed7027b03d5f7bc86f164"


def apply(root: Path):
    def edit(name, old, new):
        path = root / name
        text = path.read_text()
        if text.count(old) != 1:
            raise RuntimeError(f"Upstream context changed: {name}: {old[:70]!r}")
        path.write_text(text.replace(old, new))

    for name in ("dmr_state.hpp", "dmr_bridge.h", "dmr_bridge.cpp"):
        shutil.copy2(Path(__file__).parent / name, root / "OpenOVR" / name)

    edit("CMakeLists.txt", "set(CMAKE_SYSTEM_VERSION 10.0.17134.0)", "# Use the installed Windows SDK (D3D11-only Demeo adapter).")
    edit("CMakeLists.txt", "-DSUPPORT_DX -DSUPPORT_DX11 -DSUPPORT_DX12 -DSUPPORT_VK -DSUPPORT_GL", "-DSUPPORT_DX -DSUPPORT_DX11")
    edit("CMakeLists.txt", 'if (WIN32)\n\t# On Windows, use the bundled copy', 'if (WIN32)\n\t# Demeo uses D3D11; no Vulkan loader/minisdk dependency.\nelseif (FALSE)\n\t# On Windows, use the bundled copy')
    edit("CMakeLists.txt", "add_compile_definitions(XR_USE_GRAPHICS_API_OPENGL XR_USE_GRAPHICS_API_VULKAN)", "# Demeo adapter: retain only the D3D11 graphics binding.")
    edit("CMakeLists.txt", "XR_USE_GRAPHICS_API_D3D11 XR_USE_GRAPHICS_API_D3D12", "XR_USE_GRAPHICS_API_D3D11")
    edit("CMakeLists.txt", "OCOVR/openvr_api.cpp\n", "OCOVR/openvr_api.cpp\n\tOpenOVR/dmr_bridge.cpp\n")

    # Keep public OpenVR calls binary-compatible, including the game provider's
    # actual import table. Explicit LegacyCompanion forwards to the local backup.
    path = root / "OCOVR/openvr_api.cpp"
    text = path.read_text().replace('#include "stdafx.h"', '#include "stdafx.h"\n#include "dmr_bridge.h"')
    calls = {
        "VR_GetGenericInterface": "interfaceVersion, error", "VR_GetInitToken": "", "VR_GetStringForHmdError": "err",
        "VR_GetVRInitErrorAsEnglishDescription": "error", "VR_GetVRInitErrorAsSymbol": "error",
        "VR_InitInternal": "peError, eApplicationType", "VR_InitInternal2": "peError, eApplicationType, pStartupInfo",
        "VR_IsHmdPresent": "", "VR_IsInterfaceVersionValid": "pchInterfaceVersion", "VR_IsRuntimeInstalled": "", "VR_RuntimePath": "",
        "VR_ShutdownInternal": "",
    }
    import re
    for name, args in calls.items():
        pattern = r"(VR_INTERFACE[^\n]+\b" + name + r"\([^\n]*\)\n\{\n)"
        text, count = re.subn(pattern, lambda match: match[1] + f'\tif (auto original = dmrLegacy<decltype(&{name})>("{name}")) return original({args});\n', text)
        if count != 1:
            raise RuntimeError("Missing OpenVR export body: " + name)
    text = text.replace('OOVR_ABORT("Stub");\n}\n\nVR_INTERFACE uint32_t VR_CALLTYPE VR_InitInternal',
                        'return "VRInitError_Unknown";\n}\n\nVR_INTERFACE uint32_t VR_CALLTYPE VR_InitInternal')
    # Native RuntimePath must not abort if the Unity provider asks for it.
    old = '\tif (auto original = dmrLegacy<decltype(&VR_RuntimePath)>("VR_RuntimePath")) return original();\n\tOOVR_ABORT("Stub");'
    assert text.count(old) == 1
    text = text.replace(old, old.split('\n')[0] + '\n\treturn dmrRuntimePath();')
    text += '''
// XRSDKOpenVR imports this private convenience export as well as public API.
VR_INTERFACE void* VR_CALLTYPE VRHeadsetView()
{
    if (auto original = dmrLegacy<decltype(&VRHeadsetView)>("VRHeadsetView")) return original();
    vr::EVRInitError error = vr::VRInitError_None;
    return VR_GetGenericInterface("IVRHeadsetView_001", &error);
}
VR_INTERFACE bool VR_CALLTYPE VR_GetRuntimePath(char* buffer, uint32_t capacity, uint32_t* required)
{
    if (auto original = dmrLegacy<decltype(&VR_GetRuntimePath)>("VR_GetRuntimePath")) return original(buffer, capacity, required);
    const char* path = dmrRuntimePath();
    uint32_t bytes = static_cast<uint32_t>(strlen(path) + 1);
    if (required) *required = bytes;
    if (!buffer || capacity < bytes) return false;
    memcpy(buffer, path, bytes);
    return true;
}
'''
    path.write_text(text)

    edit("DrvOpenXR/DrvOpenXR.cpp", '#include "DrvOpenXR.h"', '#include "DrvOpenXR.h"\n#include "dmr_bridge.h"')
    edit("DrvOpenXR/DrvOpenXR.cpp", "\tcurrentBackend->OnSessionCreated();", "\tdmrSessionCreated(xr_instance, xr_system);\n\tcurrentBackend->OnSessionCreated();")
    edit("DrvOpenXR/DrvOpenXR.cpp", "void DrvOpenXR::ShutdownSession()\n{", "void DrvOpenXR::ShutdownSession()\n{\n\tdmrAlpha.running(false);")
    edit("DrvOpenXR/XrBackend.cpp", '#include "XrBackend.h"', '#include "XrBackend.h"\n#include "dmr_bridge.h"')
    edit("DrvOpenXR/XrBackend.cpp", "void XrBackend::WaitForTrackingData()\n{", "void XrBackend::WaitForTrackingData()\n{\n\tdmrAlpha.beginFrame();")
    # First graphics-API handoff starts a *new* session. Begin its frame before
    # copying either eye, rather than presenting an uninitialized first image.
    path = root / "DrvOpenXR/XrBackend.cpp"
    text = path.read_text()
    start = text.index("\t// If WaitGetPoses was called before the first texture was submitted")
    end = text.index("\n}\n\nvoid XrBackend::SubmitFrames", start)
    deferred = text[start:end]
    text = text[:start] + text[end:]
    anchor = "\tXrCompositionLayerProjectionView& layer = projectionViews[eye];"
    assert text.count(anchor) == 1
    text = text.replace(anchor, deferred + "\n\n" + anchor)
    old = "\tif (sessionActive && renderingFrame)\n\t\tcomp.Invoke(texture, bounds, layer.subImage, (XruEye)eye, submitFlags);"
    assert text.count(old) == 1
    text = text.replace(old, "\tif (sessionActive && renderingFrame) {\n\t\tcomp.Invoke(texture, bounds, layer.subImage, (XruEye)eye, submitFlags);\n\t\tdmrAlpha.eye(static_cast<uint32_t>(eye));\n\t}")
    anchor = "\tinfo.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;"
    # SubmitFrames only: skybox handling has a separate original opaque call.
    where = text.index(anchor, text.index("void XrBackend::SubmitFrames"))
    text = text[:where] + text[where:].replace(anchor, "\tconst auto dmrFrame = dmrAlpha.frame();\n\tinfo.environmentBlendMode = dmrFrame.alpha ? XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND : XR_ENVIRONMENT_BLEND_MODE_OPAQUE;", 1)
    old = "\tif (submittedEyeTextures) {"
    assert text.count(old) == 1
    text = text.replace(old, "\tif (submittedEyeTextures && (!dmrFrame.alpha || dmrFrame.pair)) {\n\t\tif (dmrFrame.alpha) mainLayer.layerFlags |= XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;")
    old = "\tOOVR_FAILED_XR_SOFT_ABORT(xrEndFrame(xr_session.get(), &info));"
    text = text.replace(old, "\tXrResult dmrResult = xrEndFrame(xr_session.get(), &info);\n\tdmrAlpha.presented(dmrFrame, XR_SUCCEEDED(dmrResult));\n\tOOVR_FAILED_XR_SOFT_ABORT(dmrResult);", 1)
    text = text.replace("\t\t\tsessionActive = true;", "\t\t\tsessionActive = true;\n\t\t\tdmrAlpha.running(true);", 1)
    text = text.replace("\t\tcase XR_SESSION_STATE_STOPPING: {", "\t\tcase XR_SESSION_STATE_STOPPING: {\n\t\t\tdmrAlpha.running(false);", 1)
    text = text.replace("\t\tcase XR_SESSION_STATE_LOSS_PENDING: {", "\t\tcase XR_SESSION_STATE_LOSS_PENDING: {\n\t\t\tdmrAlpha.running(false);", 1)
    path.write_text(text)

    # Preserve both Unity array eyes through MSAA. Upstream resolves every eye
    # into subresource 0 but copies eye 1 from subresource 1, leaving it undefined.
    edit("OpenOVR/Compositor/dx11compositor.cpp", "resDesc.SampleDesc.Count = 1;\n\n\t\t\tresolvedMSAATextures", "resDesc.SampleDesc.Count = 1;\n\t\t\tresDesc.SampleDesc.Quality = 0;\n\t\t\tresDesc.Format = type;\n\n\t\t\tresolvedMSAATextures")
    edit("OpenOVR/Compositor/dx11compositor.cpp", "\t\t\tcontext->ResolveSubresource(resolvedMSAATextures[currentIndex], 0, src, arrayIndex, resDesc.Format);", "\t\t\tconst UINT subresource = D3D11CalcSubresource(0, arrayIndex, srcDesc.MipLevels);\n\t\t\tcontext->ResolveSubresource(resolvedMSAATextures[currentIndex], subresource, src, subresource, static_cast<DXGI_FORMAT>(createInfo.format));")
    edit("OpenOVR/Compositor/dx11compositor.cpp", "0, resolvedMSAATextures[currentIndex], arrayIndex, &sourceRegion);", "0, resolvedMSAATextures[currentIndex], subresource, &sourceRegion);")
    # No binary provider settings / per-eye copy adapter is needed on this path.


if __name__ == "__main__":
    apply(Path(sys.argv[1]).resolve())
