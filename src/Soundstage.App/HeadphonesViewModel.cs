using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Soundstage.Engine;

namespace Soundstage.UI;

public sealed record Choice(int Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One equalizer slider.</summary>
public sealed class EqBand(HeadphonesViewModel owner, string label) : INotifyPropertyChanged
{
    double _value;
    public string Label { get; } = label;

    public double Value
    {
        get => _value;
        set
        {
            value = Math.Round(value);
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            owner.BandMoved();
        }
    }

    /// <summary>Updates the slider without sending anything to the headphones.</summary>
    public void Show(double value)
    {
        if (_value == value) return;
        _value = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A named on/off setting the headphones describe themselves (touch panel etc.).</summary>
public sealed class DeviceSwitch(HeadphonesViewModel owner, byte slot, string name) : INotifyPropertyChanged
{
    bool _on;
    public byte Slot { get; } = slot;
    public string Name { get; } = name;

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(On)));
            owner.SwitchToggled(this);
        }
    }

    public void Show(bool on)
    {
        if (_on == on) return;
        _on = on;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(On)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The Headphones page: mirrors the headphones' settings and sends changes back.</summary>
public sealed class HeadphonesViewModel : INotifyPropertyChanged
{
    static readonly string[] TenBandLabels = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    static readonly string[] SixBandLabels = { "Bass", "400", "1k", "2.5k", "6.3k", "16k" };

    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly BlockingCollection<Action> _work = new();
    readonly DispatcherTimer _retry, _bandTimer, _levelTimer, _volumeTimer;
    SonyHeadphones? _device;
    bool _showing;       // true while copying device state into the page (no echo back)
    bool _connecting;
    int _ticks;

    public HeadphonesViewModel()
    {
        // All Bluetooth traffic happens on one background thread, in order.
        var worker = new Thread(() => { foreach (var job in _work.GetConsumingEnumerable()) { try { job(); } catch (Exception) { } } })
        {
            IsBackground = true,
            Name = "headphones",
        };
        worker.Start();

        ConnectCommand = new RelayCommand(TryConnect, () => !IsConnected && !_connecting);
        PowerOffCommand = new RelayCommand(() => Do(d => d.PowerOff()), () => IsConnected);

        _bandTimer = Debounce(150, SendBands);
        _levelTimer = Debounce(150, SendNoise);
        _volumeTimer = Debounce(150, () => { int v = (int)_volume; Do(d => d.SetVolume(v)); });

        // Reconnect when the headphones come back, and keep the battery reading fresh.
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _retry.Tick += (_, _) =>
        {
            if (!IsConnected) TryConnect();
            else if (++_ticks % 12 == 0) Do(d => d.RefreshBattery());
        };
        _retry.Start();
        TryConnect();
    }

    DispatcherTimer Debounce(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        return timer;
    }

    static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    // ------------------------------------------------------------ connection

    bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (Set(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    string _statusText = "Looking for headphones…";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Snapshot previews show sample values and must not talk to hardware.</summary>
    bool _preview;

    void TryConnect()
    {
        if (_preview || _connecting || IsConnected) return;
        _connecting = true;
        _work.Add(() =>
        {
            string? status = null;
            SonyHeadphones? device = null;
            try
            {
                var found = SonyHeadphones.FindHeadphones();
                if (found == null) status = "Connect your Sony headphones to this PC over Bluetooth.";
                else
                {
                    device = new SonyHeadphones();
                    string? error = device.Connect(found.Address);
                    if (error != null) { device.Dispose(); device = null; status = found.Name + ": " + error; }
                }
            }
            catch (Exception ex) { status = ex.Message; device = null; }

            _ui.BeginInvoke(() =>
            {
                _connecting = false;
                if (_preview) { device?.Dispose(); return; }
                if (device == null) { StatusText = status ?? "Not connected."; CommandManager.InvalidateRequerySuggested(); return; }
                _device = device;
                device.Changed += () => _ui.BeginInvoke(ShowState);
                device.Disconnected += () => _ui.BeginInvoke(() => OnDisconnected(device));
                IsConnected = true;
                StatusText = "Connected";
                ShowState();
            });
        });
    }

    void OnDisconnected(SonyHeadphones device)
    {
        if (_device != device) return;
        _device = null;
        device.Dispose();
        IsConnected = false;
        StatusText = "Headphones disconnected.";
    }

    /// <summary>Queues a command for the headphones.</summary>
    void Do(Action<SonyHeadphones> action)
    {
        var device = _device;
        if (device == null || _showing) return;
        _work.Add(() => action(device));
    }

    public void Shutdown()
    {
        _retry.Stop();
        _work.CompleteAdding();
        _device?.Dispose();
    }

    // ------------------------------------------------------- state -> page

    void ShowState()
    {
        var device = _device;
        if (device != null) Show(device.State);
    }

    /// <summary>Fills the page with sample values (used for layout snapshots only).</summary>
    internal void ShowPreview()
    {
        _preview = true;
        _retry.Stop();
        IsConnected = true;
        StatusText = "Connected";
        var s = new SonyState
        {
            Model = "WH-1000XM6", Firmware = "3.1.5", Battery = 94, Codec = 0x10,
            NoiseControlSupported = true, Noise = NoiseMode.Ambient, AmbientLevel = 15, AutoAmbientSupported = true,
            EqSupported = true, EqPreset = 0xA0, EqBands = new[] { 8, 7, 6, 5, 6, 6, 7, 8, 9, 7 }, EqCentre = 6,
            SpeakToChat = false, Dsee = true, PauseWhenRemoved = true, AutoPowerOff = 0x10, ConnectionPriority = 0, Volume = 14,
        };
        s.Switches.Add(new SonySwitch { Slot = 0xD1, Name = "TOUCH_PANEL", On = true });
        Show(s);
    }

    void Show(SonyState s)
    {
        _showing = true;
        try
        {
            Model = s.Model ?? "Sony headphones";
            Firmware = s.Firmware == null ? "" : "Firmware " + s.Firmware;
            Battery = Math.Max(0, s.Battery);
            BatteryText = s.Battery < 0 ? "" : s.Battery + "%" + (s.Charging ? " · charging" : "");
            CodecText = s.Codec switch { 0x01 => "SBC", 0x02 => "AAC", 0x10 => "LDAC", 0x20 => "aptX", 0x21 => "aptX HD", 0x30 => "LC3", _ => "" };

            NoiseSupported = s.NoiseControlSupported;
            _mode = s.Noise;
            NoiseCancelling = s.Noise == NoiseMode.NoiseCancelling;
            Ambient = s.Noise == NoiseMode.Ambient;
            NoiseOff = s.Noise == NoiseMode.Off;
            AmbientLevel = s.AmbientLevel;
            FocusOnVoice = s.FocusOnVoice;
            AutoAmbientSupported = s.AutoAmbientSupported;
            AutoAmbient = s.AutoAmbient;

            EqSupported = s.EqSupported;
            if (Bands.Count != s.EqBands.Length)
            {
                Bands.Clear();
                string[] labels = s.EqBands.Length == 10 ? TenBandLabels : s.EqBands.Length == 6 ? SixBandLabels : new string[s.EqBands.Length];
                for (int i = 0; i < s.EqBands.Length; i++) Bands.Add(new EqBand(this, labels[i] ?? (i + 1).ToString()));
                EqMaximum = s.EqCentre * 2;
            }
            _eqCentre = s.EqCentre;
            for (int i = 0; i < Bands.Count; i++) Bands[i].Show(s.EqBands[i]);
            if (!EqPresets.Any(p => p.Value == s.EqPreset)) EqPresets.Add(new Choice(s.EqPreset, $"Preset 0x{s.EqPreset:X2}"));
            EqPreset = EqPresets.First(p => p.Value == s.EqPreset);

            SpeakToChatSupported = s.SpeakToChat.HasValue;
            SpeakToChat = s.SpeakToChat ?? false;
            DseeSupported = s.Dsee.HasValue;
            Dsee = s.Dsee ?? false;
            PauseSupported = s.PauseWhenRemoved.HasValue;
            PauseWhenRemoved = s.PauseWhenRemoved ?? false;

            PrioritySupported = s.ConnectionPriority >= 0;
            Priority = Priorities.FirstOrDefault(p => p.Value == s.ConnectionPriority);
            AutoPowerOffSupported = s.AutoPowerOff >= 0;
            // Offer what the headphones say they accept, plus whatever is set right now.
            var offered = AllAutoPowerOffChoices
                .Where(c => s.AutoPowerOffOptions.Length == 0 || s.AutoPowerOffOptions.Contains(c.Value) || c.Value == s.AutoPowerOff)
                .ToList();
            if (!offered.SequenceEqual(AutoPowerOffChoices))
            {
                AutoPowerOffChoices.Clear();
                foreach (var c in offered) AutoPowerOffChoices.Add(c);
            }
            AutoPowerOff = AutoPowerOffChoices.FirstOrDefault(p => p.Value == s.AutoPowerOff);

            VolumeSupported = s.Volume >= 0;
            if (s.Volume >= 0) Volume = s.Volume;

            foreach (var sw in s.Switches)
            {
                if (sw.Name == null || sw.On == null) continue;
                var row = Switches.FirstOrDefault(x => x.Slot == sw.Slot);
                if (row == null) { row = new DeviceSwitch(this, sw.Slot, Friendly(sw.Name)); Switches.Add(row); }
                row.Show(sw.On.Value);
            }
        }
        finally { _showing = false; }
    }

    // The headphones report internal names such as "TOUCH_PANEL"; make them readable.
    static string Friendly(string raw)
    {
        switch (raw)
        {
            case "MULTIPOINT_SETTING": return "Connect to 2 devices at once";
            case "TOUCH_PANEL_SETTING": return "Touch sensor control panel";
            case "SIDETONE_SETTING": return "Hear your own voice on calls";
        }
        if (raw.EndsWith("_SETTING")) raw = raw[..^8];
        string text = raw.Replace('_', ' ').Trim().ToLowerInvariant();
        return text.Length == 0 ? raw : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ------------------------------------------------------------- about

    string _model = "Headphones", _firmware = "", _batteryText = "", _codecText = "";
    double _battery;
    public string Model { get => _model; private set => Set(ref _model, value); }
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }
    public string CodecText { get => _codecText; private set => Set(ref _codecText, value); }
    public double Battery { get => _battery; private set => Set(ref _battery, value); }

    // ------------------------------------------------------ noise control

    bool _noiseSupported, _nc, _ambient, _noiseOff, _focusOnVoice, _autoAmbientSupported, _autoAmbient;
    double _ambientLevel = 10;

    public bool NoiseSupported { get => _noiseSupported; private set => Set(ref _noiseSupported, value); }
    public bool AutoAmbientSupported { get => _autoAmbientSupported; private set => Set(ref _autoAmbientSupported, value); }

    NoiseMode _mode;
    public bool NoiseCancelling { get => _nc; set { if (Set(ref _nc, value) && value) { _mode = NoiseMode.NoiseCancelling; SendNoise(); } } }
    public bool Ambient
    {
        get => _ambient;
        set { if (Set(ref _ambient, value)) { Notify(nameof(AmbientLevelEnabled)); if (value) { _mode = NoiseMode.Ambient; SendNoise(); } } }
    }
    public bool NoiseOff { get => _noiseOff; set { if (Set(ref _noiseOff, value) && value) { _mode = NoiseMode.Off; SendNoise(); } } }

    public double AmbientLevel
    {
        get => _ambientLevel;
        set { if (Set(ref _ambientLevel, Math.Round(value))) { Notify(nameof(AmbientLevelText)); if (!_showing) Restart(_levelTimer); } }
    }
    public string AmbientLevelText => $"{_ambientLevel:0}";
    public bool AmbientLevelEnabled => _ambient && !_autoAmbient;

    public bool FocusOnVoice { get => _focusOnVoice; set { if (Set(ref _focusOnVoice, value)) SendNoise(); } }
    public bool AutoAmbient
    {
        get => _autoAmbient;
        set { if (Set(ref _autoAmbient, value)) { Notify(nameof(AmbientLevelEnabled)); SendNoise(); } }
    }

    /// <summary>Selects a noise mode from outside the page (tray menu, shortcuts).</summary>
    public void SetNoiseMode(NoiseMode mode)
    {
        if (!IsConnected || !NoiseSupported) return;
        if (mode != NoiseMode.NoiseCancelling) NoiseCancelling = false;
        if (mode != NoiseMode.Ambient) Ambient = false;
        if (mode != NoiseMode.Off) NoiseOff = false;
        if (mode == NoiseMode.NoiseCancelling) NoiseCancelling = true;
        else if (mode == NoiseMode.Ambient) Ambient = true;
        else NoiseOff = true;
    }

    /// <summary>Switches between noise cancelling and ambient sound.</summary>
    public void ToggleNoiseMode() =>
        SetNoiseMode(_mode == NoiseMode.NoiseCancelling ? NoiseMode.Ambient : NoiseMode.NoiseCancelling);

    void SendNoise()
    {
        if (_showing) return;
        NoiseMode mode = _mode;
        int level = (int)_ambientLevel;
        bool voice = _focusOnVoice, auto = _autoAmbient;
        Do(d => d.SetNoise(mode, level, voice, auto));
    }

    // ---------------------------------------------------------- equalizer

    public ObservableCollection<EqBand> Bands { get; } = new();
    public ObservableCollection<Choice> EqPresets { get; } =
        new(SonyHeadphones.EqPresets.Select(p => new Choice(p.Key, p.Value)));

    bool _eqSupported;
    int _eqCentre = 6;
    double _eqMaximum = 12;
    Choice? _eqPreset;

    public bool EqSupported { get => _eqSupported; private set => Set(ref _eqSupported, value); }
    public double EqMaximum { get => _eqMaximum; private set => Set(ref _eqMaximum, value); }

    public Choice? EqPreset
    {
        get => _eqPreset;
        set
        {
            if (!Set(ref _eqPreset, value) || value == null) return;
            int preset = value.Value;
            Do(d => d.SetEqPreset(preset));
        }
    }

    internal void BandMoved()
    {
        if (!_showing) Restart(_bandTimer);
    }

    void SendBands()
    {
        int[] steps = Bands.Select(b => (int)b.Value).ToArray();
        if (steps.Length == 0) return;
        Do(d => d.SetEqBands(steps));
    }

    // ----------------------------------------------------------- features

    bool _stcSupported, _stc, _dseeSupported, _dsee, _pauseSupported, _pause, _prioritySupported, _apoSupported, _volumeSupported;
    double _volume;
    Choice? _priority, _autoPowerOff;

    public bool SpeakToChatSupported { get => _stcSupported; private set => Set(ref _stcSupported, value); }
    public bool SpeakToChat { get => _stc; set { if (Set(ref _stc, value)) Do(d => d.SetSpeakToChat(value)); } }

    public bool DseeSupported { get => _dseeSupported; private set => Set(ref _dseeSupported, value); }
    public bool Dsee { get => _dsee; set { if (Set(ref _dsee, value)) Do(d => d.SetDsee(value)); } }

    public bool PauseSupported { get => _pauseSupported; private set => Set(ref _pauseSupported, value); }
    public bool PauseWhenRemoved { get => _pause; set { if (Set(ref _pause, value)) Do(d => d.SetPauseWhenRemoved(value)); } }

    public Choice[] Priorities { get; } = { new(0, "Sound quality"), new(1, "Stable connection") };
    public bool PrioritySupported { get => _prioritySupported; private set => Set(ref _prioritySupported, value); }
    public Choice? Priority
    {
        get => _priority;
        set { if (Set(ref _priority, value) && value != null) { int v = value.Value; Do(d => d.SetConnectionPriority(v)); } }
    }

    static readonly Choice[] AllAutoPowerOffChoices =
    {
        new(0x00, "After 5 minutes"), new(0x04, "After 15 minutes"), new(0x01, "After 30 minutes"),
        new(0x02, "After 1 hour"), new(0x03, "After 3 hours"),
        new(SonyHeadphones.AutoPowerOffWhenRemoved, "When taken off"), new(SonyHeadphones.AutoPowerOffNever, "Never"),
    };
    /// <summary>Only the choices this model accepts.</summary>
    public ObservableCollection<Choice> AutoPowerOffChoices { get; } = new(AllAutoPowerOffChoices);
    public bool AutoPowerOffSupported { get => _apoSupported; private set => Set(ref _apoSupported, value); }
    public Choice? AutoPowerOff
    {
        get => _autoPowerOff;
        set { if (Set(ref _autoPowerOff, value) && value != null) { int v = value.Value; Do(d => d.SetAutoPowerOff(v)); } }
    }

    public bool VolumeSupported { get => _volumeSupported; private set => Set(ref _volumeSupported, value); }
    public double Volume
    {
        get => _volume;
        set { if (Set(ref _volume, Math.Round(value))) { Notify(nameof(VolumeText)); if (!_showing) Restart(_volumeTimer); } }
    }
    public string VolumeText => $"{_volume:0}";

    public ObservableCollection<DeviceSwitch> Switches { get; } = new();

    internal void SwitchToggled(DeviceSwitch sw)
    {
        byte slot = sw.Slot;
        bool on = sw.On;
        Do(d => d.SetSwitch(slot, on));
    }

    public ICommand ConnectCommand { get; }
    public ICommand PowerOffCommand { get; }

    // ------------------------------------------------------------ plumbing

    public event PropertyChangedEventHandler? PropertyChanged;

    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}
