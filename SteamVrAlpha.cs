#nullable disable
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace GreenScreen
{
    // SteamOS 0.5.8 / PC SteamVR beta added streamed OpenXR ALPHA_BLEND.
    // Use the provider's real instance (Unity OpenXR or game-local OpenComposite)
    // and submission mode. No PC/Frame patch, second diagnostic XR instance,
    // global camera setting or companion grant is involved.
    internal static class SteamVrAlpha
    {
        internal enum Result { Unsupported, Pending, Active }
        internal interface IBackend
        {
            long Generation { get; }
            bool Running { get; }
            bool SupportsAlpha();
            int Mode { get; set; }
        }

        private static IBackend _backend;
        internal static Func<IBackend> BackendFactory = () => OpenCompositeAlpha.TryOpen() ?? UnityBackend.Open();
        private static Lease _lease;
        private static string _unavailable;
        private static double _nextBind;
        internal static bool Active => _lease != null && _lease.Active;
        internal static bool CanPrepare => _lease != null && _lease.CanPrepare;

        internal static void Initialize()
        {
            if (_backend != null) return;
            try
            {
                _backend = BackendFactory();
                _lease = new Lease(_backend);
                _unavailable = null;
            }
            catch (Exception error) { _unavailable = "OpenXR alpha control API unavailable: " + error.GetType().Name; }
        }

        internal static Result Tick(bool requested, double now, out string reason)
        {
            if (!requested) { Stop(); reason = null; return Result.Unsupported; }
            // Unity can load XRSDKOpenVR/openvr_api after the mod. Bind only
            // the already-loaded bridge, never LoadLibrary a second XR owner.
            if (now >= _nextBind && (_backend == null || _backend is UnityBackend))
            {
                _nextBind = now + 1;
                var translated = OpenCompositeAlpha.TryOpen();
                if (translated != null && !ReferenceEquals(translated, _backend))
                {
                    Stop(); (_backend as UnityBackend)?.Dispose();
                    _backend = translated; _lease = new Lease(translated); _unavailable = null;
                }
            }
            if (_lease == null) { reason = _unavailable ?? "OpenXR alpha control API unavailable."; return Result.Unsupported; }
            return _lease.Tick(now, out reason);
        }
        internal static void Stop() => _lease?.Stop();
        internal static void Shutdown()
        {
            Stop();
            (_backend as UnityBackend)?.Dispose();
            _backend = null; _lease = null; _unavailable = null; _nextBind = 0;
        }

        // Mode changes are queued by Unity. A successful Set is not acceptance.
        // Bind ownership to the real session generation and confirm Get on a
        // subsequent tick before allowing transparent game-camera clears.
        internal sealed class Lease
        {
            private readonly IBackend _api;
            private long _generation = -1;
            private bool _active, _pending, _checked, _supported, _failed;
            private int _previous, _restore, _observations;
            private double _requestedAt;
            private string _failure;
            internal Lease(IBackend api) { _api = api; }
            internal bool Active => _active && _api.Running && _generation == _api.Generation;
            internal bool CanPrepare => (_pending || _active) && _checked && _supported && !_failed
                && _api.Running && _generation == _api.Generation;

            internal Result Tick(double now, out string reason)
            {
                reason = null;
                if (_generation != _api.Generation) { Forget(); _checked = _failed = false; _restore = 0; _generation = _api.Generation; }
                if (!_api.Running)
                {
                    Forget(); reason = "Waiting for the game's OpenXR session; restart the game if the mod was loaded after XR startup.";
                    return Result.Pending;
                }
                if (_failed) { reason = _failure; return Result.Unsupported; }
                try
                {
                    if (!_checked) { _supported = _api.SupportsAlpha(); _checked = true; }
                    if (!_supported)
                    {
                        reason = "The active SteamVR OpenXR system does not offer ALPHA_BLEND. Use the current PC SteamVR and Steam Frame beta, or LegacyCompanion.";
                        return Result.Unsupported;
                    }
                    int actual = _api.Mode;
                    if (actual < 1 || actual > 3) throw new InvalidOperationException("Invalid Unity environment blend mode.");
                    if (_active)
                    {
                        if (actual == 3) return Result.Active;
                        throw new InvalidOperationException("Environment blend mode changed outside the mod.");
                    }
                    if (_pending)
                    {
                        if (actual == 3) { _pending = false; _active = true; return Result.Active; }
                        if (actual != _previous) throw new InvalidOperationException("Environment blend mode changed while alpha was pending.");
                        if (++_observations >= 3 && now - _requestedAt >= 3)
                            throw new InvalidOperationException("OpenXR did not accept ALPHA_BLEND within the activation deadline.");
                    }
                    else if (actual == 3 && _restore == 0)
                    {
                        _previous = 3; _active = true; return Result.Active; // Externally owned, no write.
                    }
                    else
                    {
                        _previous = _restore != 0 && actual == 3 ? _restore : actual;
                        _restore = 0; _pending = true; _requestedAt = now; _observations = 0;
                        _api.Mode = 3;
                    }
                    reason = "Waiting for native SteamVR OpenXR alpha blending to be accepted at frame submission.";
                    return Result.Pending;
                }
                catch (Exception error)
                {
                    Stop(); _failed = true;
                    reason = _failure = "Native SteamVR OpenXR alpha unavailable: " + error.GetType().Name + ": " + error.Message;
                    return Result.Unsupported;
                }
            }

            internal void Stop()
            {
                bool restore = (_pending || _active) && _previous != 3 && _previous > 0;
                bool pending = _pending;
                int previous = _previous;
                Forget();
                if (!restore) return;
                try
                {
                    if (_generation != _api.Generation || !_api.Running) return;
                    int actual = _api.Mode;
                    if (actual != 3 && !(pending && actual == previous)) return;
                    _restore = previous;
                    _api.Mode = previous;
                }
                catch (Exception error) { Utils.LogDebug("SteamVR alpha mode restoration unavailable: " + error.GetType().Name); }
            }
            private void Forget() { _active = _pending = false; _previous = _observations = 0; }
        }

        private static void BeforeNativeEvent(object[] __args)
        {
            try { (_backend as UnityBackend)?.Observe(__args[0].ToString(), Convert.ToUInt64(__args[1])); }
            catch (Exception error)
            {
                // Never let an optional observer interfere with the game's XR startup/shutdown.
                (_backend as UnityBackend)?.Lost();
                Utils.LogDebug("SteamVR alpha lifecycle observer unavailable: " + error.GetType().Name);
            }
        }

        private sealed class UnityBackend : IBackend, IDisposable
        {
            private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            private readonly MethodInfo _get, _set, _events;
            private readonly PropertyInfo _proc;
            private readonly Type _mode;
            private readonly HarmonyLib.Harmony _patches;
            private readonly MethodInfo _prefix = typeof(SteamVrAlpha).GetMethod(nameof(BeforeNativeEvent), Static);
            private ulong _instance, _system, _session;
            private bool _begun;
            public long Generation { get; private set; }
            public bool Running => _instance != 0 && _system != 0 && _session != 0 && _begun;
            public int Mode
            {
                get => Convert.ToInt32(_get.Invoke(null, null));
                set => _set.Invoke(null, new[] { Enum.ToObject(_mode, value) });
            }

            private UnityBackend(Type feature, Type loader)
            {
                _get = feature.GetMethod("GetEnvironmentBlendMode", Static);
                _set = feature.GetMethod("SetEnvironmentBlendMode", Static);
                _proc = feature.GetProperty("xrGetInstanceProcAddr", Static);
                _events = loader.GetMethod("ReceiveNativeEvent", Static);
                if (_get == null || _set == null || _proc?.PropertyType != typeof(IntPtr) || _events == null)
                    throw new MissingMemberException("Unity OpenXR blend/lifecycle API");
                _mode = _get.ReturnType;
                var args = _events.GetParameters();
                if (!_mode.IsEnum || Convert.ToInt32(Enum.Parse(_mode, "AlphaBlend")) != 3
                    || _set.ReturnType != typeof(void) || _set.GetParameters().Length != 1 || _set.GetParameters()[0].ParameterType != _mode
                    || args.Length != 2 || !args[0].ParameterType.IsEnum || args[1].ParameterType != typeof(ulong))
                    throw new NotSupportedException("Unexpected Unity OpenXR API shape.");
                foreach (string name in new[] { "XrInstanceChanged", "XrSystemIdChanged", "XrSessionChanged", "XrBeginSession",
                    "XrEndSession", "XrDestroySession", "XrDestroyInstance", "XrLossPending", "XrInstanceLossPending" })
                    Enum.Parse(args[0].ParameterType, name); // Validate names, never assume private enum ordinals.
                _patches = new HarmonyLib.Harmony("McFredward.DemeoMixedReality.SteamVrAlpha");
                _patches.Patch(_events, prefix: new HarmonyMethod(_prefix));
            }
            internal static UnityBackend Open()
            {
                if (IntPtr.Size != 8) throw new NotSupportedException("SteamVR alpha integration requires a 64-bit game process.");
                Type feature = SteamLinkConnectionLifecycle.FindType("UnityEngine.XR.OpenXR.Features.OpenXRFeature")
                    ?? Type.GetType("UnityEngine.XR.OpenXR.Features.OpenXRFeature, Unity.XR.OpenXR", false);
                Type loader = SteamLinkConnectionLifecycle.FindType("UnityEngine.XR.OpenXR.OpenXRLoaderBase");
                if (feature == null || loader == null) throw new NotSupportedException("This game has no Unity OpenXR loader.");
                return new UnityBackend(feature, loader);
            }
            internal void Observe(string name, ulong payload)
            {
                switch (name)
                {
                    case "XrInstanceChanged":
                        if (_instance != 0 && _instance != payload) Lost();
                        _instance = payload; Generation++; break;
                    case "XrSystemIdChanged": _system = payload; Generation++; break;
                    case "XrSessionChanged": _session = payload; _begun = false; Generation++; break;
                    case "XrBeginSession": if (_session == payload) { _begun = true; Generation++; } break;
                    case "XrEndSession": case "XrStopping": case "XrExiting":
                        if (payload == _session || name == "XrStopping") { Stop(); _begun = false; Generation++; }
                        break;
                    case "XrDestroySession":
                        if (payload == _session) { Stop(); _begun = false; _session = 0; Generation++; }
                        break;
                    case "XrLossPending": _begun = false; _session = 0; Generation++; break;
                    case "XrDestroyInstance": case "XrInstanceLossPending": Lost(); break;
                }
            }
            internal void Lost() { _instance = _system = _session = 0; _begun = false; Generation++; }
            public bool SupportsAlpha()
            {
                IntPtr address = (IntPtr)_proc.GetValue(null);
                if (!Running || address == IntPtr.Zero) throw new InvalidOperationException("No live OpenXR instance.");
                var get = Marshal.GetDelegateForFunctionPointer<GetProc>(address);
                if (get(_instance, "xrEnumerateEnvironmentBlendModes", out IntPtr function) != 0 || function == IntPtr.Zero)
                    throw new NotSupportedException("xrEnumerateEnvironmentBlendModes unavailable.");
                var enumerate = Marshal.GetDelegateForFunctionPointer<EnumerateModes>(function);
                if (enumerate(_instance, _system, 2, 0, out uint count, IntPtr.Zero) != 0 || count == 0 || count > 16)
                    throw new InvalidOperationException("OpenXR environment blend mode count failed.");
                IntPtr modes = Marshal.AllocHGlobal(checked((int)count * 4));
                try
                {
                    for (int i = 0; i < count; i++) Marshal.WriteInt32(modes, i * 4, 0);
                    if (enumerate(_instance, _system, 2, count, out uint written, modes) != 0 || written == 0 || written > count)
                        throw new InvalidOperationException("OpenXR environment blend enumeration failed.");
                    bool alpha = false;
                    for (int i = 0; i < written; i++)
                    {
                        int mode = Marshal.ReadInt32(modes, i * 4);
                        if (mode <= 0) throw new InvalidOperationException("Invalid OpenXR environment blend mode.");
                        alpha |= mode == 3;
                    }
                    Utils.LogInfo("SteamVR OpenXR alpha capability: " + (alpha ? "available" : "not offered") + "; queried the game's live PRIMARY_STEREO system.");
                    return alpha;
                }
                finally { Marshal.FreeHGlobal(modes); GC.KeepAlive(get); GC.KeepAlive(enumerate); }
            }
            public void Dispose() { _patches.Unpatch(_events, _prefix); Lost(); }
            [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
            private delegate int GetProc(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate int EnumerateModes(ulong instance, ulong system, int views, uint capacity, out uint count, IntPtr modes);
        }
    }
}
