using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PcOnSongBridge
{
    internal enum PlaybackRouteState { Pending, Unselected, Matched, Mismatch, Multiple, Unavailable }

    internal sealed class PlaybackRouteResult
    {
        public readonly PlaybackRouteState State;
        public readonly string Message;
        public readonly string SelectedId;
        public readonly string[] ActiveIds;
        public readonly string[] ActiveNames;

        internal PlaybackRouteResult(PlaybackRouteState state, string message, string selectedId,
            List<string> activeIds, List<string> activeNames)
        {
            State = state;
            Message = message;
            SelectedId = selectedId;
            ActiveIds = activeIds.ToArray();
            ActiveNames = activeNames.ToArray();
        }
    }

    internal static class PlaybackRoute
    {
        private const int QueryFunctionInstanceId = 0x0800 + 17;
        private const int QueryFunctionInstanceIdSize = 0x0800 + 18;
        private const int ActiveSession = 1;
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        // WinMM indexes can change. Query the exact endpoint behind each input or output.
        public static string GetWaveOutEndpointId(int waveOutIndex)
        {
            return GetWaveEndpointId(waveOutIndex, false);
        }

        internal static string GetWaveEndpointId(int waveIndex, bool input)
        {
            if (waveIndex < 0) return null;
            var device = new IntPtr(waveIndex);
            int bytes;
            uint status = input ? QueryInSize(device, QueryFunctionInstanceIdSize, out bytes, IntPtr.Zero)
                : QueryOutSize(device, QueryFunctionInstanceIdSize, out bytes, IntPtr.Zero);
            if (status != 0 || bytes < 4 || bytes % 2 != 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                status = input ? QueryInId(device, QueryFunctionInstanceId, buffer, new IntPtr(bytes))
                    : QueryOutId(device, QueryFunctionInstanceId, buffer, new IntPtr(bytes));
                if (status != 0) return null;
                string id = Marshal.PtrToStringUni(buffer, bytes / 2).TrimEnd('\0');
                return String.IsNullOrEmpty(id) ? null : id;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("winmm.dll", EntryPoint = "waveOutMessage")]
        private static extern uint QueryOutSize(IntPtr device, int message, out int bytes, IntPtr reserved);
        [DllImport("winmm.dll", EntryPoint = "waveOutMessage")]
        private static extern uint QueryOutId(IntPtr device, int message, IntPtr buffer, IntPtr bytes);
        [DllImport("winmm.dll", EntryPoint = "waveInMessage")]
        private static extern uint QueryInSize(IntPtr device, int message, out int bytes, IntPtr reserved);
        [DllImport("winmm.dll", EntryPoint = "waveInMessage")]
        private static extern uint QueryInId(IntPtr device, int message, IntPtr buffer, IntPtr bytes);

        internal static string GetDefaultOutputEndpointId()
        {
            IDeviceEnumerator enumerator = null;
            IDevice device = null;
            try
            {
                enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                enumerator.GetDefaultAudioEndpoint(0, 1, out device); // eRender, eMultimedia
                string id;
                device.GetId(out id);
                return id;
            }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { Release(device); Release(enumerator); }
        }

        internal static string GetEndpointFriendlyName(string endpointId)
        {
            if (String.IsNullOrEmpty(endpointId)) return null;
            IDeviceEnumerator enumerator = null;
            IDevice device = null;
            try
            {
                enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                if (enumerator.GetDevice(endpointId, out device) < 0 || device == null) return null;
                return GetFriendlyName(device);
            }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.IO.IOException) { return null; }
            finally { Release(device); Release(enumerator); }
        }

        public static PlaybackRouteResult Inspect(int processId, int waveOutIndex)
        {
            string id = GetWaveOutEndpointId(waveOutIndex);
            if (waveOutIndex >= 0 && id == null)
                return new PlaybackRouteResult(PlaybackRouteState.Unavailable,
                    "Windows could not identify the selected output. Refresh the playback devices.", null,
                    new List<string>(), new List<string>());
            return Inspect(processId, id);
        }

        // Each call takes a fresh snapshot. Only active, single-process render sessions count.
        public static PlaybackRouteResult Inspect(int processId, string selectedEndpointId)
        {
            var ids = new List<string>();
            var names = new List<string>();
            string selectedName = null;
            IDeviceEnumerator enumerator = null;
            IDeviceCollection devices = null;
            try
            {
                enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
                enumerator.EnumAudioEndpoints(0, 1, out devices); // eRender, DEVICE_STATE_ACTIVE
                uint count;
                devices.GetCount(out count);
                if (count == 0)
                    return new PlaybackRouteResult(PlaybackRouteState.Unavailable, "Windows has no active playback outputs.",
                        selectedEndpointId, ids, names);
                for (uint i = 0; i < count; i++)
                {
                    IDevice endpoint = null;
                    try
                    {
                        devices.Item(i, out endpoint);
                        string id;
                        endpoint.GetId(out id);
                        bool selected = String.Equals(id, selectedEndpointId, StringComparison.OrdinalIgnoreCase);
                        bool active = processId > 0 && HasActiveSession(endpoint, processId);
                        if (selected || active)
                        {
                            string name = GetFriendlyName(endpoint) ?? id;
                            if (selected) selectedName = name;
                            if (active) { ids.Add(id); names.Add(name); }
                        }
                    }
                    finally { Release(endpoint); }
                }
            }
            catch (COMException ex)
            {
                // A hot unplug or service restart can invalidate a snapshot midway. Never declare it matched.
                return new PlaybackRouteResult(PlaybackRouteState.Unavailable,
                    "Windows could not finish checking playback routes (" + ex.ErrorCode.ToString("X8") + "). Try again.",
                    selectedEndpointId, ids, names);
            }
            finally { Release(devices); Release(enumerator); }

            return Describe(selectedEndpointId, selectedName, ids, names);
        }

        internal static PlaybackRouteResult Describe(string selectedId, string selectedName,
            List<string> activeIds, List<string> activeNames)
        {
            string observed = String.Join(", ", activeNames);
            if (activeIds.Count > 1)
                return new PlaybackRouteResult(PlaybackRouteState.Multiple, "scrcpy is playing on multiple outputs (" + observed + ").",
                    selectedId, activeIds, activeNames);
            if (selectedId != null && selectedName == null)
            {
                string actual = activeIds.Count == 1 ? " scrcpy is playing on " + observed + "." : "";
                return new PlaybackRouteResult(PlaybackRouteState.Unavailable,
                    "The selected output is unavailable. Refresh the playback devices." + actual, selectedId, activeIds, activeNames);
            }
            if (activeIds.Count == 0)
                return new PlaybackRouteResult(PlaybackRouteState.Pending, "Waiting for an active scrcpy playback session.",
                    selectedId, activeIds, activeNames);
            if (selectedId == null)
                return new PlaybackRouteResult(PlaybackRouteState.Unselected,
                    "scrcpy is playing on " + observed + ". Select the intended output to compare.", selectedId, activeIds, activeNames);
            if (!String.Equals(activeIds[0], selectedId, StringComparison.OrdinalIgnoreCase))
            {
                if (String.Equals(observed, selectedName, StringComparison.OrdinalIgnoreCase))
                    return new PlaybackRouteResult(PlaybackRouteState.Mismatch,
                        "Two outputs share the name " + observed + ". scrcpy is playing on the other endpoint.",
                        selectedId, activeIds, activeNames);
                return new PlaybackRouteResult(PlaybackRouteState.Mismatch,
                    "scrcpy is playing on " + observed + ". You selected " + selectedName + ".", selectedId, activeIds, activeNames);
            }
            return new PlaybackRouteResult(PlaybackRouteState.Matched,
                "scrcpy is playing on " + observed + ", the selected output.", selectedId, activeIds, activeNames);
        }

        private static bool HasActiveSession(IDevice endpoint, int processId)
        {
            Guid managerId = typeof(ISessionManager).GUID;
            ISessionManager manager = null;
            ISessionEnumerator sessions = null;
            try
            {
                endpoint.Activate(ref managerId, 1, IntPtr.Zero, out manager); // CLSCTX_INPROC_SERVER
                manager.GetSessionEnumerator(out sessions);
                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    ISessionControl session = null;
                    try
                    {
                        sessions.GetSession(i, out session);
                        int state;
                        session.GetState(out state);
                        if (state != ActiveSession) continue;
                        uint pid;
                        int status = ((ISessionControl2)session).GetProcessId(out pid);
                        Marshal.ThrowExceptionForHR(status);
                        if (status == 0 && pid == processId) return true;
                    }
                    finally { Release(session); }
                }
                return false;
            }
            finally { Release(sessions); Release(manager); }
        }

        private static string GetFriendlyName(IDevice endpoint)
        {
            IPropertyStore properties = null;
            PropertyValue value = new PropertyValue();
            try
            {
                endpoint.OpenPropertyStore(0, out properties); // STGM_READ
                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                properties.GetValue(ref key, out value); // PKEY_Device_FriendlyName
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; // VT_LPWSTR
            }
            finally { PropVariantClear(ref value); Release(properties); }
        }

        private static void Release(object instance)
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }

        // PROPVARIANT has an eight-byte tag followed by a union of up to two pointer-sized fields.
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyValue
        {
            public ushort Type, Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer, Second;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropertyValue value);

        // Core Audio vtable prefixes from mmdeviceapi.h and audiopolicy.h.
        // Unused slots retain the native order and are never called.
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDeviceEnumerator
        {
            void EnumAudioEndpoints(int flow, uint mask, out IDeviceCollection devices);
            void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDeviceCollection { void GetCount(out uint count); void Item(uint index, out IDevice device); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDevice
        {
            void Activate(ref Guid iid, uint context, IntPtr parameters, out ISessionManager manager);
            void OpenPropertyStore(uint access, out IPropertyStore store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(); void GetAt();
            void GetValue(ref PropertyKey key, out PropertyValue value);
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISessionManager
        {
            void GetAudioSessionControl(); void GetSimpleAudioVolume();
            void GetSessionEnumerator(out ISessionEnumerator enumerator);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISessionEnumerator
        {
            void GetCount(out int count); void GetSession(int index, out ISessionControl session);
        }
        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISessionControl { void GetState(out int state); }
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISessionControl2
        {
            void GetState(); void GetDisplayName(); void SetDisplayName(); void GetIconPath(); void SetIconPath();
            void GetGroupingParam(); void SetGroupingParam(); void RegisterAudioSessionNotification();
            void UnregisterAudioSessionNotification(); void GetSessionIdentifier(); void GetSessionInstanceIdentifier();
            [PreserveSig] int GetProcessId(out uint pid);
        }
    }
}
