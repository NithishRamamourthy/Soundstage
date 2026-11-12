// Minimal WASAPI interop: just enough to enumerate render endpoints, capture one
// in loopback mode and render to another. No third-party dependencies.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Soundstage.Engine
{
    enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public uint u;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint framesWritten, uint flags);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClientComObject { }

    // Windows has no documented API for changing the default playback device; this is
    // the interface the Sound control panel itself uses. Only SetDefaultEndpoint is
    // called, so the ten methods before it are declared as bare vtable slots.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int Slot0();
        [PreserveSig] int Slot1();
        [PreserveSig] int Slot2();
        [PreserveSig] int Slot3();
        [PreserveSig] int Slot4();
        [PreserveSig] int Slot5();
        [PreserveSig] int Slot6();
        [PreserveSig] int Slot7();
        [PreserveSig] int Slot8();
        [PreserveSig] int Slot9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }

    /// <summary>Parsed WAVEFORMATEX / WAVEFORMATEXTENSIBLE.</summary>
    sealed class WaveFormat
    {
        public int Channels;
        public int SampleRate;
        public int Bits;
        public uint ChannelMask;
        public bool IsFloat;

        static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public static WaveFormat FromPointer(IntPtr p)
        {
            WaveFormat f = new WaveFormat();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = (ushort)Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            f.Bits = (ushort)Marshal.ReadInt16(p, 14);
            int cbSize = (ushort)Marshal.ReadInt16(p, 16);
            if (tag == 0xFFFE && cbSize >= 22)
            {
                f.ChannelMask = (uint)Marshal.ReadInt32(p, 20);
                byte[] g = new byte[16];
                Marshal.Copy(new IntPtr(p.ToInt64() + 24), g, 0, 16);
                f.IsFloat = new Guid(g) == FloatSubFormat;
            }
            else
            {
                f.IsFloat = tag == 3;
            }
            return f;
        }

        public override string ToString()
        {
            return Channels + " ch, " + SampleRate + " Hz, " + Bits + "-bit " + (IsFloat ? "float" : "pcm") +
                   ", mask 0x" + ChannelMask.ToString("X");
        }
    }

    sealed class AudioEndpoint
    {
        public string Id;
        public string Name;
        public IMMDevice Device;
    }

    static class Wasapi
    {
        public const uint DEVICE_STATE_ACTIVE = 1;
        public const uint CLSCTX_ALL = 23;
        public const uint STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint BUFFERFLAGS_SILENT = 0x2;

        public static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        static PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pv);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException(what + " failed: 0x" + hr.ToString("X8"));
        }

        public static List<AudioEndpoint> RenderEndpoints()
        {
            List<AudioEndpoint> list = new List<AudioEndpoint>();
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDeviceCollection col;
            Check(en.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out col), "EnumAudioEndpoints");
            uint count;
            Check(col.GetCount(out count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice dev;
                if (col.Item(i, out dev) < 0) continue;
                AudioEndpoint ep = new AudioEndpoint();
                ep.Device = dev;
                dev.GetId(out ep.Id);
                ep.Name = FriendlyName(dev);
                list.Add(ep);
            }
            return list;
        }

        static string FriendlyName(IMMDevice dev)
        {
            IPropertyStore store;
            if (dev.OpenPropertyStore(0, out store) < 0) return "(unknown)";
            PROPVARIANT pv;
            string name = "(unknown)";
            if (store.GetValue(ref PKEY_Device_FriendlyName, out pv) >= 0)
            {
                if (pv.vt == 31 && pv.p != IntPtr.Zero) name = Marshal.PtrToStringUni(pv.p);
                PropVariantClear(ref pv);
            }
            return name;
        }

        public static AudioEndpoint Find(List<AudioEndpoint> endpoints, string nameContains)
        {
            foreach (AudioEndpoint ep in endpoints)
            {
                if (ep.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0) return ep;
            }
            return null;
        }

        public static IAudioClient Activate(AudioEndpoint ep)
        {
            object o;
            Guid iid = IID_IAudioClient;
            Check(ep.Device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o), "Activate(" + ep.Name + ")");
            return (IAudioClient)o;
        }

        /// <summary>Initializes a shared-mode stream using the endpoint's own mix format.</summary>
        public static WaveFormat InitShared(IAudioClient client, bool loopback, long hnsBuffer)
        {
            IntPtr pFormat;
            Check(client.GetMixFormat(out pFormat), "GetMixFormat");
            try
            {
                WaveFormat fmt = WaveFormat.FromPointer(pFormat);
                Check(client.Initialize(0, loopback ? STREAMFLAGS_LOOPBACK : 0, hnsBuffer, 0, pFormat, IntPtr.Zero),
                      "IAudioClient.Initialize");
                return fmt;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pFormat);
            }
        }

        /// <summary>Id of the device Windows currently plays to, or null.</summary>
        public static string DefaultRenderId()
        {
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) < 0 || dev == null) return null;
            string id;
            dev.GetId(out id);
            return id;
        }

        /// <summary>Makes a device the Windows default for media and system sounds
        /// (calls keep whatever communications device is set).</summary>
        public static bool SetDefaultRender(string id)
        {
            try
            {
                IPolicyConfig config = (IPolicyConfig)new PolicyConfigClientComObject();
                int a = config.SetDefaultEndpoint(id, ERole.eConsole);
                int b = config.SetDefaultEndpoint(id, ERole.eMultimedia);
                Marshal.ReleaseComObject(config);
                return a >= 0 && b >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static PROPERTYKEY PKEY_AudioEndpoint_PhysicalSpeakers = new PROPERTYKEY
        {
            fmtid = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"),
            pid = 3
        };

        /// <summary>Channel mask of the speakers the driver says the device has (0 if unknown).</summary>
        public static uint PhysicalSpeakers(AudioEndpoint ep)
        {
            IPropertyStore store;
            if (ep.Device.OpenPropertyStore(0, out store) < 0) return 0;
            PROPVARIANT pv;
            uint mask = 0;
            if (store.GetValue(ref PKEY_AudioEndpoint_PhysicalSpeakers, out pv) >= 0)
            {
                if (pv.vt == 19) mask = pv.u;
                PropVariantClear(ref pv);
            }
            return mask;
        }

        /// <summary>The most channels (8, 6 or 2) the driver accepts, found by asking it
        /// about concrete 7.1 and 5.1 formats. This is what the device could be set to,
        /// not what it is set to now.</summary>
        public static int MaxChannels(AudioEndpoint ep)
        {
            IAudioClient c;
            try { c = Activate(ep); }
            catch (Exception) { return 0; }
            try
            {
                int[] counts = { 8, 6 };
                uint[] masks = { 0x63F, 0x3F };
                int[] rates = { 48000, 44100, 96000 };
                int[] bits = { 16, 24, 32 };
                for (int i = 0; i < counts.Length; i++)
                    foreach (int rate in rates)
                        foreach (int b in bits)
                            if (Supports(c, counts[i], masks[i], rate, b)) return counts[i];
                return 2;
            }
            finally { Marshal.ReleaseComObject(c); }
        }

        static bool Supports(IAudioClient client, int channels, uint mask, int rate, int bits)
        {
            // WAVEFORMATEXTENSIBLE, integer PCM.
            IntPtr p = Marshal.AllocCoTaskMem(40);
            try
            {
                int blockAlign = channels * bits / 8;
                Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
                Marshal.WriteInt16(p, 2, (short)channels);
                Marshal.WriteInt32(p, 4, rate);
                Marshal.WriteInt32(p, 8, rate * blockAlign);
                Marshal.WriteInt16(p, 12, (short)blockAlign);
                Marshal.WriteInt16(p, 14, (short)bits);
                Marshal.WriteInt16(p, 16, 22);
                Marshal.WriteInt16(p, 18, (short)bits);
                Marshal.WriteInt32(p, 20, unchecked((int)mask));
                byte[] pcm = new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray();
                Marshal.Copy(pcm, 0, new IntPtr(p.ToInt64() + 24), 16);
                IntPtr closest;
                return client.IsFormatSupported(1, p, out closest) == 0;   // exclusive mode: exact answer
            }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        public static WaveFormat MixFormat(AudioEndpoint ep)
        {
            IAudioClient c = Activate(ep);
            IntPtr pFormat;
            Check(c.GetMixFormat(out pFormat), "GetMixFormat");
            try { return WaveFormat.FromPointer(pFormat); }
            finally { Marshal.FreeCoTaskMem(pFormat); Marshal.ReleaseComObject(c); }
        }
    }
}
