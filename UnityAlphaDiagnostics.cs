#nullable disable
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GreenScreen
{
#if DEMEOBATTLEMARKED
    // Battlemarked already owns OpenXR. Record on its current final-blit
    // attachment; no OpenComposite, extra camera, target switch or XR out ABI.
    internal static class UnityAlphaDiagnostics
    {
        private static HarmonyLib.Harmony _patch;
        private static MethodInfo _target, _draw;
        private static Func<object, object> _camera, _resolve, _xr, _xrEnabled, _alpha, _conversion;
        private static AssetBundle _bundle;
        private static Material _material;
        private static bool _attempted, _failed, _reported, _expired;
        private static float _start = -1;
        private static float _nextReport;
        private static float _waitingSince = -1;
        private static bool _waitingReported;
        private static bool LiveAlpha()
        {
            try { return SteamVrAlpha.Active; }
            catch { return false; }
        }

        internal static void Tick()
        {
            var settings = ConfigurationStore.Current;
            if (!settings.OpenXRAlphaDiagnostics || !settings.SteamLinkNativePassthrough
                || settings.SteamLinkBackend == PassthroughBackend.LegacyCompanion) { Shutdown(); return; }
            if (_attempted)
            {
                if (!_failed && !_reported && LiveAlpha())
                {
                    if (_waitingSince < 0) _waitingSince = Time.unscaledTime;
                    if (!_waitingReported && Time.unscaledTime - _waitingSince > 10)
                    {
                        _waitingReported = true;
                        Utils.LogWarning("Battlemarked alpha diagnostics: no owned final XR blit reached the test-strip hook yet; no test pixels have been drawn. Normal passthrough retained.");
                    }
                }
                return;
            }
            _attempted = true;
            try
            {
                Type pass = SteamLinkConnectionLifecycle.FindType("UnityEngine.Rendering.Universal.Internal.FinalBlitPass");
                _target = pass?.GetMethod("ExecutePass", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var args = _target?.GetParameters();
                if (args == null || args.Length != 5 || _target.ReturnType != typeof(void)
                    || !args[0].ParameterType.FullName.EndsWith("UnityEngine.Rendering.RasterCommandBuffer", StringComparison.Ordinal))
                    throw new MissingMethodException("Battlemarked URP FinalBlitPass.ExecutePass");
                _camera = SrpOpaqueCoverage.Reader(args[4].ParameterType, "camera", out Type cameraType);
                _resolve = SrpOpaqueCoverage.Reader(args[4].ParameterType, "resolveFinalTarget", out Type resolveType);
                _xr = SrpOpaqueCoverage.Reader(args[4].ParameterType, "xr", out Type xrType);
                _xrEnabled = SrpOpaqueCoverage.Reader(xrType, "enabled", out Type enabledType);
                _alpha = SrpOpaqueCoverage.Reader(args[1].ParameterType, "enableAlphaOutput", out Type alphaType);
                _conversion = SrpOpaqueCoverage.Reader(args[1].ParameterType, "requireSrgbConversion", out Type conversionType);
                if (cameraType != typeof(Camera) || resolveType != typeof(bool) || enabledType != typeof(bool) || alphaType != typeof(bool) || conversionType != typeof(bool))
                    throw new MissingMemberException("Battlemarked final XR camera contract");
                _draw = args[0].ParameterType.GetMethod("DrawProcedural", new[] {
                    typeof(Matrix4x4), typeof(Material), typeof(int), typeof(UnityEngine.MeshTopology), typeof(int) });
                if (_draw == null) throw new MissingMethodException("Final raster DrawProcedural");
                using (Stream stream = typeof(UnityAlphaDiagnostics).Assembly.GetManifestResourceStream("GreenScreen.AlphaDiagnostics"))
                {
                    if (stream == null) throw new InvalidDataException("Missing owned diagnostic shader bundle");
                    using (var data = new MemoryStream()) { stream.CopyTo(data); _bundle = UnityShaderLoader.LoadBundle(data.ToArray()); }
                }
                if (_bundle == null) throw new InvalidDataException("Diagnostic bundle could not be loaded");
                Shader shader = UnityShaderLoader.LoadShader(_bundle,"assets/alphadiagnostics.shader");
                if (shader == null || shader.name != "Hidden/Demeo/AlphaDiagnostics" || !shader.isSupported)
                    throw new InvalidDataException("Diagnostic shader unavailable");
                _bundle.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                if (_material.passCount != 1 || _material.FindPass("ALPHA_TEST_STRIP") != 0)
                    throw new InvalidDataException("Unexpected diagnostic shader pass");
                _patch = new HarmonyLib.Harmony("McFredward.DemeoMixedReality.UnityAlphaDiagnostics");
                _patch.Patch(_target, postfix: new HarmonyMethod(typeof(UnityAlphaDiagnostics).GetMethod(nameof(AfterFinalBlit), BindingFlags.Static | BindingFlags.NonPublic)));
                Utils.LogInfo("Battlemarked OpenXR alpha diagnostics ready: waiting for the owned native XR camera's final blit; no OpenComposite needed.");
            }
            catch (Exception error) { Fail(error); }
        }

        private static void AfterFinalBlit(object __0, object __1, object __4)
        {
            if (_failed || _expired || !ConfigurationStore.Current.OpenXRAlphaDiagnostics || !LiveAlpha()) return;
            try
            {
                Camera camera = _camera(__4) as Camera;
                if (!NativePassthrough.OwnsSrpSource(camera) || camera.targetTexture != null
                    || !(_resolve(__4) is bool resolve) || !resolve || !(_xrEnabled(_xr(__4)) is bool xr) || !xr) return;
                if (_start < 0) _start = Time.unscaledTime;
                if (Time.unscaledTime - _start >= 120)
                {
                    _expired = true;
                    Utils.LogInfo("Battlemarked OpenXR alpha diagnostics: 120-second window complete; test strip removed.");
                    return;
                }
                _draw.Invoke(__0, new object[] { Matrix4x4.identity, _material, 0, UnityEngine.MeshTopology.Triangles, 3 });
                if (!_reported || Time.unscaledTime >= _nextReport)
                {
                    Utils.LogInfo("Battlemarked OpenXR alpha test strip drawn after final blit; camera=" + camera.name
                        + "; UnityFrame=" + Time.frameCount + "; finalAlpha=" + _alpha(__1)
                        + "; colourSpace=" + QualitySettings.activeColorSpace
                        + "; finalSrgbConversion=" + _conversion(__1)
                        + "; cells: clear / opaque grey / opaque green / half white / additive light A0 / soft grey edge. No GPU readback or downstream pixel proof.");
                    _reported = true; _nextReport = Time.unscaledTime + 30;
                }
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Fail(Exception error)
        {
            _failed = true;
            try { Remove(); } catch { /* A failed optional cleanup cannot revoke the game's XR mode. */ }
            Utils.LogWarning("Battlemarked alpha diagnostic unavailable: " + error.GetType().Name + ": " + error.Message + "; normal passthrough retained.");
        }
        private static void Remove()
        {
            if (_patch != null && _target != null) _patch.Unpatch(_target, typeof(UnityAlphaDiagnostics).GetMethod(nameof(AfterFinalBlit), BindingFlags.Static | BindingFlags.NonPublic));
            _patch = null;
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _material = null;
            if (_bundle != null) _bundle.Unload(true);
            _bundle = null;
        }
        internal static void Shutdown()
        {
            Remove(); _attempted = _failed = _reported = _expired = false; _start = -1; _nextReport = 0;
            _waitingSince = -1; _waitingReported = false;
            _target = _draw = null; _camera = _resolve = _xr = _xrEnabled = _alpha = _conversion = null;
        }
    }
#else
    internal static class UnityAlphaDiagnostics
    {
        internal static void Tick() { }
        internal static void Shutdown() { }
    }
#endif
}
