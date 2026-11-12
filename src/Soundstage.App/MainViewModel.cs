using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Soundstage.Engine;

namespace Soundstage.UI;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

public sealed class AppSettings
{
    public string? OutputId { get; set; }
    public string? SourceId { get; set; }
    public bool Tracking { get; set; } = true;
    public bool AutoCenter { get; set; } = true;
    public double PredictionMs { get; set; } = 100;
    public bool MeasuredModel { get; set; } = true;
    public double RoomPercent { get; set; } = 35;
    /// <summary>Point Windows at the capture device while spatial audio is on, and back afterwards.</summary>
    public bool AutoSwitchOutput { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool Hotkeys { get; set; } = true;
    /// <summary>The user left spatial audio on; resume it when the app or headphones come back.</summary>
    public bool SpatialOn { get; set; }
    /// <summary>What Windows was playing to before Soundstage redirected it.</summary>
    public string? PreviousOutputId { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundstage", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    readonly EngineHost _engine = new();
    readonly AppSettings _settings = AppSettings.Load();
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _timer;
    readonly Stopwatch _sinceStart = new();
    bool _loading;

    public MainViewModel()
    {
        _tracking = _settings.Tracking;
        _autoCenter = _settings.AutoCenter;
        _predictionMs = _settings.PredictionMs;
        _measuredModel = _settings.MeasuredModel;
        _roomPercent = Math.Clamp(_settings.RoomPercent, 0, 100);
        _engine.Render.Room = (float)(_roomPercent / 100.0);
        ApplyPoseSettings();

        RecenterCommand = new RelayCommand(() => _engine.Pose.Recenter(), () => IsRunning);
        TestSoundCommand = new RelayCommand(ToggleTestSound, () => SelectedOutput != null);
        FixTrackerCommand = new RelayCommand(FixTracker, () => !IsFixing);
        RefreshCommand = new RelayCommand(RefreshDevices);
        SoundSettingsCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }));
        // The speaker-layout wizard only exists in the classic Sound control panel.
        SurroundSetupCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,0") { UseShellExecute = true }));

        _engine.StateChanged += () => _ui.BeginInvoke(OnEngineStateChanged);
        _engine.Faulted += message => _ui.BeginInvoke(() => OnEngineFaulted(message));

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        RefreshDevices();

