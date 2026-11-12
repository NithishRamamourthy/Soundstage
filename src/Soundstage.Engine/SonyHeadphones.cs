// What the control-channel messages mean: reading and changing the headphones'
// own settings (noise cancelling, equalizer, speak-to-chat and so on).
//
// Message layouts follow the protocol tables published by the MIT-licensed
// SonyHeadphonesClient project (see NOTICE.md), checked against a WH-1000XM6.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Soundstage.Engine
{
    public enum NoiseMode { Off, NoiseCancelling, Ambient }

    /// <summary>Everything known about the headphones. Null / -1 means "not reported".</summary>
    public sealed class SonyState
    {
        public string Model, Firmware;
        public int Battery = -1;
        public bool Charging;
        public int Codec = -1;

        public bool NoiseControlSupported;
        public NoiseMode Noise;
        public int AmbientLevel = 10;          // 1..20
        public bool FocusOnVoice;
        public bool AutoAmbientSupported;      // XM6: level follows the surroundings
        public bool AutoAmbient;
        public int AmbientSensitivity;         // 0 standard, 1 high, 2 low

        public bool EqSupported;
        public int EqPreset;
        public int[] EqBands = new int[0];     // raw steps; the centre step is flat
        public int EqCentre = 6;

        public bool? SpeakToChat;
        public int SpeakToChatSensitivity = -1;   // 0 auto, 1 high, 2 low
        public int SpeakToChatTimeout = -1;       // 0 short, 1 standard, 2 long, 3 never
        public bool? Dsee;
        public int AutoPowerOff = -1;             // see SonyHeadphones.AutoPowerOffNames
        /// <summary>The auto power-off choices this model accepts (empty if it did not say).</summary>
        public int[] AutoPowerOffOptions = new int[0];
        public int ConnectionPriority = -1;       // 0 sound quality, 1 stable connection
        public bool? PauseWhenRemoved;
        public int Volume = -1;

        /// <summary>The device's own named on/off settings (touch panel and the like).</summary>
        public List<SonySwitch> Switches = new List<SonySwitch>();

        public SonyState Clone()
        {
            SonyState c = (SonyState)MemberwiseClone();
            c.EqBands = (int[])EqBands.Clone();
            c.AutoPowerOffOptions = (int[])AutoPowerOffOptions.Clone();
            c.Switches = new List<SonySwitch>();
            foreach (SonySwitch s in Switches) c.Switches.Add(new SonySwitch { Slot = s.Slot, Name = s.Name, On = s.On });
            return c;
        }
    }

    public sealed class SonySwitch
    {
        public byte Slot;      // 0xD1..0xD4
        public string Name;
        public bool? On;
    }

    public sealed class BluetoothDevice
    {
        public ulong Address;
        public string Name;
        public bool Connected;
        public override string ToString() { return Name; }
    }

    public sealed class SonyHeadphones : IDisposable
    {
        public static readonly string[] AutoPowerOffNames = { "After 5 minutes", "After 30 minutes", "After 1 hour", "After 3 hours", "After 15 minutes" };
        public const int AutoPowerOffWhenRemoved = 0x10, AutoPowerOffNever = 0x11;

        public static readonly KeyValuePair<int, string>[] EqPresets =
        {
            new KeyValuePair<int, string>(0x00, "Off"),
            new KeyValuePair<int, string>(0x30, "Heavy"),
            new KeyValuePair<int, string>(0x31, "Clear"),
            new KeyValuePair<int, string>(0x32, "Hard"),
            new KeyValuePair<int, string>(0x33, "Soft"),
            new KeyValuePair<int, string>(0xA0, "Custom"),
        };
        public const int EqCustom = 0xA0;

        readonly SonyLink _link = new SonyLink();
        readonly object _lock = new object();
        readonly SonyState _state = new SonyState();
        byte _noiseType, _autoPowerOffType, _connectionType, _volumeType;
        int _autoPowerOffLast;

        sealed class Waiter
        {
            public byte Command, Sub;
            public bool AnySub;
            public byte[] Reply;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }
        readonly List<Waiter> _waiters = new List<Waiter>();

        /// <summary>Something in State changed (raised on a background thread).</summary>
        public event Action Changed;
        /// <summary>The headphones dropped the control channel.</summary>
        public event Action Disconnected;
        /// <summary>Raw traffic, for diagnostics.</summary>
        public event Action<string> Trace;

        public bool IsConnected { get { return _link.IsOpen; } }

        public SonyState State { get { lock (_lock) return _state.Clone(); } }

        public SonyHeadphones()
        {
            _link.Received += OnMessage;
            _link.Closed += delegate { Action h = Disconnected; if (h != null) h(); };
        }

        // --------------------------------------------------------- discovery

        [StructLayout(LayoutKind.Sequential)]
        struct SearchParams
        {
            public int dwSize;
            public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DeviceInfo
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public long stLastSeen0, stLastSeen1, stLastUsed0, stLastUsed1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindDeviceClose(IntPtr find);

        /// <summary>Paired Bluetooth devices, connected ones first.</summary>
        public static List<BluetoothDevice> PairedDevices()
        {
            List<BluetoothDevice> list = new List<BluetoothDevice>();
            SearchParams sp = new SearchParams();
            sp.dwSize = Marshal.SizeOf(typeof(SearchParams));
            sp.fReturnAuthenticated = 1; sp.fReturnRemembered = 1; sp.fReturnConnected = 1;
            DeviceInfo info = new DeviceInfo();
            info.dwSize = Marshal.SizeOf(typeof(DeviceInfo));
            IntPtr find = BluetoothFindFirstDevice(ref sp, ref info);
            if (find == IntPtr.Zero) return list;
            try
            {
                do
                {
                    list.Add(new BluetoothDevice { Address = info.Address, Name = info.szName, Connected = info.fConnected != 0 });
                }
                while (BluetoothFindNextDevice(find, ref info));
            }
            finally { BluetoothFindDeviceClose(find); }
            list.Sort(delegate(BluetoothDevice a, BluetoothDevice b) { return b.Connected.CompareTo(a.Connected); });
            return list;
        }

        // --------------------------------------------------------- connecting

        /// <summary>Opens the control channel and reads every setting. Null on success.</summary>
        public string Connect(ulong address)
        {
            string error = _link.Open(address, SonyLink.ServiceV2);
            if (error != null) return error;

            byte[] info = Query(0x00, 0x00, 0x01, 1500, true) ?? Query(0x00, 0x00, 0x01, 1500, true);
            if (info == null) { _link.Dispose(); return "The headphones did not answer on the control channel."; }
            if (info.Length < 8) { _link.Dispose(); return "This model uses an older control protocol that is not supported yet."; }

            Query(0x04, 0x01, 0x05);   // model
            Query(0x04, 0x02, 0x05);   // firmware
            Refresh();
            return null;
        }

        /// <summary>Re-reads every setting from the headphones.</summary>
        public void Refresh()
        {
            Query(0x22, 0x00, 0x23);   // battery
            Query(0x12, 0x02, 0x13);   // codec

            // Noise control comes in several layouts; ask newest first and keep the one that answers.
            if (_noiseType == 0)
            {
                foreach (byte type in new byte[] { 0x19, 0x17, 0x15 })
                {
                    byte[] r = Query(0x66, type, 0x67, 500);
                    if (r != null && r.Length >= 7) { _noiseType = type; break; }
                }
            }
            else Query(0x66, _noiseType, 0x67);

            Query(0x56, 0x00, 0x57);   // equalizer
            Query(0xF6, 0x0C, 0xF7);   // speak-to-chat
            Query(0xFA, 0x0C, 0xFB);   // ... sensitivity and timeout
            Query(0xF6, 0x01, 0xF7, 500);   // pause when taken off
            Query(0xE6, 0x01, 0xE7);   // DSEE

            if (_autoPowerOffType == 0)
            {
                foreach (byte type in new byte[] { 0x05, 0x04 })
                    if (Query(0x26, type, 0x27, 500) != null) { _autoPowerOffType = type; break; }
            }
            else Query(0x26, _autoPowerOffType, 0x27);
            // Models differ in which choices they accept (the XM6 only has two), so ask.
            bool needOptions;
            lock (_lock) needOptions = _state.AutoPowerOffOptions.Length == 0;
            if (_autoPowerOffType != 0 && needOptions) Query(0x20, _autoPowerOffType, 0x21, 500);

            if (!_connectionProbed)
            {
                _connectionProbed = true;
                foreach (byte type in new byte[] { 0x02, 0x05, 0x00 })
                    if (Query(0xE6, type, 0xE7, 500) != null) { _connectionType = type; _connectionKnown = true; break; }
            }
            else if (_connectionKnown) Query(0xE6, _connectionType, 0xE7);

            if (_volumeType == 0)
            {
                foreach (byte type in new byte[] { 0x20, 0x30 })
                    if (Query(0xA6, type, 0xA7, 500) != null) { _volumeType = type; break; }
            }
            else Query(0xA6, _volumeType, 0xA7);

            // The device's own named switches (touch panel etc.).
            for (byte slot = 0xD1; slot <= 0xD4; slot++)
            {
                bool known;
                lock (_lock) known = FindSwitch(slot) != null;
                if (!known && Query(0xD0, slot, 0xD1, 500) == null) continue;
                Query(0xD6, slot, 0xD7, 500);
            }
        }
        bool _connectionKnown, _connectionProbed;

        /// <summary>The first connected paired device that looks like Sony headphones, or null.</summary>
        public static BluetoothDevice FindHeadphones()
        {
            foreach (BluetoothDevice d in PairedDevices())
            {
                if (!d.Connected || d.Name == null) continue;
                string n = d.Name.ToUpperInvariant();
                if (n.StartsWith("WH-") || n.StartsWith("WF-") || n.StartsWith("WI-") || n.Contains("LINKBUDS") || n.Contains("ULT WEAR"))
                    return d;
            }
            return null;
        }

        public void RefreshBattery()
        {
            Query(0x22, 0x00, 0x23);
        }

        // ------------------------------------------------------------ setters

        public bool SetNoise(NoiseMode mode, int ambientLevel, bool focusOnVoice, bool autoAmbient)
        {
            byte type = _noiseType;
            if (type == 0) return false;
            int sensitivity;
            lock (_lock) sensitivity = _state.AmbientSensitivity;
            byte level = (byte)Math.Max(1, Math.Min(20, ambientLevel));
            byte on = (byte)(mode == NoiseMode.Off ? 0 : 1);
            byte asm = (byte)(mode == NoiseMode.Ambient ? 1 : 0);
            byte voice = (byte)(focusOnVoice ? 1 : 0);
            byte[] payload = type == 0x19
                ? new byte[] { 0x68, type, 0x01, on, asm, voice, level, (byte)(autoAmbient ? 1 : 0), (byte)sensitivity }
                : new byte[] { 0x68, type, 0x01, on, asm, voice, level };
            return Send(payload);
        }

        public bool SetEqPreset(int preset)
        {
            return Send(0x58, 0x00, (byte)preset, 0x00);
        }

        /// <summary>Sets the custom equalizer; values are raw steps, EqCentre is flat.</summary>
        public bool SetEqBands(int[] steps)
        {
            byte[] payload = new byte[4 + steps.Length];
            payload[0] = 0x58; payload[1] = 0x00; payload[2] = EqCustom; payload[3] = (byte)steps.Length;
            for (int i = 0; i < steps.Length; i++) payload[4 + i] = (byte)Math.Max(0, steps[i]);
            return Send(payload);
        }

        // On/off settings use 0 for on and 1 for off.
        public bool SetSpeakToChat(bool on) { return Send(0xF8, 0x0C, (byte)(on ? 0 : 1), 0x01); }
        public bool SetSpeakToChatOptions(int sensitivity, int timeout) { return Send(0xFC, 0x0C, (byte)sensitivity, (byte)timeout); }
        public bool SetPauseWhenRemoved(bool on) { return Send(0xF8, 0x01, (byte)(on ? 0 : 1)); }
        public bool SetDsee(bool on) { return Send(0xE8, 0x01, (byte)(on ? 1 : 0)); }
        public bool SetSwitch(byte slot, bool on) { return Send(0xD8, slot, 0x00, (byte)(on ? 0 : 1)); }

        public bool SetAutoPowerOff(int code)
        {
            if (_autoPowerOffType == 0) return false;
            int last = code < 0x10 ? code : _autoPowerOffLast;
            return Send(0x28, _autoPowerOffType, (byte)code, (byte)last);
        }

        public bool SetConnectionPriority(int priority)
        {
            if (!_connectionKnown) return false;
            return Send(0xE8, _connectionType, (byte)priority);
        }

        public bool SetVolume(int volume)
        {
            if (_volumeType == 0) return false;
            return Send(0xA8, _volumeType, (byte)Math.Max(0, Math.Min(30, volume)));
        }

        public bool PowerOff() { return Send(0x24, 0x03, 0x01); }

        bool Send(params byte[] payload)
        {
            Say("-> " + SonyLink.Hex(payload));
            return _link.Send(SonyLink.TypeCommand1, payload);
        }

        byte[] Query(byte command, byte sub, byte replyCommand, int timeoutMs = 1200, bool anySub = false)
        {
            if (!_link.IsOpen) return null;
            Waiter w = new Waiter { Command = replyCommand, Sub = sub, AnySub = anySub };
            lock (_waiters) _waiters.Add(w);
            try
            {
                Say("-> " + SonyLink.Hex(new byte[] { command, sub }));
                _link.Send(SonyLink.TypeCommand1, command, sub);
                w.Done.WaitOne(timeoutMs);
                return w.Reply;
            }
            finally { lock (_waiters) _waiters.Remove(w); }
        }

        void Say(string text)
        {
            Action<string> h = Trace;
            if (h != null) h(text);
        }

        // ------------------------------------------------------------ parsing

        SonySwitch FindSwitch(byte slot)
        {
            foreach (SonySwitch s in _state.Switches) if (s.Slot == slot) return s;
            return null;
        }

        void OnMessage(SonyMessage m)
        {
            byte[] p = m.Payload;
            if (p.Length == 0) return;
            Say("<- " + SonyLink.Hex(p));
            if (m.Type != SonyLink.TypeCommand1) return;

            bool changed;
            lock (_lock) changed = Apply(p);

            lock (_waiters)
            {
                foreach (Waiter w in _waiters)
                {
                    if (w.Reply == null && w.Command == p[0] && (w.AnySub || (p.Length > 1 && p[1] == w.Sub)))
                    {
                        w.Reply = p;
                        w.Done.Set();
                        break;
                    }
                }
            }
            if (changed)
            {
                Action h = Changed;
                if (h != null) h();
            }
        }

        // Replies (odd command, low nibble 1/3/7/B) and notifications (5/9/D) share layouts.
        bool Apply(byte[] p)
        {
            byte sub = p.Length > 1 ? p[1] : (byte)0;
            switch (p[0])
            {
                case 0x05:   // device info
                    if (p.Length < 3 || p.Length < 3 + p[2]) return false;
                    string text = Encoding.ASCII.GetString(p, 3, p[2]);
                    if (sub == 0x01) _state.Model = text; else if (sub == 0x02) _state.Firmware = text;
                    return true;

                case 0x23: case 0x25:   // battery
                    if (sub != 0x00 || p.Length < 4) return false;
                    _state.Battery = p[2];
                    _state.Charging = p[3] == 1;
                    return true;

                case 0x13: case 0x15:   // codec
                    if (sub != 0x02 || p.Length < 3) return false;
                    _state.Codec = p[2];
                    return true;

                case 0x67: case 0x69:   // noise control: status, on/off, mode, voice, level[, auto, sensitivity]
                    if (p.Length < 7 || (sub != 0x19 && sub != 0x17 && sub != 0x15)) return false;
                    if (_noiseType != 0 && sub != _noiseType) return false;
                    _state.NoiseControlSupported = true;
                    _state.Noise = p[3] == 0 ? NoiseMode.Off : (p[4] == 1 ? NoiseMode.Ambient : NoiseMode.NoiseCancelling);
                    _state.FocusOnVoice = p[5] == 1;
                    if (p[6] >= 1 && p[6] <= 20) _state.AmbientLevel = p[6];
                    if (sub == 0x19 && p.Length >= 9)
                    {
                        _state.AutoAmbientSupported = true;
                        _state.AutoAmbient = p[7] == 1;
                        _state.AmbientSensitivity = p[8];
                    }
                    return true;

                case 0x57: case 0x59:   // equalizer: preset, band count, bands
                    if (sub != 0x00 || p.Length < 4 || p.Length < 4 + p[3]) return false;
                    _state.EqSupported = true;
                    _state.EqPreset = p[2];
                    int[] bands = new int[p[3]];
                    for (int i = 0; i < bands.Length; i++) bands[i] = p[4 + i];
                    _state.EqBands = bands;
                    // Ten-band models use steps 0..12; the older five-band-plus-bass layout 0..20.
                    _state.EqCentre = bands.Length == 6 ? 10 : 6;
                    return true;

                case 0xF7: case 0xF9:   // system switches (0 = on)
                    if (p.Length < 3) return false;
                    if (sub == 0x0C) _state.SpeakToChat = p[2] == 0;
                    else if (sub == 0x01) _state.PauseWhenRemoved = p[2] == 0;
                    else return false;
                    return true;

                case 0xFB: case 0xFD:   // speak-to-chat options
                    if (sub != 0x0C || p.Length < 4) return false;
                    _state.SpeakToChatSensitivity = p[2];
                    _state.SpeakToChatTimeout = p[3];
                    return true;

                case 0x21:   // auto power off capability: count, then the accepted choices
                    if ((sub != 0x04 && sub != 0x05) || p.Length < 3 || p.Length < 3 + p[2]) return false;
                    int[] options = new int[p[2]];
                    for (int i = 0; i < options.Length; i++) options[i] = p[3 + i];
                    _state.AutoPowerOffOptions = options;
                    return true;

                case 0x27: case 0x29:   // auto power off: current, last timed choice
                    if ((sub != 0x04 && sub != 0x05) || p.Length < 4) return false;
                    _state.AutoPowerOff = p[2];
                    _autoPowerOffLast = p[3];
                    return true;

                case 0xE7: case 0xE9:   // audio
                    if (p.Length < 3) return false;
                    if (sub == 0x01) _state.Dsee = p[2] == 1;
                    else if (sub == 0x00 || sub == 0x02 || sub == 0x05) _state.ConnectionPriority = p[2];
                    else return false;
                    return true;

                case 0xA7: case 0xA9:   // volume
                    if (p.Length < 3 || (sub != 0x20 && sub != 0x30)) return false;
                    _state.Volume = p[2];
                    return true;

                case 0xD1:   // a named switch: type, string format, then length-prefixed name
                    if (sub < 0xD1 || sub > 0xD4 || p.Length < 5 || p[2] != 0x00) return false;
                    int n = p[4];
                    if (p.Length < 5 + n) return false;
                    SonySwitch created = FindSwitch(sub);
                    if (created == null) { created = new SonySwitch { Slot = sub }; _state.Switches.Add(created); }
                    created.Name = Encoding.UTF8.GetString(p, 5, n);
                    return true;

                case 0xD7: case 0xD9:   // switch value (0 = on)
                    if (p.Length < 4 || p[2] != 0x00) return false;
                    SonySwitch sw = FindSwitch(sub);
                    if (sw == null) return false;
                    sw.On = p[3] == 0;
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            _link.Dispose();
        }
    }
}
