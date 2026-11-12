// Command-line runner for the engine: handy for testing without the app window.
using System;
using System.Globalization;
using System.Threading;
using Soundstage.Engine;

namespace Soundstage.Cli
{
    static class Program
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static volatile bool _quit;

        [MTAThread]
        static int Main(string[] args)
        {
            EngineOptions opt = new EngineOptions();
            bool list = false, selfTest = false;
            double seconds = 0, room = -1;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--list") list = true;
                else if (a == "--selftest") selfTest = true;
                else if (a == "--demo") opt.Demo = true;
                else if (a == "--demo-surround") { opt.Demo = true; opt.DemoMask = 0x63F; }
                else if (a == "--no-tracker") opt.UseTracker = false;
                else if (a == "--simple") opt.SimpleModel = true;
                else if (a == "--sony-probe" && i + 1 < args.Length) return SonyProbe(args[i + 1]);
                else if (a == "--sony")
                {
                    string[] rest = new string[args.Length - i - 1];
                    Array.Copy(args, i + 1, rest, 0, rest.Length);
                    return Sony(rest);
                }
                else if (a == "--room" && i + 1 < args.Length) room = double.Parse(args[++i], Inv);
                else if (a == "--pack-hrtf" && i + 2 < args.Length)
                {
                    int count = SelfTest.PackHrtf(args[i + 1], args[i + 2]);
                    Console.WriteLine("Packed " + count + " measurements into " + args[i + 2]);
                    return 0;
                }
                else if (a == "--feed-test") opt.FeedTest = true;
                else if (a == "--feed-channel" && i + 1 < args.Length) opt.FeedChannel = int.Parse(args[++i], Inv);
                else if (a == "--demo-az" && i + 1 < args.Length) opt.DemoAzimuth = double.Parse(args[++i], Inv);
                else if (a == "--seconds" && i + 1 < args.Length) seconds = double.Parse(args[++i], Inv);
                else if (a == "--source" && i + 1 < args.Length) opt.SourceName = args[++i];
                else if (a == "--output" && i + 1 < args.Length) opt.OutputName = args[++i];
                else if (a == "--tracker" && i + 1 < args.Length) opt.TrackerPath = args[++i];
                else if (a == "--port" && i + 1 < args.Length) opt.Port = int.Parse(args[++i], Inv);
                else if (a == "--help" || a == "-h" || a == "/?") { PrintHelp(); return 0; }
                else { Console.Error.WriteLine("Unknown argument: " + a); PrintHelp(); return 2; }
            }

            if (selfTest) return SelfTest.Run();
            if (list)
            {
                foreach (AudioDeviceInfo d in EngineHost.ListOutputs())
                    Console.WriteLine(d.Name + "\n    now: " + AudioDeviceInfo.LayoutName(d.Channels) + ", " + d.SampleRate +
                                      " Hz, mask 0x" + d.ChannelMask.ToString("X") + "   driver maximum: " +
                                      (d.MaxChannels == 0 ? "unknown" : AudioDeviceInfo.LayoutName(d.MaxChannels)));
                return 0;
            }

            EngineHost host = new EngineHost();
            if (room >= 0) host.Render.Room = (float)room;
            string fault = null;
            host.Log += Console.WriteLine;
            host.Faulted += delegate(string m) { fault = m; _quit = true; };
            host.Start(opt);

            Console.WriteLine("Commands: c=recenter  t=tracking on/off  a=auto-center on/off  y/p/r=flip axis  +/-=prediction  q=quit");
            StartCommandReader(host.Pose);

