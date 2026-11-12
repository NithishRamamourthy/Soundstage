// EngineHost: captures a (virtual) surround device in loopback, renders it
// binaurally with head tracking and plays the result on the headphones.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Soundstage.Engine
{
    public sealed class AudioDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Channels { get; set; }
        public int SampleRate { get; set; }
        /// <summary>Channel mask of the format the device is set to now.</summary>
        public uint ChannelMask { get; set; }
        /// <summary>The most channels the driver accepts (8, 6 or 2; 0 if unknown).</summary>
        public int MaxChannels { get; set; }

        /// <summary>"Stereo", "5.1 surround", "7.1 surround" or "N channels".</summary>
        public static string LayoutName(int channels)
        {
            switch (channels)
            {
                case 1: return "Mono";
                case 2: return "Stereo";
                case 6: return "5.1 surround";
                case 8: return "7.1 surround";
                default: return channels + " channels";
            }
        }

        public override string ToString() { return Name; }
    }

    public sealed class EngineOptions
    {
        /// <summary>Endpoint id of the device to capture; falls back to SourceName.</summary>
        public string SourceId;
        public string SourceName = "Steam Streaming Speakers";
        /// <summary>Endpoint id of the headphones; falls back to OutputName.</summary>
        public string OutputId;
        public string OutputName = "WH-1000XM6";
        public string TrackerPath;
        public int Port = 4242;
        public bool UseTracker = true;
        /// <summary>Ignore the source device and play test noise from DemoAzimuth.</summary>
        public bool Demo;
        public double DemoAzimuth;
        /// <summary>With Demo: a channel mask (e.g. 0x63F for 7.1) whose speakers the test
        /// noise visits one after another. 0 keeps the single source at DemoAzimuth.</summary>
        public uint DemoMask;
        /// <summary>Use the mathematical head model instead of measured ear responses.</summary>
        public bool SimpleModel;
        /// <summary>Play test noise into the source device to exercise the full chain.</summary>
        public bool FeedTest;
        public int FeedChannel;
    }

    public struct EngineStatus
    {
        public bool Running;
        public double Yaw, Pitch, Roll;
        public double TrackerRate;
        public double InputDb, OutputDb;
        public double BufferMs, AdjustPercent;
        public int Underruns;
        /// <summary>Peak level per source channel since the last call, 0..1 (null when stopped).</summary>
        public float[] ChannelPeaks;
    }

    public sealed class EngineHost
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly object _gate = new object();
        Thread _thread;
        volatile bool _quit;
        volatile bool _running;
        float _inPeak, _outPeak;
        volatile float[] _chanPeak;
        StereoRing _ring;
        int _sourceRate;
        int _lastSamples;
        readonly Stopwatch _statusClock = Stopwatch.StartNew();
        double _lastStatusTime;
        double _lastRate;

        /// <summary>Head pose and its tuning; lives across sessions so settings stick.</summary>
        public HeadPose Pose { get; private set; }
        public SpeakerLayout Layout { get; private set; }
        /// <summary>Renderer settings that apply immediately, even while running.</summary>
        public RenderSettings Render { get; private set; }
        public bool IsRunning { get { return _running; } }

        /// <summary>Informational messages (device names, tracker state).</summary>
        public event Action<string> Log;
        /// <summary>The session ended because of an error.</summary>
        public event Action<string> Faulted;
        /// <summary>IsRunning changed.</summary>
        public event Action StateChanged;

        public EngineHost()
        {
            Pose = new HeadPose();
            Render = new RenderSettings();
        }

        public static List<AudioDeviceInfo> ListOutputs()
        {
            List<AudioDeviceInfo> result = new List<AudioDeviceInfo>();
            foreach (AudioEndpoint ep in Wasapi.RenderEndpoints())
            {
                AudioDeviceInfo info = new AudioDeviceInfo { Id = ep.Id, Name = ep.Name };
                try
                {
                    WaveFormat f = Wasapi.MixFormat(ep);
                    info.Channels = f.Channels;
                    info.SampleRate = f.SampleRate;
                    info.ChannelMask = f.ChannelMask;
                    info.MaxChannels = Wasapi.MaxChannels(ep);
                }
                catch (Exception) { }
                result.Add(info);
            }
            return result;
        }

        /// <summary>Id of the playback device Windows currently uses, or null.</summary>
        public static string GetDefaultOutputId()
        {
            try { return Wasapi.DefaultRenderId(); }
            catch (Exception) { return null; }
        }

        /// <summary>Makes a playback device the Windows default. False if Windows refused.</summary>
        public static bool SetDefaultOutput(string id)
        {
            return !string.IsNullOrEmpty(id) && Wasapi.SetDefaultRender(id);
        }

        /// <summary>Locates sony-head-tracker.exe: an explicit path, next to the app, in
        /// third_party, or the copy bundled in this build.</summary>
        public static string FindTracker(string hint)
        {
            if (!string.IsNullOrEmpty(hint) && File.Exists(hint)) return Path.GetFullPath(hint);
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int up = 0; up < 7 && dir != null; up++)
            {
                string direct = Path.Combine(dir, "sony-head-tracker.exe");
                if (File.Exists(direct)) return direct;
                string third = Path.Combine(dir, "third_party");
                if (Directory.Exists(third))
                {
                    string[] found = Directory.GetFiles(third, "sony-head-tracker.exe", SearchOption.AllDirectories);
                    if (found.Length > 0) return found[0];
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return TrackerBundle.Ensure();
        }

        /// <summary>Runs the tracker's own repair step (Windows shows one admin prompt).</summary>
        public static int RepairTracker(string trackerPath)
        {
            string path = FindTracker(trackerPath);
            if (path == null) return -1;
            ProcessStartInfo psi = new ProcessStartInfo(path, "repair");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(120000);
                return p.HasExited ? p.ExitCode : -2;
            }
        }

        public void Start(EngineOptions options)
        {
            lock (_gate)
            {
                if (_thread != null && _thread.IsAlive) return;
                _quit = false;
                _thread = new Thread(delegate() { Run(options); });
                _thread.IsBackground = true;
                _thread.Name = "engine";
                _thread.Start();
            }
        }

        public void Stop()
        {
            Thread t;
            lock (_gate) { t = _thread; _quit = true; }
            if (t != null && t != Thread.CurrentThread) t.Join(4000);
        }

        public EngineStatus GetStatus()
        {
            EngineStatus s = new EngineStatus();
            s.Running = _running;
            double yaw, pitch, roll;
            Pose.Snapshot(out yaw, out pitch, out roll);
            s.Yaw = yaw; s.Pitch = pitch; s.Roll = roll;

            double now = _statusClock.Elapsed.TotalSeconds;
            if (now - _lastStatusTime >= 0.5)
            {
                int sc = Pose.SampleCount;
                _lastRate = (sc - _lastSamples) / (now - _lastStatusTime);
                if (_lastRate < 0) _lastRate = 0;
                _lastSamples = sc;
                _lastStatusTime = now;
            }
            s.TrackerRate = _running ? _lastRate : 0;

            float ip = _inPeak, op = _outPeak;
            _inPeak = 0; _outPeak = 0;
            s.InputDb = Db(ip);
            s.OutputDb = Db(op);
            float[] peaks = _chanPeak;
            if (_running && peaks != null)
            {
                s.ChannelPeaks = new float[peaks.Length];
                for (int i = 0; i < peaks.Length; i++) { s.ChannelPeaks[i] = peaks[i]; peaks[i] = 0; }
            }
            StereoRing ring = _ring;
            if (ring != null && _sourceRate > 0)
            {
                s.BufferMs = 1000.0 * ring.Count / _sourceRate;
                s.AdjustPercent = ring.Epsilon * 100.0;
                s.Underruns = ring.Underruns;
            }
            return s;
        }

        static double Db(float peak)
        {
            return peak <= 1e-6f ? -120.0 : 20.0 * Math.Log10(peak);
        }

        void Say(string message)
        {
            Action<string> h = Log;
            if (h != null) h(message);
        }

        void Run(EngineOptions opt)
        {
            Process tracker = null;
            TrackerListener listener = null;
            IAudioClient outClient = null, srcClient = null;
            Thread tIn = null, tOut = null;
            string fault = null;
            Wasapi.timeBeginPeriod(1);
            try
            {
                List<AudioEndpoint> endpoints = Wasapi.RenderEndpoints();
                AudioEndpoint outEp = FindEndpoint(endpoints, opt.OutputId, opt.OutputName);
                if (outEp == null) throw new InvalidOperationException("Headphones not found. Check that they are connected.");
                AudioEndpoint srcEp = null;
                if (!opt.Demo)
                {
                    srcEp = FindEndpoint(endpoints, opt.SourceId, opt.SourceName);
                    if (srcEp == null) throw new InvalidOperationException("Source device not found.");
                    if (srcEp.Id == outEp.Id) throw new InvalidOperationException("The source and the headphones must be different devices.");
                }

                // ---- output ----
                outClient = Wasapi.Activate(outEp);
                WaveFormat outFmt = Wasapi.InitShared(outClient, false, 1000000);
                if (!outFmt.IsFloat || outFmt.Bits != 32) throw new InvalidOperationException("Output mix format is not 32-bit float.");
                object o;
                Guid iidRender = Wasapi.IID_IAudioRenderClient;
                Wasapi.Check(outClient.GetService(ref iidRender, out o), "GetService(render)");
                IAudioRenderClient render = (IAudioRenderClient)o;
                Say("Output : " + outEp.Name + "  [" + outFmt + "]");

                // ---- source ----
                IAudioCaptureClient capture = null;
                WaveFormat srcFmt;
                SpeakerLayout layout;
                if (opt.Demo)
                {
                    srcFmt = new WaveFormat();
                    srcFmt.SampleRate = outFmt.SampleRate; srcFmt.Bits = 32; srcFmt.IsFloat = true;
                    if (opt.DemoMask != 0)
                    {
                        int bits = 0;
                        for (uint m = opt.DemoMask; m != 0; m >>= 1) bits += (int)(m & 1);
                        srcFmt.Channels = bits;
                        srcFmt.ChannelMask = opt.DemoMask;
                        layout = SpeakerLayout.FromMask(bits, opt.DemoMask);
                        Say("Source : built-in test noise visiting each speaker");
                    }
                    else
                    {
                        srcFmt.Channels = 1;
                        layout = SpeakerLayout.Single(opt.DemoAzimuth);
                        Say("Source : built-in test noise at azimuth " + opt.DemoAzimuth.ToString("0", Inv) + " deg");
                    }
                }
                else
                {
                    srcClient = Wasapi.Activate(srcEp);
                    srcFmt = Wasapi.InitShared(srcClient, true, 1000000);
                    if (!srcFmt.IsFloat || srcFmt.Bits != 32) throw new InvalidOperationException("Source mix format is not 32-bit float.");
                    Guid iidCapture = Wasapi.IID_IAudioCaptureClient;
                    Wasapi.Check(srcClient.GetService(ref iidCapture, out o), "GetService(capture)");
                    capture = (IAudioCaptureClient)o;
                    layout = SpeakerLayout.FromMask(srcFmt.Channels, srcFmt.ChannelMask);
                    Say("Source : " + srcEp.Name + "  [" + srcFmt + "]");
                }
                Say("Layout : " + layout);
                Layout = layout;

                // ---- head tracking ----
                Pose.Reset();
                if (opt.UseTracker)
                {
                    listener = new TrackerListener(Pose, opt.Port + 1);
                    if (!listener.Start())
                    {
                        Say("Tracker: UDP port " + (opt.Port + 1) + " is busy; head tracking unavailable.");
                    }
                    else
                    {
                        // If a tracker (its GUI, say) is already broadcasting, use it as is.
                        Thread.Sleep(400);
                        if (Pose.SampleCount > 0)
                        {
                            Say("Tracker: using the tracker that is already running (UDP " + (opt.Port + 1) + ")");
                        }
                        else
                        {
                            tracker = StartTracker(opt.TrackerPath, opt.Port);
                            Say(tracker != null
                                ? "Tracker: started bridge (pid " + tracker.Id + "), listening on UDP " + (opt.Port + 1)
                                : "Tracker: bridge executable not found; listening on UDP " + (opt.Port + 1) + " for an external one");
                        }
                    }
                }

                IRenderer dsp = opt.SimpleModel || !HrtfSet.IsAvailable
                    ? (IRenderer)new Binaural(srcFmt.SampleRate, layout, Pose)
                    : new HrtfBinaural(srcFmt.SampleRate, layout, Pose, Render);
                Say("Sound  : " + (dsp is HrtfBinaural ? "measured ear responses (MIT KEMAR)" : "simple head model"));
                StereoRing ring = new StereoRing(srcFmt.SampleRate, (int)(srcFmt.SampleRate * 0.035));
                _sourceRate = srcFmt.SampleRate;
                _ring = ring;
                _chanPeak = new float[layout.Count];

                IAudioClient srcClientLocal = srcClient, outClientLocal = outClient;
                tIn = opt.Demo
                    ? new Thread(delegate() { DemoLoop(srcFmt.SampleRate, layout, dsp, ring); })
                    : new Thread(delegate() { CaptureLoop(srcClientLocal, capture, srcFmt, dsp, ring); });
                tOut = new Thread(delegate() { RenderLoop(outClientLocal, render, outFmt, srcFmt.SampleRate, ring); });
                tIn.Priority = ThreadPriority.Highest; tOut.Priority = ThreadPriority.Highest;
                tIn.IsBackground = true; tOut.IsBackground = true;
                tIn.Name = "dsp"; tOut.Name = "render";
                tIn.Start(); tOut.Start();

                if (opt.FeedTest && !opt.Demo)
                {
                    IAudioClient feedClient = Wasapi.Activate(srcEp);
                    WaveFormat feedFmt = Wasapi.InitShared(feedClient, false, 1000000);
                    Guid iidFeed = Wasapi.IID_IAudioRenderClient;
                    Wasapi.Check(feedClient.GetService(ref iidFeed, out o), "GetService(feed)");
                    IAudioRenderClient feedRender = (IAudioRenderClient)o;
                    int feedChannel = opt.FeedChannel;
                    Thread tFeed = new Thread(delegate() { FeedLoop(feedClient, feedRender, feedFmt, feedChannel); });
                    tFeed.IsBackground = true;
                    tFeed.Name = "feed";
                    tFeed.Start();
                    Say("Feed   : test noise into source channel " + feedChannel);
                }

                _running = true;
                RaiseStateChanged();
                while (!_quit) Thread.Sleep(50);
                fault = _loopFault;
            }
            catch (Exception ex)
            {
                fault = ex.Message;
            }
            finally
            {
                _quit = true;
                if (tIn != null) tIn.Join(1000);
                if (tOut != null) tOut.Join(1000);
                try { if (srcClient != null) srcClient.Stop(); } catch (Exception) { }
                try { if (outClient != null) outClient.Stop(); } catch (Exception) { }
                if (listener != null) listener.Stop();
                if (tracker != null)
                {
                    try { if (!tracker.HasExited) tracker.Kill(); } catch (Exception) { }
                }
                Wasapi.timeEndPeriod(1);
                _ring = null;
                _chanPeak = null;
                _loopFault = null;
                _running = false;
                RaiseStateChanged();
                if (fault != null)
                {
                    Action<string> h = Faulted;
                    if (h != null) h(fault);
                }
            }
        }

        volatile string _loopFault;

        void RaiseStateChanged()
        {
            Action h = StateChanged;
            if (h != null) h();
        }

        static AudioEndpoint FindEndpoint(List<AudioEndpoint> endpoints, string id, string name)
        {
            if (!string.IsNullOrEmpty(id))
            {
                foreach (AudioEndpoint ep in endpoints) if (ep.Id == id) return ep;
            }
            return string.IsNullOrEmpty(name) ? null : Wasapi.Find(endpoints, name);
        }

        static Process StartTracker(string hint, int port)
        {
            string path = FindTracker(hint);
            if (path == null) return null;
            ProcessStartInfo psi = new ProcessStartInfo(path, "bridge --port " + port.ToString(Inv));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            // Drain and discard the bridge's log so its pipes never fill up.
            p.OutputDataReceived += delegate { };
            p.ErrorDataReceived += delegate { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        static void TrackPeak(float[] buf, int count, ref float peak)
        {
            float p = peak;
            for (int i = 0; i < count; i++)
            {
                float a = buf[i] < 0 ? -buf[i] : buf[i];
                if (a > p) p = a;
            }
            peak = p;
        }

        void CaptureLoop(IAudioClient client, IAudioCaptureClient capture, WaveFormat fmt, IRenderer dsp, StereoRing ring)
        {
            try
            {
                int ch = fmt.Channels;
                float[] inBuf = new float[fmt.SampleRate * ch / 4];
                float[] outBuf = new float[fmt.SampleRate * 2 / 4];
                Wasapi.Check(client.Start(), "capture Start");
                while (!_quit)
                {
                    uint packet;
                    Wasapi.Check(capture.GetNextPacketSize(out packet), "The source device");
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong devPos, qpc;
                        Wasapi.Check(capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc), "The source device");
                        int n = (int)frames;
                        if (n * ch > inBuf.Length) { inBuf = new float[n * ch]; outBuf = new float[n * 2]; }
                        if ((flags & Wasapi.BUFFERFLAGS_SILENT) != 0) Array.Clear(inBuf, 0, n * ch);
                        else Marshal.Copy(data, inBuf, 0, n * ch);
                        capture.ReleaseBuffer(frames);

                        TrackPeak(inBuf, n * ch, ref _inPeak);
                        TrackChannelPeaks(inBuf, n, ch);
                        dsp.Process(inBuf, n, outBuf);
                        TrackPeak(outBuf, n * 2, ref _outPeak);
                        ring.Write(outBuf, n);

                        Wasapi.Check(capture.GetNextPacketSize(out packet), "The source device");
                    }
                    Thread.Sleep(2);
                }
            }
            catch (Exception)
            {
                if (!_quit) { _loopFault = "The source device stopped responding."; _quit = true; }
            }
        }

        void TrackChannelPeaks(float[] buf, int frames, int ch)
        {
            float[] peaks = _chanPeak;
            if (peaks == null || peaks.Length != ch) return;
            for (int f = 0; f < frames; f++)
            {
                int o = f * ch;
                for (int c = 0; c < ch; c++)
                {
                    float a = buf[o + c] < 0 ? -buf[o + c] : buf[o + c];
                    if (a > peaks[c]) peaks[c] = a;
                }
            }
        }

        /// <summary>The order in which the test noise visits the speakers: clockwise from
        /// front left, skipping the subwoofer channel.</summary>
        static int[] WalkOrder(SpeakerLayout layout)
        {
            List<int> order = new List<int>();
            for (int i = 0; i < layout.Count; i++) if (!layout.IsLfe[i]) order.Add(i);
            order.Sort(delegate(int a, int b)
            {
                double ka = (layout.Azimuth[a] + 30 + 360) % 360, kb = (layout.Azimuth[b] + 30 + 360) % 360;
                return ka.CompareTo(kb);
            });
            return order.ToArray();
        }

        /// <summary>Stands in for the capture device: bursts of soft noise, either from one
        /// direction or visiting each speaker of the layout in turn.</summary>
        void DemoLoop(int sampleRate, SpeakerLayout layout, IRenderer dsp, StereoRing ring)
        {
            Random rng = new Random(1);
            Stopwatch sw = Stopwatch.StartNew();
            long produced = 0;
            int ch = layout.Count;
            int[] order = WalkOrder(layout);
            const int BurstsPerSpeaker = 3;
            float[] inBuf = new float[4096 * ch];
            float[] outBuf = new float[8192];
            double lp = 0;
            int period = (int)(sampleRate * 0.30), on = (int)(sampleRate * 0.18), fade = (int)(sampleRate * 0.005);
            while (!_quit)
            {
                long due = (long)(sw.Elapsed.TotalSeconds * sampleRate) - produced;
                while (due > 0)
                {
                    int n = (int)Math.Min(due, 4096);
                    Array.Clear(inBuf, 0, n * ch);
                    for (int i = 0; i < n; i++)
                    {
                        long t = produced + i;
                        int ph = (int)(t % period);
                        double env = ph >= on ? 0 : Math.Min(1.0, Math.Min(ph, on - ph) / (double)fade);
                        lp += 0.25 * ((rng.NextDouble() * 2 - 1) - lp);   // soften the top end
                        int speaker = order[(int)(t / period / BurstsPerSpeaker % order.Length)];
                        inBuf[i * ch + speaker] = (float)(lp * env * 0.3);
                    }
                    TrackPeak(inBuf, n * ch, ref _inPeak);
                    TrackChannelPeaks(inBuf, n, ch);
                    dsp.Process(inBuf, n, outBuf);
                    TrackPeak(outBuf, n * 2, ref _outPeak);
                    ring.Write(outBuf, n);
                    produced += n;
                    due -= n;
                }
                Thread.Sleep(5);
            }
        }

        void FeedLoop(IAudioClient client, IAudioRenderClient render, WaveFormat fmt, int channel)
        {
            try
            {
                uint bufferFrames;
                Wasapi.Check(client.GetBufferSize(out bufferFrames), "GetBufferSize");
                int ch = fmt.Channels;
                if (channel < 0 || channel >= ch) channel = 0;
                int targetPad = (int)(fmt.SampleRate * 0.03);
                float[] tmp = new float[(int)bufferFrames * ch];
                Random rng = new Random(2);
                double lp = 0;
                long produced = 0;
                int period = (int)(fmt.SampleRate * 0.30), on = (int)(fmt.SampleRate * 0.18), fade = (int)(fmt.SampleRate * 0.005);
                Wasapi.Check(client.Start(), "feed Start");
                while (!_quit)
                {
                    uint pad;
                    if (client.GetCurrentPadding(out pad) < 0) break;
                    int want = Math.Min((int)bufferFrames - (int)pad, targetPad - (int)pad);
                    if (want > 0)
                    {
                        IntPtr data;
                        if (render.GetBuffer((uint)want, out data) >= 0)
                        {
                            Array.Clear(tmp, 0, want * ch);
                            for (int i = 0; i < want; i++)
                            {
                                int ph = (int)((produced + i) % period);
                                double env = ph >= on ? 0 : Math.Min(1.0, Math.Min(ph, on - ph) / (double)fade);
                                lp += 0.25 * ((rng.NextDouble() * 2 - 1) - lp);
                                tmp[i * ch + channel] = (float)(lp * env * 0.3);
                            }
                            produced += want;
                            Marshal.Copy(tmp, 0, data, want * ch);
                            render.ReleaseBuffer((uint)want, 0);
                        }
                    }
                    Thread.Sleep(2);
                }
                client.Stop();
            }
            catch (Exception) { }
        }

        void RenderLoop(IAudioClient client, IAudioRenderClient render, WaveFormat fmt, int inputRate, StereoRing ring)
        {
            try
            {
                uint bufferFrames;
                Wasapi.Check(client.GetBufferSize(out bufferFrames), "GetBufferSize");
                int ch = fmt.Channels;
                int targetPad = (int)(fmt.SampleRate * 0.03);
                double baseRatio = (double)inputRate / fmt.SampleRate;
                float[] tmp = new float[(int)bufferFrames * ch];
                Wasapi.Check(client.Start(), "render Start");
                while (!_quit)
                {
                    uint pad;
                    Wasapi.Check(client.GetCurrentPadding(out pad), "The headphones");
                    int want = Math.Min((int)bufferFrames - (int)pad, targetPad - (int)pad);
                    if (want > 0)
                    {
                        IntPtr data;
                        Wasapi.Check(render.GetBuffer((uint)want, out data), "The headphones");
                        ring.Read(tmp, want, ch, baseRatio, inputRate);
                        Marshal.Copy(tmp, 0, data, want * ch);
                        render.ReleaseBuffer((uint)want, 0);
                    }
                    Thread.Sleep(2);
                }
            }
            catch (Exception)
            {
                if (!_quit) { _loopFault = "The headphones were disconnected."; _quit = true; }
            }
        }
    }

    /// <summary>Stereo FIFO between the DSP thread and the output thread. The reader
    /// resamples with a slowly adjusted ratio so the two device clocks never drift apart.</summary>
    sealed class StereoRing
    {
        readonly float[] _buf;
        readonly int _cap;
        int _read, _count;
        double _phase;
        double _fillAvg;
        bool _priming = true;
        readonly object _lock = new object();

        public int TargetFrames;
        public int Underruns;
        public double Epsilon;

        public StereoRing(int capacityFrames, int targetFrames)
        {
            _cap = capacityFrames;
            _buf = new float[capacityFrames * 2];
            TargetFrames = targetFrames;
            _fillAvg = targetFrames;
        }

        public int Count { get { lock (_lock) return _count; } }

        public void Write(float[] stereo, int frames)
        {
            lock (_lock)
            {
                for (int i = 0; i < frames; i++)
                {
                    if (_count == _cap)
                    {
                        // Reader stalled: drop the oldest audio back down to the target.
                        int drop = _count - TargetFrames;
                        _read = (_read + drop) % _cap;
                        _count -= drop;
                    }
                    int w = (_read + _count) % _cap;
                    _buf[w * 2] = stereo[i * 2];
                    _buf[w * 2 + 1] = stereo[i * 2 + 1];
                    _count++;
                }
            }
        }

        /// <summary>Fills <paramref name="frames"/> output frames; baseRatio is input rate / output rate.</summary>
        public void Read(float[] dst, int frames, int dstChannels, double baseRatio, int inputRate)
        {
            lock (_lock)
            {
                if (_count > TargetFrames * 3)
                {
                    // Far too much queued (for example the output device took a while to
                    // start): skip ahead rather than carry the delay forever.
                    int drop = _count - TargetFrames;
                    _read = (_read + drop) % _cap;
                    _count -= drop;
                    _fillAvg = _count;
                }
                _fillAvg = 0.99 * _fillAvg + 0.01 * _count;
                double eps = 0.2 * (_fillAvg - TargetFrames) / inputRate;
                if (eps > 0.005) eps = 0.005;
                if (eps < -0.005) eps = -0.005;
                Epsilon = eps;
                double ratio = baseRatio * (1.0 + eps);

                if (_priming && _count >= TargetFrames) { _priming = false; _fillAvg = _count; }

                for (int f = 0; f < frames; f++)
                {
                    float l = 0, r = 0;
                    if (!_priming)
                    {
                        if (_count < 4)
                        {
                            _priming = true;
                            Underruns++;
                        }
                        else
                        {
                            int i0 = _read, i1 = (_read + 1) % _cap, i2 = (_read + 2) % _cap, i3 = (_read + 3) % _cap;
                            float t = (float)_phase;
                            l = Hermite(_buf[i0 * 2], _buf[i1 * 2], _buf[i2 * 2], _buf[i3 * 2], t);
                            r = Hermite(_buf[i0 * 2 + 1], _buf[i1 * 2 + 1], _buf[i2 * 2 + 1], _buf[i3 * 2 + 1], t);
                            _phase += ratio;
                            while (_phase >= 1.0 && _count > 0)
                            {
                                _phase -= 1.0;
                                _read = (_read + 1) % _cap;
                                _count--;
                            }
                        }
                    }
                    int o = f * dstChannels;
                    dst[o] = l;
                    if (dstChannels > 1) dst[o + 1] = r;
                    for (int c = 2; c < dstChannels; c++) dst[o + c] = 0;
                }
            }
        }

        // 4-point Catmull-Rom interpolation between p1 and p2.
        static float Hermite(float p0, float p1, float p2, float p3, float t)
        {
            float a = -0.5f * p0 + 1.5f * p1 - 1.5f * p2 + 0.5f * p3;
            float b = p0 - 2.5f * p1 + 2f * p2 - 0.5f * p3;
            float c = -0.5f * p0 + 0.5f * p2;
            return ((a * t + b) * t + c) * t + p1;
        }
    }

    /// <summary>Offline checks of the renderer's direction cues; no audio devices needed.</summary>
    public static class SelfTest
    {
        static int _failures;

        static bool _measured;

        /// <summary>Converts the MIT KEMAR diffuse.zip archive into the packed HRIR resource.</summary>
        public static int PackHrtf(string zipPath, string outPath)
        {
            return HrtfSet.Pack(zipPath, outPath);
        }

        public static int Run()
        {
            _failures = 0;
            _measured = false;
            Console.WriteLine("== simple head model ==");
            Suite();
            if (HrtfSet.IsAvailable)
            {
                _measured = true;
                Console.WriteLine("== measured ear responses ==");
                Suite();
                FrontBack();
                Speed();
            }
            else Console.WriteLine("(measured ear responses not built in; skipped)");
            Console.WriteLine(_failures == 0 ? "PASS" : "FAIL (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Front and back must differ in tone; the simple model cannot do this.</summary>
        static void FrontBack()
        {
            double[] front = BandLevels(-30), back = BandLevels(-150);
            string[] names = { "0.5-2 kHz", "2-5 kHz", "5-9 kHz", "9-14 kHz" };
            double largest = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 1; i < front.Length; i++)
            {
                // Tone only: compare each band relative to that direction's own low band.
                double diff = (back[i] - back[0]) - (front[i] - front[0]);
                largest = Math.Max(largest, Math.Abs(diff));
                sb.Append(string.Format(CultureInfo.InvariantCulture, "  {0} {1:+0.0;-0.0} dB", names[i], diff));
            }
            bool ok = largest > 2.0;
            if (!ok) _failures++;
            Console.WriteLine("back-left vs front-left tone, left ear:" + sb + "   some band must differ by more than 2 dB  " + (ok ? "ok" : "FAILED"));
        }

        // Left-ear level in four frequency bands (dB) for white noise from one direction.
        static double[] BandLevels(double az)
        {
            const int sr = 48000, n = 19200, win = 1024;
            HrtfBinaural dsp = new HrtfBinaural(sr, SpeakerLayout.Single(az), new FixedHead(), new RenderSettings { Room = 0 });
            Random rng = new Random(7);
            float[] input = new float[n];
            for (int i = 0; i < n; i++) input[i] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            float[] output = new float[n * 2];
            dsp.Process(input, n, output);

            double[] edges = { 500, 2000, 5000, 9000, 14000 };
            double[] energy = new double[edges.Length - 1];
            for (int start = 1024; start + win <= n; start += win)
            {
                for (int band = 0; band < energy.Length; band++)
                {
                    int k0 = (int)(edges[band] * win / sr), k1 = (int)(edges[band + 1] * win / sr);
                    for (int k = k0; k < k1; k += 3)
                    {
                        double re = 0, im = 0, w = 2 * Math.PI * k / win;
                        for (int i = 0; i < win; i++)
                        {
                            double v = output[(start + i) * 2];
                            re += v * Math.Cos(w * i);
                            im -= v * Math.Sin(w * i);
                        }
                        energy[band] += (re * re + im * im) / ((k1 - k0 + 2) / 3);
                    }
                }
            }
            for (int band = 0; band < energy.Length; band++) energy[band] = 10 * Math.Log10(energy[band]);
            return energy;
        }

        /// <summary>How much faster than real time a full 7.1 mix with the room renders.</summary>
        static void Speed()
        {
            const int sr = 48000, n = 48000;
            SpeakerLayout layout = SpeakerLayout.FromMask(8, 0x63F);
            HrtfBinaural dsp = new HrtfBinaural(sr, layout, new FixedHead(), new RenderSettings { Room = 1 });
            Random rng = new Random(7);
            float[] input = new float[n * 8];
            for (int i = 0; i < input.Length; i++) input[i] = (float)(rng.NextDouble() * 2 - 1) * 0.1f;
            float[] output = new float[n * 2];
            dsp.Process(input, 4800, output);   // warm up
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            for (int rep = 0; rep < 5; rep++) dsp.Process(input, n, output);
            double perSecond = sw.Elapsed.TotalSeconds / 5;
            float peak = 0;
            foreach (float v in output) peak = Math.Max(peak, Math.Abs(v));
            bool ok = perSecond < 0.25;
            if (!ok) _failures++;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "7.1 with room: {0:0.0}% of one CPU core, output peak {1:0.00}   must stay under 25%  {2}",
                perSecond * 100, peak, ok ? "ok" : "FAILED"));
        }

        static void Suite()
        {
            Console.WriteLine("case                              ILD (R-L) dB   ITD (R-L) samples   expectation");
            Case("source front, head straight", 0, 0, 0);
            Case("source right 90", 90, 0, 1);
            Case("source left 90", -90, 0, -1);
            Case("source front, head turned right 30", 0, 30, -1);
            Case("source front, head turned left 30", 0, -30, 1);
            Case("source right 30, head turned right 30", 30, 30, 0);
            Case("source behind, head straight", 180, 0, 0);

            // 7.1: every channel must land on its own side.
            SpeakerLayout surround = SpeakerLayout.FromMask(8, 0x63F);
            for (int i = 0; i < surround.Count; i++)
            {
                if (surround.IsLfe[i]) { Channel(surround, i, "7.1 " + surround.Names[i] + " (subwoofer)", 0); continue; }
                double az = surround.Azimuth[i];
                int side = az > 1 && az < 179 ? 1 : (az < -1 && az > -179 ? -1 : 0);
                Channel(surround, i, "7.1 " + surround.Names[i] + " at " + az.ToString("0", CultureInfo.InvariantCulture), side);
            }
        }

        // side: +1 = should be heard on the right, -1 = left, 0 = centred.
        static void Case(string name, double az, double yaw, int side)
        {
            Check(name, SpeakerLayout.Single(az), 0, yaw, side);
        }

        static void Channel(SpeakerLayout layout, int channel, string name, int side)
        {
            Check(name, layout, channel, 0, side);
        }

        static void Check(string name, SpeakerLayout layout, int channel, double yaw, int side)
        {
            const int sr = 48000, n = 9600;
            int ch = layout.Count;
            FixedHead head = new FixedHead();
            head.Yaw = yaw;
            IRenderer dsp = _measured
                ? (IRenderer)new HrtfBinaural(sr, layout, head, new RenderSettings { Room = 0 })
                : new Binaural(sr, layout, head);
            Random rng = new Random(7);
            float[] input = new float[n * ch];
            for (int i = 0; i < n; i++) input[i * ch + channel] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            float[] output = new float[n * 2];
            dsp.Process(input, n, output);

            double eL = 0, eR = 0;
            for (int i = 0; i < n; i++) { eL += output[i * 2] * output[i * 2]; eR += output[i * 2 + 1] * output[i * 2 + 1]; }
            double ild = 10 * Math.Log10(eR / eL);

            // Lag of the right ear relative to the left: negative means the right ear hears it first.
            int bestLag = 0; double best = double.MinValue;
            for (int lag = -48; lag <= 48; lag++)
            {
                double acc = 0;
                for (int i = 64; i < n - 64; i++) acc += output[i * 2] * output[(i + lag) * 2 + 1];
                if (acc > best) { best = acc; bestLag = lag; }
            }

            bool ok;
            string expect;
            if (side > 0) { ok = ild > 3 && bestLag < 0; expect = "right louder and earlier"; }
            else if (side < 0) { ok = ild < -3 && bestLag > 0; expect = "left louder and earlier"; }
            else { ok = Math.Abs(ild) < 0.5 && bestLag == 0; expect = "centred"; }
            if (!ok) _failures++;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-38}{1,8:0.0}{2,16}        {3}  {4}",
                name, ild, bestLag, expect, ok ? "ok" : "FAILED"));
        }
    }
}
