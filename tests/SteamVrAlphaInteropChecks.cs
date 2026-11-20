using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GreenScreen;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

internal static class SteamVrAlphaInteropChecks
{
    internal static int Queries, Events;
    internal static bool Alpha = true;
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }
    private static void Event(OpenXRFeature.NativeEvent name, ulong payload) => OpenXRLoaderBase.ReceiveNativeEvent(name, payload);
    private static void Session(ulong session)
    {
        Event(OpenXRFeature.NativeEvent.XrInstanceChanged, 41);
        Event(OpenXRFeature.NativeEvent.XrSystemIdChanged, 73);
        Event(OpenXRFeature.NativeEvent.XrSessionChanged, session);
        Event(OpenXRFeature.NativeEvent.XrBeginSession, session);
    }
    public static void Main()
    {
        SteamVrAlpha.Initialize();
        Check(SteamVrAlpha.Tick(true, 0, out _) == SteamVrAlpha.Result.Pending && Queries == 0,
            "observer waits for actual lifecycle handles and never invents an instance");
        Session(101);
        Check(SteamVrAlpha.Tick(true, 1, out _) == SteamVrAlpha.Result.Pending && OpenXRFeature.Requested == 3 && Queries == 2,
            "real Harmony enum/ulong prefix captures live context; native count/data ABI queries exact instance/system");
        OpenXRFeature.Submit();
        Check(SteamVrAlpha.Tick(true, 2, out _) == SteamVrAlpha.Result.Active && SteamVrAlpha.Active,
            "reflection invokes existing Unity blend API and confirms delayed acceptance");
        Event(OpenXRFeature.NativeEvent.XrEndSession, 101);
        Check(!SteamVrAlpha.Active && OpenXRFeature.Requested == 1,
            "session-end prefix restores mode before the original handler retires the session");
        OpenXRFeature.Submit();
        Event(OpenXRFeature.NativeEvent.XrBeginSession, 101);
        SteamVrAlpha.Tick(true, 3, out _); OpenXRFeature.Submit(); SteamVrAlpha.Tick(true, 4, out _);
        Event(OpenXRFeature.NativeEvent.XrLossPending, 101);
        Check(!SteamVrAlpha.Active && OpenXRFeature.Requested == 0, "session loss revokes without writes through invalid handles");
        Event(OpenXRFeature.NativeEvent.XrDestroyInstance, 41);
        OpenXRFeature.Actual = 1; Alpha = false;
        Session(102);
        Check(SteamVrAlpha.Tick(true, 5, out _) == SteamVrAlpha.Result.Unsupported && OpenXRFeature.Requested == 0,
            "new session reruns actual enumeration and refuses an opaque-only system");
        Check(Events == 12, "all observed lifecycle calls still reach the original game callback");
        SteamVrAlpha.Shutdown();
        Alpha = true; Session(103); SteamVrAlpha.Initialize();
        Check(SteamVrAlpha.Tick(true, 6, out _) == SteamVrAlpha.Result.Pending,
            "late installation cannot reuse destroyed handles and requires fresh lifecycle events");
        SteamVrAlpha.Shutdown();
        Console.WriteLine("Native OpenXR ABI and real Harmony lifecycle checks passed: " + _checks);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static unsafe int GetProc(ulong instance, byte* name, IntPtr* function)
    {
        if (instance != 41 || Marshal.PtrToStringAnsi((IntPtr)name) != "xrEnumerateEnvironmentBlendModes") return -12;
        *function = (IntPtr)(delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, IntPtr, int>)&Enumerate;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static unsafe int Enumerate(ulong instance, ulong system, int view, uint capacity, uint* count, IntPtr modes)
    {
        Queries++;
        if (instance != 41 || system != 73 || view != 2) return -12;
        *count = Alpha ? 2u : 1u;
        if (capacity == 0) return modes == IntPtr.Zero ? 0 : -1;
        if (capacity < *count || modes == IntPtr.Zero) return -11;
        Marshal.WriteInt32(modes, 0, 1);
        if (Alpha) Marshal.WriteInt32(modes, 4, 3);
        return 0;
    }
}

namespace UnityEngine.XR.OpenXR.Features
{
    public static class OpenXRFeature
    {
        // Deliberately non-production ordinals: the observer must use names.
        public enum NativeEvent { XrInstanceChanged = 12, XrSystemIdChanged, XrSessionChanged, XrBeginSession,
            XrEndSession, XrDestroySession, XrDestroyInstance, XrLossPending, XrInstanceLossPending }
        public enum BlendMode { Opaque = 1, Additive = 2, AlphaBlend = 3 }
        internal static int Actual = 1, Requested;
        private static unsafe IntPtr xrGetInstanceProcAddr =>
            (IntPtr)(delegate* unmanaged[Stdcall]<ulong, byte*, IntPtr*, int>)&SteamVrAlphaInteropChecks.GetProc;
        private static BlendMode GetEnvironmentBlendMode() => (BlendMode)Actual;
        private static void SetEnvironmentBlendMode(BlendMode mode) => Requested = (int)mode;
        internal static void Submit() { if (Requested != 0) { Actual = Requested; Requested = 0; } }
    }
}
namespace UnityEngine.XR.OpenXR
{
    public static class OpenXRLoaderBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReceiveNativeEvent(OpenXRFeature.NativeEvent e, ulong payload) => SteamVrAlphaInteropChecks.Events++;
    }
}
namespace GreenScreen
{
    internal static class SteamLinkConnectionLifecycle
    {
        internal static Type FindType(string name) => Assembly.GetExecutingAssembly().GetType(name);
    }
    internal static class Utils
    {
        internal static void LogDebug(string text) { }
        internal static void LogInfo(string text) { }
    }
}
