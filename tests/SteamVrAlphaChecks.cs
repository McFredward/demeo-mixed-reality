using System;
using System.IO;
using GreenScreen;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

internal static class SteamVrAlphaChecks
{
    private sealed class Runtime : SteamVrAlpha.IBackend
    {
        public long Generation { get; set; } = 1;
        public bool Running { get; set; } = true;
        internal bool Supported = true, ThrowQuery;
        internal int Actual = 1, Requested, Writes, Queries;
        public bool SupportsAlpha() { Queries++; if (ThrowQuery) throw new Exception("query failed"); return Supported; }
        public int Mode { get => Actual; set { Writes++; Requested = value; } }
        internal void Submit() { if (Requested != 0) { Actual = Requested; Requested = 0; } }
    }

    internal static void Run(Action<bool, string> check)
    {
        var api = new Runtime(); var lease = new SteamVrAlpha.Lease(api);
        check(lease.Tick(0, out _) == SteamVrAlpha.Result.Pending && !lease.Active && api.Requested == 3,
            "native alpha request does not authorize transparency before Unity consumes it");
        check(lease.Tick(.1, out _) == SteamVrAlpha.Result.Pending && api.Writes == 1 && api.Queries == 1,
            "waiting does not repeat enumeration or writes");
        api.Submit();
        check(lease.Tick(.2, out _) == SteamVrAlpha.Result.Active && lease.Active,
            "actual native alpha acceptance admits source without a companion grant");
        lease.Stop();
        check(!lease.Active && api.Requested == 1, "opt-out revokes source and queues original mode");
        check(lease.Tick(.3, out _) == SteamVrAlpha.Result.Pending && api.Requested == 3,
            "rapid off/on cancels queued restore before admitting source again");
        api.Submit(); lease.Tick(.4, out _);
        api.Actual = 2; int writes = api.Writes;
        check(lease.Tick(.5, out _) == SteamVrAlpha.Result.Unsupported && !lease.Active && api.Writes == writes,
            "external mode change revokes transparency without fighting another owner");

        api = new Runtime { Actual = 3 }; lease = new SteamVrAlpha.Lease(api);
        check(lease.Tick(0, out _) == SteamVrAlpha.Result.Active && api.Writes == 0,
            "already active external alpha is used without claiming a write");
        lease.Stop(); check(api.Writes == 0, "external alpha is preserved on opt-out");

        api = new Runtime { Supported = false }; lease = new SteamVrAlpha.Lease(api);
        check(lease.Tick(0, out _) == SteamVrAlpha.Result.Unsupported && api.Writes == 0,
            "opaque-only runtime never receives unsupported alpha request");
        api.Supported = true; api.Generation++;
        check(lease.Tick(1, out _) == SteamVrAlpha.Result.Pending && api.Queries == 2,
            "a new real session rechecks capability rather than pinning a version string");
        api.Submit(); lease.Tick(2, out _);
        api.Generation++; api.Running = false; writes = api.Writes;
        check(!lease.Active, "lost session immediately revokes native source between frames");
        lease.Stop();
        check(api.Writes == writes, "dead session handles are never used to restore blend mode");
        check(lease.Tick(3, out _) == SteamVrAlpha.Result.Pending, "no running session remains pending");

        api = new Runtime { ThrowQuery = true }; lease = new SteamVrAlpha.Lease(api);
        check(lease.Tick(0, out _) == SteamVrAlpha.Result.Unsupported && api.Writes == 0,
            "failed capability query remains distinct from permission to clear alpha");

        api = new Runtime(); lease = new SteamVrAlpha.Lease(api);
        lease.Tick(0, out _); lease.Tick(10, out _); lease.Tick(11, out _);
        check(lease.Tick(12, out _) == SteamVrAlpha.Result.Unsupported && api.Requested == 1 && !lease.Active,
            "unaccepted request times out and cancels queued alpha");
        writes = api.Writes;
        lease.Tick(13, out _);
        check(api.Writes == writes, "rejected activation is not retried every frame");
        check(ModConfiguration.Parse(new[] { "[STEAMLINK]", "Backend=LegacyCompanion" }).SteamLinkBackend == PassthroughBackend.LegacyCompanion
            && ModConfiguration.Parse(new[] { "[STEAMLINK]", "Backend=steamvr" }).SteamLinkBackend == PassthroughBackend.SteamVR
            && ModConfiguration.Parse(new[] { "[STEAMLINK]", "Backend=99" }).SteamLinkBackend == PassthroughBackend.Auto,
            "legacy INI supports explicit native/companion choice and invalid values use auto");
    }

