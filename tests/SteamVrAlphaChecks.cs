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
        private long _generation = 1;
        internal bool ThrowReads;
        public long Generation { get { if (ThrowReads) throw new Exception("control API retired"); return _generation; } set => _generation = value; }
        public bool Running { get; set; } = true;
        internal bool Supported = true, ThrowQuery;
        internal int Actual = 1, Requested, Writes, Queries;
        public bool SupportsAlpha() { Queries++; if (ThrowQuery) throw new Exception("query failed"); return Supported; }
        public int Mode { get => Actual; set { Writes++; Requested = value; } }
        internal void Submit() { if (Requested != 0) { Actual = Requested; Requested = 0; } }
    }

    internal static void Run(Action<bool, string> check)
    {
        check(System.Runtime.InteropServices.Marshal.SizeOf<OpenCompositeAlpha.State>() == 24
            && System.Runtime.InteropServices.Marshal.OffsetOf<OpenCompositeAlpha.State>("Generation").ToInt32() == 8,
            "managed OpenComposite state matches native 24-byte x64 ABI");
        var state = new OpenCompositeAlpha.State { Version = 1, Bytes = 24, Generation = 1, Flags = 1, Mode = 1 };
        check(state.Valid, "versioned native OpenComposite state is accepted");
        state.Version = 2;
        check(!state.Valid, "unknown OpenComposite state protocol is refused");
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
        api = new Runtime(); lease = new SteamVrAlpha.Lease(api);
        lease.Tick(0, out _); api.Submit(); lease.Tick(1, out _);
        api.ThrowReads = true; writes = api.Writes;
        lease.Stop();
        check(!lease.Active && api.Writes == writes, "retired native API during shutdown cannot throw past cleanup or receive a restoration write");
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
            manager.activeLoader = new Unity.XR.OpenVR.OpenVRLoader();
            NativePassthrough.Initialize(); configure(true);
            Time.unscaledTime += 1.1f; tick(); SteamLinkConnection.Frame(); SteamLinkCoverageBridge.Grant(); tick();
            check(Camera.main.backgroundColor == Color.black && !SteamLinkCoverageBridge.CanAcceptOrdinaryRgba && !OpenVrSubmission.Owned,
                "Auto never silently falls back to companion permission on an untranslated OpenVR loader");
            NativePassthrough.Shutdown();
            manager.activeLoader = new UnityEngine.XR.OpenXR.OpenXRLoader();
            UnityEngine.XR.OpenXR.OpenXRRuntime.name = "SteamVR/OpenXR";
            var camera = Camera.main;
            var eye = XRSettings.eyeTextureDesc;
            eye.dimension = TextureDimension.Tex2DArray; eye.volumeDepth = 2;
            XRSettings.eyeTextureDesc = eye;
            UnityEngine.XR.OpenXR.OpenXRSettings.ApplyLayout = false;
            UnityEngine.XR.OpenXR.OpenXRSettings.Instance.renderMode = UnityEngine.XR.OpenXR.OpenXRSettings.RenderMode.SinglePassInstanced;
            int layoutWrites = UnityEngine.XR.OpenXR.OpenXRSettings.Writes;
            var renderer = (UnityEngine.Rendering.Universal.UniversalRenderer)
                ((UnityEngine.Rendering.Universal.UniversalAdditionalCameraData)camera.Extra).scriptableRenderer;
            renderer.renderingModeActual = UnityEngine.Rendering.Universal.UniversalRenderer.RenderingMode.Deferred;
            NativePassthrough.Initialize(); configure(true);
            Time.unscaledTime += 1.1f; tick(); SteamLinkConnection.Frame(); tick();
            check(api.Writes == 0 && !SteamVrAlpha.Active && camera.backgroundColor == Color.black,
                "unsupported camera profile cannot queue native alpha before source preparation");
            renderer.renderingModeActual = UnityEngine.Rendering.Universal.UniversalRenderer.RenderingMode.Forward;
            tick();
            check(camera.backgroundColor == Color.black && !SteamLinkCoverageBridge.CanAcceptOrdinaryRgba,
                "native activation waits with opaque output and does not open the legacy bridge");
            check(NativePassthrough.OwnsSrpSource(camera) && !SteamVrAlpha.Active,
                "pending native mode prepares current-frame alpha before its first frame-end submission");
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
#if DEMEOBATTLEMARKED
            check(HarmonyLib.Harmony.FinalPostfix == null,"Battlemarked diagnostics install no hook by default");
            ConfigurationStore.Current.OpenXRAlphaDiagnostics = true;
            UnityAlphaDiagnostics.Tick();
            check(HarmonyLib.Harmony.FinalPostfix != null,"Battlemarked opt-in binds the actual final blit without OpenComposite");
            var finalData = new UnityEngine.Rendering.Universal.UniversalCameraData { camera = camera };
            var finalPass = new UnityEngine.Rendering.Universal.Internal.FinalBlitPass.PassData { enableAlphaOutput = true };
            var finalCommands = new UnityEngine.Rendering.RasterCommandBuffer();
            int queries = XRDisplaySubsystem.ParameterQueries, reads = AsyncGPUReadback.Requests;
            UnityEngine.Rendering.Universal.Internal.FinalBlitPass.ExecutePass(finalCommands,finalPass,new RTHandle(),new RTHandle(),finalData);
            check(finalCommands.Passes.Count == 1 && camera.backgroundColor == Color.clear && SteamVrAlpha.Active,
                "Battlemarked draws one test strip on its owned final XR attachment without altering native passthrough");
            check(XRDisplaySubsystem.ParameterQueries == queries && AsyncGPUReadback.Requests == reads,
                "Battlemarked visual diagnosis never calls unsafe XR out parameters or GPU readback");
            finalData.resolveFinalTarget = false;
            UnityEngine.Rendering.Universal.Internal.FinalBlitPass.ExecutePass(finalCommands,finalPass,new RTHandle(),new RTHandle(),finalData);
            finalData.resolveFinalTarget = true; finalData.xr.enabled = false;
            UnityEngine.Rendering.Universal.Internal.FinalBlitPass.ExecutePass(finalCommands,finalPass,new RTHandle(),new RTHandle(),finalData);
            finalData.xr.enabled = true; finalData.camera = new Camera();
            UnityEngine.Rendering.Universal.Internal.FinalBlitPass.ExecutePass(finalCommands,finalPass,new RTHandle(),new RTHandle(),finalData);
            check(finalCommands.Passes.Count == 1,"diagnostic refuses intermediates, spectator outputs and other cameras");
            finalData.camera = camera;
            Time.frameCount++;
            UnityEngine.Rendering.Universal.Internal.FinalBlitPass.ExecutePass(finalCommands,finalPass,new RTHandle(),new RTHandle(),finalData);
            check(finalCommands.Passes.Count == 1,"a stale game frame cannot draw a diagnostic strip");
            Time.unscaledTime += 121; tick();
            UnityEngine.Rendering.Universal.Internal.FinalBlitPass.ExecutePass(finalCommands,finalPass,new RTHandle(),new RTHandle(),finalData);
            check(finalCommands.Passes.Count == 1 && SteamVrAlpha.Active,"120-second timeout removes only test pixels and retains native alpha");
            ConfigurationStore.Current.OpenXRAlphaDiagnostics = false; UnityAlphaDiagnostics.Tick();
            check(HarmonyLib.Harmony.FinalPostfix == null,"diagnostic opt-out unpatches and releases only owned assets");
            AssetBundle.Reject = true; ConfigurationStore.Current.OpenXRAlphaDiagnostics = true; UnityAlphaDiagnostics.Tick();
            check(HarmonyLib.Harmony.FinalPostfix == null && SteamVrAlpha.Active,"diagnostic asset failure leaves normal native passthrough active");
            AssetBundle.Reject = false; ConfigurationStore.Current.OpenXRAlphaDiagnostics = false; UnityAlphaDiagnostics.Tick();
#endif
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