        // Resume spatial audio if it was left on, now or whenever the headphones return.
        _resumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _resumeTimer.Tick += (_, _) => TryResume();
        _resumeTimer.Start();
        if (_settings.SpatialOn && SelectedOutput != null && SelectedSource != null) IsOn = true;
    }

    readonly DispatcherTimer _resumeTimer;
    bool _resuming;
    int _resumeFailures;

    void TryResume()
    {
        if (_isOn && IsRunning && _sinceStart.Elapsed.TotalSeconds > 10) _resumeFailures = 0;
        // Give up after repeated failures so a persistent problem does not loop forever.
        if (!_settings.SpatialOn || _isOn || _testing || _resuming || _resumeFailures >= 3) return;
        _resuming = true;
        Task.Run(() =>
        {
            List<AudioDeviceInfo> found;
            try { found = EngineHost.ListOutputs(); }
            catch (Exception) { found = new List<AudioDeviceInfo>(); }
            _ui.BeginInvoke(() =>
            {
                _resuming = false;
                if (!_settings.SpatialOn || _isOn || _testing) return;
                ApplyDevices(found);
                if (SelectedOutput != null && SelectedSource != null) IsOn = true;
            });
        });
    }

    // ------------------------------------------------------------- navigation

    int _page;
    /// <summary>0 = Spatial audio, 1 = Headphones, 2 = Settings.</summary>
    public int Page { get => _page; set => Set(ref _page, value); }

    // --------------------------------------------------------------- settings

    public bool AutoSwitchOutput
    {
        get => _settings.AutoSwitchOutput;
        set
        {
            if (_settings.AutoSwitchOutput == value) return;
            _settings.AutoSwitchOutput = value;
            _settings.Save();
            Notify(nameof(AutoSwitchOutput));
            Notify(nameof(SourceHint));
            if (value && _isOn) RouteOutputToSource();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { if (_settings.CloseToTray != value) { _settings.CloseToTray = value; _settings.Save(); Notify(nameof(CloseToTray)); } }
    }

    public bool StartWithWindows
    {
        get { try { return Startup.IsEnabled; } catch (Exception) { return false; } }
        set
        {
            try { Startup.Set(value); } catch (Exception) { }
            Notify(nameof(StartWithWindows));
        }
    }

    /// <summary>Raised when the shortcuts should be (un)registered; returns false if some are taken.</summary>
    public Func<bool, bool>? ApplyHotkeys;

    public bool HotkeysEnabled
    {
        get => _settings.Hotkeys;
        set
        {
            if (_settings.Hotkeys == value) return;
            _settings.Hotkeys = value;
            _settings.Save();
            Notify(nameof(HotkeysEnabled));
            HotkeyConflict = !(ApplyHotkeys?.Invoke(value) ?? true) && value;
        }
    }

    bool _hotkeyConflict;
    public bool HotkeyConflict { get => _hotkeyConflict; set => Set(ref _hotkeyConflict, value); }

    public string Version =>
        "Version " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");

    // ------------------------------------------------------ Windows output

    void RouteOutputToSource()
    {
        string? source = SelectedSource?.Id;
        if (!_settings.AutoSwitchOutput || source == null) return;
        Task.Run(() =>
        {
            string? current = EngineHost.GetDefaultOutputId();
            if (current == source) return;
            if (current != null) { _settings.PreviousOutputId = current; _settings.Save(); }
            EngineHost.SetDefaultOutput(source);
        });
    }

    /// <summary>If Windows is still pointed at the capture device, send it back to the headphones.</summary>
    void RestoreOutput(bool wait = false)
    {
        string? source = SelectedSource?.Id ?? _settings.SourceId;
        string? target = SelectedOutput?.Id ?? _settings.PreviousOutputId;
        if (!_settings.AutoSwitchOutput || source == null || target == null || target == source) return;
        var task = Task.Run(() =>
        {
            if (EngineHost.GetDefaultOutputId() == source) EngineHost.SetDefaultOutput(target);
        });
        if (wait) task.Wait(2000);
    }

    public void Recenter()
    {
        if (IsRunning) _engine.Pose.Recenter();
    }

    // ---------------------------------------------------------------- devices

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    AudioDeviceInfo? _selectedOutput;
    public AudioDeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!Set(ref _selectedOutput, value) || _loading) return;
            _settings.OutputId = value?.Id;
            _settings.Save();
            NotifySource();
            RestartIfRunning();
        }
    }

    AudioDeviceInfo? _selectedSource;
    public AudioDeviceInfo? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value) || _loading) return;
            _settings.SourceId = value?.Id;
            _settings.Save();
            NotifySource();
            RestartIfRunning();
        }
    }

    void NotifySource()
    {
        Notify(nameof(SourceHint));
        Notify(nameof(SourceLayoutText));
        Notify(nameof(CanSetUpSurround));
        if (!_engine.IsRunning) Layout = IdleLayout();
    }

    /// <summary>While stopped, preview the speakers the capture device will provide.</summary>
    SpeakerLayout? IdleLayout() =>
        SelectedSource is { Channels: > 0 } s ? SpeakerLayout.FromMask(s.Channels, s.ChannelMask) : null;

    /// <summary>What the capture device is set to, and whether it could do more.</summary>
    public string SourceLayoutText
    {
        get
        {
            var s = SelectedSource;
            if (s == null || s.Channels == 0) return "";
            string now = AudioDeviceInfo.LayoutName(s.Channels);
            return s.MaxChannels > s.Channels
                ? $"Set to {now}. This device can do {AudioDeviceInfo.LayoutName(s.MaxChannels)}, which gives games and films real surround positions."
                : $"Set to {now}.";
        }
    }

    public bool CanSetUpSurround => SelectedSource is { } s && s.MaxChannels > s.Channels;

    public string SourceHint => SelectedSource == null
        ? "Pick a virtual audio device to capture. Apps play to it and Soundstage sends the spatial mix to your headphones."
        : _settings.AutoSwitchOutput
            ? $"While spatial audio is on, Soundstage makes “{SelectedSource.Name}” the Windows output and switches back when you turn it off."
            : $"In Windows sound settings, choose “{SelectedSource.Name}” as the output device. Soundstage sends the spatial mix to your headphones.";

    public void RefreshDevices()
    {
        List<AudioDeviceInfo> found;
        try { found = EngineHost.ListOutputs(); }
        catch (Exception) { found = new List<AudioDeviceInfo>(); }
        ApplyDevices(found);
    }

    void ApplyDevices(List<AudioDeviceInfo> found)
    {
        int channelsBefore = _selectedSource?.Channels ?? 0;
        string? sourceBefore = _selectedSource?.Id;
        _loading = true;
        try
        {
            string? outId = _selectedOutput?.Id ?? _settings.OutputId;
            string? srcId = _selectedSource?.Id ?? _settings.SourceId;
            Devices.Clear();
            foreach (var d in found) Devices.Add(d);

            SelectedOutput = found.FirstOrDefault(d => d.Id == outId)
                ?? found.FirstOrDefault(d => d.Name.Contains("WH-1000X", StringComparison.OrdinalIgnoreCase))
                ?? found.FirstOrDefault(d => d.Name.Contains("WF-1000X", StringComparison.OrdinalIgnoreCase));
            SelectedSource = found.FirstOrDefault(d => d.Id == srcId && d.Id != SelectedOutput?.Id)
                ?? found.FirstOrDefault(d => d.Name.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase))
                ?? found.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        }
        finally { _loading = false; }
        NotifySource();
        if (!IsRunning) UpdateIdleStatus();
        // The capture device was reconfigured (say stereo -> 7.1): pick up the new layout.
        else if (!_testing && _selectedSource?.Id == sourceBefore && (_selectedSource?.Channels ?? 0) != channelsBefore)
            RestartIfRunning();
    }

    // ----------------------------------------------------------------- engine

    bool _isOn;
    /// <summary>The master switch.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value)) return;
            _settings.SpatialOn = value;
            _settings.Save();
            if (value) { _testing = false; Notify(nameof(TestSoundLabel)); StartEngine(); }
            else { StopEngine(); RestoreOutput(); }
        }
    }

    bool _testing;
    public string TestSoundLabel => _testing ? "Stop test sound" : "Play test sound";

    public bool IsRunning => _engine.IsRunning;

    void StartEngine()
    {
        ErrorText = null;
        var options = new EngineOptions
        {
            OutputId = SelectedOutput?.Id,
            OutputName = null,
            SourceId = SelectedSource?.Id,
            SourceName = null,
            Demo = _testing,
            SimpleModel = !_measuredModel,
            // With a surround source the test sound visits every speaker; otherwise it
            // stays straight ahead, which is the clearest check of head tracking.
            DemoMask = _testing && SelectedSource is { Channels: > 2 } src
                ? (src.ChannelMask != 0 ? src.ChannelMask : src.Channels == 8 ? 0x63Fu : 0x3Fu)
                : 0,
        };
        if (options.OutputId == null) { Fail("Choose your headphones first."); return; }
        if (!_testing && options.SourceId == null) { Fail("Choose a device to capture first."); return; }
        Status("Starting…", "idle");
        if (!_testing) RouteOutputToSource();
        _sinceStart.Restart();
        _engine.Start(options);
    }

    void StopEngine() => Task.Run(_engine.Stop);

    void RestartIfRunning()
    {
        if (!_isOn && !_testing) return;
        Task.Run(() =>
        {
            _engine.Stop();
            _ui.BeginInvoke(() => { if (_isOn || _testing) StartEngine(); });
        });
    }

    void ToggleTestSound()
    {
        if (_testing)
        {
            _testing = false;
            StopEngine();
        }
        else
        {
            // The test sound replaces live audio while it plays.
            if (_isOn)
            {
                _isOn = false;
                Notify(nameof(IsOn));
                _settings.SpatialOn = false;
                _settings.Save();
                RestoreOutput();
            }
            _testing = true;
            Task.Run(() =>
            {
                _engine.Stop();
                _ui.BeginInvoke(StartEngine);
            });
        }
        Notify(nameof(TestSoundLabel));
    }

    void OnEngineStateChanged()
    {
        Notify(nameof(IsRunning));
        Layout = _engine.IsRunning ? _engine.Layout : IdleLayout();
        CommandManager.InvalidateRequerySuggested();
        if (!_engine.IsRunning && ErrorText == null) UpdateIdleStatus();
    }

    void OnEngineFaulted(string message) => Fail(message);

    void Fail(string message)
    {
        _testing = false;
        Notify(nameof(TestSoundLabel));
        if (_isOn)
        {
            // SpatialOn stays set, so it resumes by itself when the headphones return.
            _isOn = false;
            Notify(nameof(IsOn));
            RestoreOutput();
        }
        _resumeFailures++;
        ErrorText = message;
        Status("Stopped", "error");
    }

    void UpdateIdleStatus()
    {
        if (SelectedOutput == null) Status("Headphones not connected", "warn");
        else Status("Off", "idle");
    }

    // ------------------------------------------------------------------ sound

    bool _measuredModel;
    /// <summary>Measured ear responses (on) or the simple mathematical head model (off).</summary>
    public bool MeasuredModel
    {
        get => _measuredModel;
        set
        {
            if (!Set(ref _measuredModel, value)) return;
            _settings.MeasuredModel = value;
            _settings.Save();
            Notify(nameof(RoomAvailable));
            RestartIfRunning();
        }
    }

    /// <summary>The room is part of the measured-response renderer only.</summary>
    public bool RoomAvailable => _measuredModel;

    double _roomPercent;
    public double RoomPercent
    {
        get => _roomPercent;
        set
        {
            if (!Set(ref _roomPercent, Math.Round(value))) return;
            _engine.Render.Room = (float)(_roomPercent / 100.0);   // applies immediately
            _settings.RoomPercent = _roomPercent;
            _settings.Save();
            Notify(nameof(RoomText));
        }
    }
    public string RoomText => _roomPercent <= 0 ? "Off" : $"{_roomPercent:0}%";

    // ---------------------------------------------------------- head tracking

    bool _tracking;
    public bool Tracking
    {
        get => _tracking;
        set { if (Set(ref _tracking, value)) { _settings.Tracking = value; SavePose(); } }
    }

    bool _autoCenter;
    public bool AutoCenter
    {
        get => _autoCenter;
        set { if (Set(ref _autoCenter, value)) { _settings.AutoCenter = value; SavePose(); } }
    }

    double _predictionMs;
    public double PredictionMs
    {
        get => _predictionMs;
        set
        {
            if (!Set(ref _predictionMs, Math.Round(value))) return;
            _settings.PredictionMs = _predictionMs;
            Notify(nameof(PredictionText));
            SavePose();
        }
    }
    public string PredictionText => $"{_predictionMs:0} ms";

    void SavePose()
    {
        ApplyPoseSettings();
        _settings.Save();
    }

    void ApplyPoseSettings()
    {
        _engine.Pose.Enabled = _tracking;
        _engine.Pose.AutoCenter = _autoCenter;
        _engine.Pose.PredictSeconds = _predictionMs / 1000.0;
    }

    bool _trackerMissing;
    public bool TrackerMissing { get => _trackerMissing; private set => Set(ref _trackerMissing, value); }

    bool _isFixing;
    public bool IsFixing { get => _isFixing; private set => Set(ref _isFixing, value); }

    void FixTracker()
    {
        IsFixing = true;
        Task.Run(() =>
        {
            int code;
            try { code = EngineHost.RepairTracker(null); }
            catch (Exception) { code = -3; }   // includes a declined admin prompt
            _ui.BeginInvoke(() =>
            {
                IsFixing = false;
                if (code == -1) ErrorText = "sony-head-tracker.exe was not found next to Soundstage.";
                else if (code != 0) ErrorText = "The tracker repair did not finish. Check that the headphones are connected.";
                RestartIfRunning();
            });
        });
    }

    // ----------------------------------------------------------------- status

    double _yaw, _pitch, _roll;
    public double Yaw { get => _yaw; private set => Set(ref _yaw, value); }
    public string YawText => $"{_yaw:0}°";
    public string PitchText => $"{_pitch:0}°";
    public string RollText => $"{_roll:0}°";

    SpeakerLayout? _layout;
    public SpeakerLayout? Layout { get => _layout; private set => Set(ref _layout, value); }

    double[]? _levels;
    /// <summary>Smoothed level per source channel (0..1), for lighting up the speakers.</summary>
    public double[]? Levels { get => _levels; private set { _levels = value; Notify(nameof(Levels)); } }

    void UpdateLevels(float[]? peaks)
    {
        if (peaks == null) { if (_levels != null) Levels = null; return; }
        var next = new double[peaks.Length];
        for (int i = 0; i < peaks.Length; i++)
        {
            double previous = _levels != null && _levels.Length == peaks.Length ? _levels[i] : 0;
            next[i] = Math.Max(ToMeter(20 * Math.Log10(Math.Max(peaks[i], 1e-6f))), previous - 0.05);
        }
        Levels = next;
    }

    bool _headActive;
    public bool HeadActive { get => _headActive; private set => Set(ref _headActive, value); }

    double _inLevel, _outLevel;
    public double InputLevel { get => _inLevel; private set => Set(ref _inLevel, value); }
    public double OutputLevel { get => _outLevel; private set => Set(ref _outLevel, value); }

    string _trackerText = "—", _bufferText = "—", _dropoutText = "—";
    public string TrackerText { get => _trackerText; private set => Set(ref _trackerText, value); }
    public string BufferText { get => _bufferText; private set => Set(ref _bufferText, value); }
    public string DropoutText { get => _dropoutText; private set => Set(ref _dropoutText, value); }

    string _statusText = "Off", _statusKind = "idle";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    string? _errorText;
    public string? ErrorText
    {
        get => _errorText;
        set { if (Set(ref _errorText, value)) Notify(nameof(HasError)); }
    }
    public bool HasError => _errorText != null;

    void Status(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    int _tick;

    void Poll()
    {
        EngineStatus s = _engine.GetStatus();
        bool tracked = s.Running && _tracking && s.TrackerRate >= 1;

        _pitch = s.Pitch; _roll = s.Roll;
        Yaw = s.Yaw;
        HeadActive = tracked;
        UpdateLevels(s.ChannelPeaks);

        // Meters: jump up instantly, fall back smoothly.
        InputLevel = Math.Max(ToMeter(s.InputDb), InputLevel - 0.03);
        OutputLevel = Math.Max(ToMeter(s.OutputDb), OutputLevel - 0.03);

        if (++_tick % 6 != 0) return;   // text at ~5 Hz is plenty
        Notify(nameof(YawText)); Notify(nameof(PitchText)); Notify(nameof(RollText));
        if (!s.Running)
        {
            TrackerText = BufferText = DropoutText = "—";
            TrackerMissing = false;
            return;
        }
        TrackerText = s.TrackerRate >= 1 ? $"{s.TrackerRate:0} updates/s" : "No data";
        BufferText = $"{s.BufferMs:0} ms";
        DropoutText = s.Underruns.ToString();
        TrackerMissing = _tracking && s.TrackerRate < 1 && _sinceStart.Elapsed.TotalSeconds > 3;

        if (_testing) Status("Playing test sound", "ok");
        else if (TrackerMissing) Status("On · no head tracking", "warn");
        else if (!_tracking) Status("On · head tracking off", "ok");
        else Status("On · head tracking active", "ok");
    }

    static double ToMeter(double db) => Math.Clamp((db + 60.0) / 60.0, 0, 1);

    // --------------------------------------------------------------- commands

    public ICommand RecenterCommand { get; }
    public ICommand TestSoundCommand { get; }
    public ICommand FixTrackerCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SoundSettingsCommand { get; }
    public ICommand SurroundSetupCommand { get; }

    /// <summary>The Headphones page.</summary>
    public HeadphonesViewModel Headphones { get; } = new();

    public void Shutdown()
    {
        Headphones.Shutdown();
        _resumeTimer.Stop();
        // Leave Windows playing to the headphones, not to a capture device nobody is reading.
        if (_isOn) RestoreOutput(wait: true);
        _timer.Stop();
        _settings.Save();
        _engine.Stop();
    }

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