            DateTime start = DateTime.UtcNow;
            while (!_quit)
            {
                Thread.Sleep(1000);
                if (seconds > 0 && (DateTime.UtcNow - start).TotalSeconds >= seconds) break;
                EngineStatus s = host.GetStatus();
                if (!s.Running) continue;
                Console.WriteLine(string.Format(Inv,
                    "yaw {0,7:0.0}  pitch {1,6:0.0}  roll {2,6:0.0} | tracker {3,4:0.0}/s | in {4,6:0.0} dB  out {5,6:0.0} dB | buf {6,3:0} ms  adj {7,6:0.00}%  underruns {8}",
                    s.Yaw, s.Pitch, s.Roll, s.TrackerRate, s.InputDb, s.OutputDb, s.BufferMs, s.AdjustPercent, s.Underruns));
            }
            host.Stop();
            if (fault != null) { Console.Error.WriteLine("Error: " + fault); return 1; }
            return 0;
        }

        /// <summary>Read-only look at the headphones' control channel: sends only
        /// "get" requests and prints whatever comes back.</summary>
        static int SonyProbe(string addressHex)
        {
            ulong address = ulong.Parse(addressHex.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, Inv);
            using (SonyLink link = new SonyLink())
            {
                link.Received += delegate(SonyMessage m) { Console.WriteLine("    <- " + m); };
                link.Closed += delegate { Console.WriteLine("    (connection closed by the headphones)"); };
                string error = link.Open(address, SonyLink.ServiceV2);
                if (error != null)
                {
                    Console.WriteLine("V2 service: " + error);
                    error = link.Open(address, SonyLink.ServiceV1);
                    if (error != null) { Console.WriteLine("V1 service: " + error); return 1; }
                    Console.WriteLine("Connected on the V1 service.");
                }
                else Console.WriteLine("Connected on the V2 service.");

                string[] names = { "init", "support functions (table 1)", "firmware version", "model name", "battery (single)", "audio codec",
                                   "noise cancelling (15)", "noise cancelling (17)", "noise cancelling (19)", "equalizer",
                                   "auto power off", "speak-to-chat on/off", "speak-to-chat settings", "DSEE", "touch panel" };
                byte[][] gets =
                {
                    new byte[] { 0x00, 0x00 }, new byte[] { 0x06, 0x00 }, new byte[] { 0x04, 0x02 }, new byte[] { 0x04, 0x01 },
                    new byte[] { 0x22, 0x00 }, new byte[] { 0x12, 0x02 },
                    new byte[] { 0x66, 0x15 }, new byte[] { 0x66, 0x17 }, new byte[] { 0x66, 0x19 }, new byte[] { 0x56, 0x00 },
                    new byte[] { 0x26, 0x05 }, new byte[] { 0xF6, 0x0C }, new byte[] { 0xFA, 0x0C }, new byte[] { 0xE6, 0x01 },
                    new byte[] { 0xD6, 0xD1 },
                };
                for (int i = 0; i < gets.Length; i++)
                {
                    if (!link.IsOpen) break;
                    Console.WriteLine("-> " + names[i] + " : " + SonyLink.Hex(gets[i]));
                    bool acked = link.Send(SonyLink.TypeCommand1, gets[i]);
                    if (!acked) Console.WriteLine("    (no acknowledgement)");
                    Thread.Sleep(350);
                }
                Thread.Sleep(800);
            }
            return 0;
        }

        static void PrintState(SonyState s)
        {
            Console.WriteLine("  model " + s.Model + "  firmware " + s.Firmware + "  battery " + s.Battery + "%" + (s.Charging ? " (charging)" : "") + "  codec 0x" + s.Codec.ToString("X2") + "  volume " + s.Volume);
            Console.WriteLine("  noise: " + (s.NoiseControlSupported ? s.Noise + "  ambient level " + s.AmbientLevel + "  voice " + s.FocusOnVoice + "  auto " + s.AutoAmbient + "  sensitivity " + s.AmbientSensitivity : "not supported"));
            Console.WriteLine("  eq: preset 0x" + s.EqPreset.ToString("X2") + "  bands [" + string.Join(" ", s.EqBands) + "]");
            Console.WriteLine("  speak-to-chat " + s.SpeakToChat + " (sensitivity " + s.SpeakToChatSensitivity + ", timeout " + s.SpeakToChatTimeout + ")  dsee " + s.Dsee + "  pause-when-removed " + s.PauseWhenRemoved);
            Console.WriteLine("  auto power off 0x" + s.AutoPowerOff.ToString("X2") + " (accepts: " + string.Join(" ", Array.ConvertAll(s.AutoPowerOffOptions, delegate(int v) { return "0x" + v.ToString("X2"); })) + ")  connection priority " + s.ConnectionPriority);
            foreach (SonySwitch sw in s.Switches) Console.WriteLine("  switch " + sw.Slot.ToString("X2") + " \"" + sw.Name + "\" = " + sw.On);
        }

        /// <summary>soundstage-cli --sony [trace] [command value]... : show, then change, headphone settings.</summary>
        static int Sony(string[] a)
        {
            BluetoothDevice dev = SonyHeadphones.FindHeadphones();
            if (dev == null)
            {
                Console.WriteLine("No connected Sony headphones found. Paired devices:");
                foreach (BluetoothDevice d in SonyHeadphones.PairedDevices()) Console.WriteLine("  " + d.Name + (d.Connected ? " (connected)" : ""));
                return 1;
            }
            Console.WriteLine("Found " + dev.Name + " [" + dev.Address.ToString("X12") + "]");
            using (SonyHeadphones hp = new SonyHeadphones())
            {
                int k = 0;
                if (a.Length > 0 && a[0] == "trace") { hp.Trace += delegate(string s) { Console.WriteLine("    " + s); }; k = 1; }
                string error = hp.Connect(dev.Address);
                if (error != null) { Console.WriteLine(error); return 1; }
                PrintState(hp.State);
                for (; k + 1 < a.Length; k += 2)
                {
                    string cmd = a[k], val = a[k + 1];
                    SonyState s = hp.State;
                    bool ok;
                    switch (cmd)
                    {
                        case "noise": ok = hp.SetNoise(val == "off" ? NoiseMode.Off : val == "ambient" ? NoiseMode.Ambient : NoiseMode.NoiseCancelling, s.AmbientLevel, s.FocusOnVoice, s.AutoAmbient); break;
                        case "level": ok = hp.SetNoise(NoiseMode.Ambient, int.Parse(val, Inv), s.FocusOnVoice, false); break;
                        case "voice": ok = hp.SetNoise(NoiseMode.Ambient, s.AmbientLevel, val == "on", s.AutoAmbient); break;
                        case "auto": ok = hp.SetNoise(NoiseMode.Ambient, s.AmbientLevel, s.FocusOnVoice, val == "on"); break;
                        case "eq": ok = hp.SetEqPreset(int.Parse(val, NumberStyles.HexNumber, Inv)); break;
                        case "bands": ok = hp.SetEqBands(Array.ConvertAll(val.Split(new char[] { ',' }), delegate(string x) { return int.Parse(x, Inv); })); break;
                        case "stc": ok = hp.SetSpeakToChat(val == "on"); break;
                        case "dsee": ok = hp.SetDsee(val == "on"); break;
                        case "pause": ok = hp.SetPauseWhenRemoved(val == "on"); break;
                        case "apo": ok = hp.SetAutoPowerOff(int.Parse(val, NumberStyles.HexNumber, Inv)); break;
                        case "priority": ok = hp.SetConnectionPriority(int.Parse(val, Inv)); break;
                        case "volume": ok = hp.SetVolume(int.Parse(val, Inv)); break;
                        case "switch": ok = hp.SetSwitch(byte.Parse(val.Split(new char[] { '=' })[0], NumberStyles.HexNumber, Inv), val.EndsWith("=on")); break;
                        case "wait": Thread.Sleep(int.Parse(val, Inv)); ok = true; break;
                        default: Console.WriteLine("unknown command " + cmd); return 2;
                    }
                    Thread.Sleep(700);
                    Console.WriteLine("> " + cmd + " " + val + (ok ? "  (acknowledged)" : "  (NOT acknowledged)"));
                    PrintState(hp.State);
                }
            }
            return 0;
        }

        static void PrintHelp()
        {
            Console.WriteLine(
                "soundstage-cli - head-tracked binaural rendering for Windows\n\n" +
                "  --list              list playback devices and their formats\n" +
                "  --source NAME       device to capture (default: Steam Streaming Speakers)\n" +
                "  --output NAME       headphones to play on (default: WH-1000XM6)\n" +
                "  --tracker PATH      path to sony-head-tracker.exe (auto-detected if omitted)\n" +
                "  --port N            tracker bridge port (default 4242; JSON arrives on N+1)\n" +
                "  --no-tracker        do not start or listen for the tracker\n" +
                "  --demo              ignore the source device and play test noise from the front\n" +
                "  --demo-az DEG       direction of the demo noise (default 0 = front)\n" +
                "  --demo-surround     play test noise from each 7.1 speaker in turn\n" +
                "  --feed-test         play test noise into the source device (checks the full chain)\n" +
                "  --feed-channel N    source channel the test noise goes to (default 0)\n" +
                "  --simple            use the mathematical head model instead of measured ear responses\n" +
                "  --room X            room simulation amount, 0 to 1 (default 0.35)\n" +
                "  --pack-hrtf ZIP OUT convert the MIT KEMAR diffuse.zip into the packed resource\n" +
                "  --seconds N         exit after N seconds\n" +
                "  --selftest          run the offline renderer checks and exit");
        }

        static void StartCommandReader(HeadPose pose)
        {
            Thread t = new Thread(delegate()
            {
                bool redirected = Console.IsInputRedirected;
                while (!_quit)
                {
                    string cmd;
                    if (redirected)
                    {
                        cmd = Console.ReadLine();
                        if (cmd == null) return;   // stdin closed: keep running without commands
                    }
                    else
                    {
                        if (!Console.KeyAvailable) { Thread.Sleep(50); continue; }
                        cmd = Console.ReadKey(true).KeyChar.ToString();
                    }
                    cmd = cmd.Trim().ToLowerInvariant();
                    if (cmd.Length == 0) continue;
                    switch (cmd[0])
                    {
                        case 'q': _quit = true; break;
                        case 'c': pose.Recenter(); Console.WriteLine("> recentered"); break;
                        case 't': pose.Enabled = !pose.Enabled; Console.WriteLine("> tracking " + (pose.Enabled ? "on" : "off")); break;
                        case 'a': pose.AutoCenter = !pose.AutoCenter; Console.WriteLine("> auto-center " + (pose.AutoCenter ? "on" : "off")); break;
                        case 'y': case 'p': case 'r':
                            pose.FlipSign(cmd[0]);
                            Console.WriteLine("> signs: yaw " + pose.YawSign + "  pitch " + pose.PitchSign + "  roll " + pose.RollSign);
                            break;
                        case '+': case '=':
                            pose.PredictSeconds = Math.Min(0.25, pose.PredictSeconds + 0.02);
                            Console.WriteLine("> prediction " + (pose.PredictSeconds * 1000).ToString("0", Inv) + " ms");
                            break;
                        case '-':
                            pose.PredictSeconds = Math.Max(0.0, pose.PredictSeconds - 0.02);
                            Console.WriteLine("> prediction " + (pose.PredictSeconds * 1000).ToString("0", Inv) + " ms");
                            break;
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "commands";
            t.Start();
        }
    }
}