    internal static void RunRendering(Action<bool, string> check, Action tick, Action<bool> configure)
    {
        var factory = SteamVrAlpha.BackendFactory;
        var api = new Runtime();
        try
        {
            SteamVrAlpha.BackendFactory = () => api;
            var manager = UnityEngine.XR.Management.XRGeneralSettings.Instance.Manager;
            manager.activeLoader = new UnityEngine.XR.OpenXR.OpenXRLoader();
            UnityEngine.XR.OpenXR.OpenXRRuntime.name = "SteamVR/OpenXR";
            var camera = Camera.main;
            var eye = XRSettings.eyeTextureDesc;
            eye.dimension = TextureDimension.Tex2DArray; eye.volumeDepth = 2;
            XRSettings.eyeTextureDesc = eye;
            UnityEngine.XR.OpenXR.OpenXRSettings.ApplyLayout = false;
            UnityEngine.XR.OpenXR.OpenXRSettings.Instance.renderMode = UnityEngine.XR.OpenXR.OpenXRSettings.RenderMode.SinglePassInstanced;
            int layoutWrites = UnityEngine.XR.OpenXR.OpenXRSettings.Writes;
            NativePassthrough.Initialize(); configure(true);
            Time.unscaledTime += 1.1f; tick(); SteamLinkConnection.Frame(); tick();
            check(camera.backgroundColor == Color.black && !SteamLinkCoverageBridge.CanAcceptOrdinaryRgba,
                "native activation waits with opaque output and does not open the legacy bridge");
            api.Submit(); tick();
            check(camera.backgroundColor == Color.clear && NativePassthrough.OwnsSrpSource(camera)
                && !SteamLinkCoverageBridge.CanAcceptOrdinaryRgba,
                "native acceptance drives real shared camera/URP preparation without companion permission");
            check(XRSettings.eyeTextureDesc.dimension == TextureDimension.Tex2DArray
                && UnityEngine.XR.OpenXR.OpenXRSettings.Writes == layoutWrites && !OpenVrSubmission.Owned,
                "native SteamVR retains both array eyes and bypasses legacy submit/layout adapters");
            var data = new UnityEngine.Rendering.Universal.Internal.DrawObjectsPass.PassData {
                isOpaque = true, cameraData = new UnityEngine.Rendering.Universal.UniversalCameraData { camera = camera } };
            var raster = new UnityEngine.Rendering.RasterCommandBuffer();
            UnityEngine.Rendering.Universal.Internal.DrawObjectsPass.ExecutePass(raster, data, 0, 0, false);
            check(raster.Passes.Count == 2 && camera.backgroundColor == Color.clear,
                "native mode keeps same-eye opaque depth alpha repair before transparents");
            configure(false); tick();
            check(!SteamVrAlpha.Active && api.Requested == 1 && camera.backgroundColor == new Color(0, 1, 0, 1),
                "normal config opt-out restores native blend selection and colour-key camera");
            api.Submit(); configure(true); tick();
            UnityEngine.XR.OpenXR.OpenXRSettings.ApplyLayout = true;
            File.AppendAllText(ConfigurationStore.ActivePath, "Backend=LegacyCompanion\n");
            NativePassthrough.InvalidateCameras(); tick();
            SteamLinkConnection.Frame(); SteamLinkCoverageBridge.Grant(); tick();
            SteamLinkCoverageBridge.Grant(); tick();
            check(!SteamVrAlpha.Active && camera.backgroundColor == Color.clear && SteamLinkCoverageBridge.CanAcceptOrdinaryRgba,
                "explicit legacy config switches the real rendering lifecycle back to companion permission");
            NativePassthrough.Shutdown();
        }
        finally { NativePassthrough.Shutdown(); SteamVrAlpha.BackendFactory = factory; }
    }
}
