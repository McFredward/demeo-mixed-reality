#nullable disable
using System;
using System.Runtime.InteropServices;

namespace GreenScreen
{
    // This is our game-local OpenComposite extension, not an unversioned query
    // into another OpenVR implementation. It exposes the bridge's *real* live
    // OpenXR session and xrEndFrame result without inventing Unity XR handles.
    internal static class OpenCompositeAlpha
    {
        internal enum Transport { NotInstalled, Legacy, Pending, SteamVR, OtherRuntime }
        private const uint Native = 1, Running = 2, SupportsAlpha = 4, SteamVR = 8, Legacy = 32, SystemKnown = 64;
        private static Backend _backend;

        [StructLayout(LayoutKind.Sequential)]
        internal struct State
        {
            internal uint Version, Bytes;
            internal ulong Generation;
            internal uint Flags, Mode;
            internal bool Valid => Version == 1 && Bytes == 24 && Generation > 0 && (Mode == 1 || Mode == 3)
                && ((Flags & (Native | Legacy)) == Native || (Flags & (Native | Legacy)) == Legacy);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetState(uint bytes, out State state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetMode(ulong generation, uint mode);

        internal static Transport Probe()
        {
            var api = Bind();
            if (api == null) return Transport.NotInstalled;
            State state = api.Read();
            if ((state.Flags & Legacy) != 0) return Transport.Legacy;
            if ((state.Flags & SystemKnown) == 0) return Transport.Pending;
            return (state.Flags & SteamVR) != 0 ? Transport.SteamVR : Transport.OtherRuntime;
        }

        internal static SteamVrAlpha.IBackend TryOpen()
        {
            var api = Bind();
            return api != null && (api.Read().Flags & Native) != 0 ? api : null;
        }

        private static Backend Bind()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IntPtr.Size != 8) return null;
            IntPtr module = GetModuleHandle("openvr_api.dll");
            if (module == IntPtr.Zero) { _backend = null; return null; }
            if (_backend != null && _backend.Module == module) return _backend;
            IntPtr get = GetProcAddress(module, "DMR_GetState"), set = GetProcAddress(module, "DMR_SetBlendMode");
            if (get == IntPtr.Zero || set == IntPtr.Zero) return null;
            // The managed controller can outlive XRSDKOpenVR's final release.
            // Keep only our recognized control module mapped for this process;
            // VR_ShutdownInternal still tears its actual XR session down.
            if (!GetModuleHandleEx(1, "openvr_api.dll", out IntPtr pinned) || pinned != module) return null;
            var api = new Backend(module, get, set);
            api.Read(); // Refuse incompatible schemas before exposing the backend.
            return _backend = api;
        }

        private sealed class Backend : SteamVrAlpha.IBackend
        {
            internal readonly IntPtr Module;
            private readonly GetState _get;
            private readonly SetMode _set;
            private ulong _generation;
            internal Backend(IntPtr module, IntPtr get, IntPtr set)
            {
                Module = module;
                _get = Marshal.GetDelegateForFunctionPointer<GetState>(get);
                _set = Marshal.GetDelegateForFunctionPointer<SetMode>(set);
            }
            internal State Read()
            {
                if (GetModuleHandle("openvr_api.dll") != Module || _get(24, out State state) != 1 || !state.Valid)
                    throw new NotSupportedException("The game-local OpenComposite alpha protocol is unavailable or changed.");
                return state;
            }
            public long Generation { get { _generation = Read().Generation; return unchecked((long)_generation); } }
            public bool Running => (Read().Flags & (Native | OpenCompositeAlpha.Running)) == (Native | OpenCompositeAlpha.Running);
            public bool SupportsAlpha() => (Read().Flags & (SteamVR | OpenCompositeAlpha.SupportsAlpha)) == (SteamVR | OpenCompositeAlpha.SupportsAlpha);
            public int Mode
            {
                get => (int)Read().Mode;
                set
                {
                    if (_set(_generation, checked((uint)value)) != 1)
                        throw new InvalidOperationException("OpenComposite refused a stale session or unsupported blend mode.");
                }
            }
        }
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetModuleHandleEx(uint flags, string name, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
