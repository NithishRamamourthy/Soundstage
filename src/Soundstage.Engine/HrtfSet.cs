// Measured head-related impulse responses (HRIRs) and their interpolation.
//
// The bundled data is the MIT Media Lab KEMAR dummy-head set (Gardner & Martin,
// 1994), diffuse-field equalized, packed into one small resource by Pack().
// On load every response is resampled to the engine rate and split into a pure
// delay (the interaural time difference) and a time-aligned response; the two
// are interpolated separately, which avoids the comb filtering you get from
// blending responses that arrive at different times.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Soundstage.Engine
{
    sealed class HrtfSet
    {
        const string ResourceName = "Soundstage.Engine.Assets.mit_kemar_diffuse.hrir";

        sealed class Ring
        {
            public double Elevation;
            public double[] Az;          // ascending, 0..180 (right-hand side; the left is mirrored)
            public float[][] L, R;       // aligned, time-reversed responses
            public float[] DelayL, DelayR;
        }

        Ring[] _rings;

        /// <summary>Length of every aligned response, in samples at SampleRate.</summary>
        public int Taps { get; private set; }
        public int SampleRate { get; private set; }

        public static bool IsAvailable
        {
            get { return typeof(HrtfSet).Assembly.GetManifestResourceInfo(ResourceName) != null; }
        }

        // ------------------------------------------------------------ loading

        public static HrtfSet LoadEmbedded(int sampleRate)
        {
            using (Stream s = typeof(HrtfSet).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidOperationException("The HRTF data is missing from this build.");
                return Load(s, sampleRate);
            }
        }

        static HrtfSet Load(Stream stream, int sampleRate)
        {
            BinaryReader br = new BinaryReader(stream);
            if (new string(br.ReadChars(4)) != "SSHR") throw new InvalidDataException("Not an HRIR pack.");
            int version = br.ReadUInt16();
            if (version != 1) throw new InvalidDataException("Unsupported HRIR pack version.");
            int dataRate = (int)br.ReadUInt32();
            int dataTaps = br.ReadUInt16();
            int ringCount = br.ReadUInt16();

            double ratio = (double)sampleRate / dataRate;
            int taps = ((int)Math.Ceiling(100 * ratio) + 7) / 8 * 8;   // keep ~2.3 ms after the onset
            int preRoll = (int)Math.Round(6 * ratio);

            HrtfSet set = new HrtfSet();
            set.SampleRate = sampleRate;
            set.Taps = taps;
            set._rings = new Ring[ringCount];

            float minDelay = float.MaxValue;
            for (int r = 0; r < ringCount; r++)
            {
                Ring ring = new Ring();
                ring.Elevation = br.ReadInt16();
                int n = br.ReadUInt16();
                ring.Az = new double[n];
                ring.L = new float[n][]; ring.R = new float[n][];
                ring.DelayL = new float[n]; ring.DelayR = new float[n];
                for (int i = 0; i < n; i++)
                {
                    ring.Az[i] = br.ReadUInt16();
                    float[] l = new float[dataTaps], rr = new float[dataTaps];
                    for (int t = 0; t < dataTaps; t++)
                    {
                        l[t] = br.ReadInt16() / 32768f;
                        rr[t] = br.ReadInt16() / 32768f;
                    }
                    ring.L[i] = Align(Resample(l, ratio), taps, preRoll, out ring.DelayL[i]);
                    ring.R[i] = Align(Resample(rr, ratio), taps, preRoll, out ring.DelayR[i]);
                    minDelay = Math.Min(minDelay, Math.Min(ring.DelayL[i], ring.DelayR[i]));
                }
                set._rings[r] = ring;
            }
            Array.Sort(set._rings, delegate(Ring a, Ring b) { return a.Elevation.CompareTo(b.Elevation); });

            // Normalize so a source straight ahead has unity broadband gain, and make
            // the smallest delay zero.
            Ring level = set._rings[0];
            foreach (Ring ring in set._rings) if (Math.Abs(ring.Elevation) < Math.Abs(level.Elevation)) level = ring;
            double energy = 0;
            foreach (float v in level.L[0]) energy += v * v;
            float norm = (float)(1.0 / Math.Sqrt(Math.Max(energy, 1e-12)));
            foreach (Ring ring in set._rings)
            {
                for (int i = 0; i < ring.Az.Length; i++)
                {
                    Scale(ring.L[i], norm); Scale(ring.R[i], norm);
                    ring.DelayL[i] -= minDelay; ring.DelayR[i] -= minDelay;
                }
            }
            return set;
        }

        static void Scale(float[] a, float g)
        {
            for (int i = 0; i < a.Length; i++) a[i] *= g;
        }

        /// <summary>Windowed-sinc resampling of an impulse response (upsampling only).</summary>
        static float[] Resample(float[] x, double ratio)
        {
            if (ratio < 1.0001) return x;
            const int Half = 12;
            int n = (int)Math.Ceiling(x.Length * ratio);
            float[] y = new float[n];
            for (int m = 0; m < n; m++)
            {
                double t = m / ratio;
                int c = (int)Math.Floor(t);
                double acc = 0;
                for (int k = c - Half + 1; k <= c + Half; k++)
                {
                    if (k < 0 || k >= x.Length) continue;
                    double d = t - k;
                    double sinc = Math.Abs(d) < 1e-9 ? 1.0 : Math.Sin(Math.PI * d) / (Math.PI * d);
                    double window = 0.5 + 0.5 * Math.Cos(Math.PI * d / Half);
                    acc += x[k] * sinc * window;
                }
                y[m] = (float)(acc / ratio);   // more samples per second, so each carries less
            }
            return y;
        }

        /// <summary>Removes the leading delay and returns the response time-reversed
        /// (ready for a dot product against a history buffer).</summary>
        static float[] Align(float[] h, int taps, int preRoll, out float delay)
        {
            float peak = 0;
            for (int i = 0; i < h.Length; i++) peak = Math.Max(peak, Math.Abs(h[i]));
            int onset = 0;
            for (int i = 0; i < h.Length; i++)
            {
                if (Math.Abs(h[i]) >= 0.15f * peak) { onset = i; break; }
            }
            int start = Math.Max(0, onset - preRoll);
            delay = start;
            float[] rev = new float[taps];
            for (int k = 0; k < taps; k++)
            {
                int src = start + k;
                float v = src < h.Length ? h[src] : 0f;
                // Fade the last few samples so truncation does not ring.
                int fromEnd = taps - 1 - k;
                if (fromEnd < 8) v *= fromEnd / 8f;
                rev[taps - 1 - k] = v;
            }
            return rev;
        }

        // ------------------------------------------------------ interpolation

        /// <summary>Writes the (time-reversed) left and right responses for a direction in
        /// the head's frame, plus each ear's delay in samples.</summary>
        public void Interpolate(double azDeg, double elDeg, float[] hL, float[] hR, out double delayL, out double delayR)
        {
            azDeg -= 360.0 * Math.Round(azDeg / 360.0);
            bool mirrored = azDeg < 0;
            double a = Math.Abs(azDeg);

            int last = _rings.Length - 1;
            double el = Math.Max(_rings[0].Elevation, Math.Min(_rings[last].Elevation, elDeg));
            int r0 = 0;
            while (r0 < last - 1 && _rings[r0 + 1].Elevation <= el) r0++;
            int r1 = Math.Min(r0 + 1, last);
            double fe = r1 == r0 ? 0 : (el - _rings[r0].Elevation) / (_rings[r1].Elevation - _rings[r0].Elevation);
            fe = Math.Max(0, Math.Min(1, fe));

            Array.Clear(hL, 0, Taps);
            Array.Clear(hR, 0, Taps);
            double dl = 0, dr = 0;
            AddRing(_rings[r0], a, mirrored, (float)(1 - fe), hL, hR, ref dl, ref dr);
            if (fe > 1e-6) AddRing(_rings[r1], a, mirrored, (float)fe, hL, hR, ref dl, ref dr);
            delayL = dl; delayR = dr;
        }

        void AddRing(Ring ring, double a, bool mirrored, float weight, float[] hL, float[] hR, ref double dl, ref double dr)
        {
            int n = ring.Az.Length;
            if (n == 1) { Add(ring, 0, mirrored, weight, hL, hR, ref dl, ref dr); return; }

            int i = 0;
            while (i < n - 1 && ring.Az[i + 1] <= a) i++;
            if (i == n - 1)
            {
                // Past the last stored azimuth: the next neighbour is its mirror image
                // on the other side of 180 degrees.
                double span = 2 * (180.0 - ring.Az[i]);
                if (span < 1e-6) { Add(ring, i, mirrored, weight, hL, hR, ref dl, ref dr); return; }
                float t = (float)Math.Max(0, Math.Min(1, (a - ring.Az[i]) / span));
                Add(ring, i, mirrored, weight * (1 - t), hL, hR, ref dl, ref dr);
                Add(ring, i, !mirrored, weight * t, hL, hR, ref dl, ref dr);
            }
            else
            {
                float t = (float)((a - ring.Az[i]) / (ring.Az[i + 1] - ring.Az[i]));
                Add(ring, i, mirrored, weight * (1 - t), hL, hR, ref dl, ref dr);
                Add(ring, i + 1, mirrored, weight * t, hL, hR, ref dl, ref dr);
            }
        }

        void Add(Ring ring, int i, bool swap, float w, float[] hL, float[] hR, ref double dl, ref double dr)
        {
            if (w <= 0) return;
            float[] srcL = swap ? ring.R[i] : ring.L[i];
            float[] srcR = swap ? ring.L[i] : ring.R[i];
            for (int k = 0; k < Taps; k++) { hL[k] += w * srcL[k]; hR[k] += w * srcR[k]; }
            dl += w * (swap ? ring.DelayR[i] : ring.DelayL[i]);
            dr += w * (swap ? ring.DelayL[i] : ring.DelayR[i]);
        }

        // ------------------------------------------------------------ packing

        /// <summary>Converts the MIT KEMAR "diffuse.zip" archive (stereo WAVs named
        /// elevNN/HNNeAAAa.wav) into the packed resource this class loads.</summary>
        public static int Pack(string zipPath, string outPath)
        {
            Regex name = new Regex(@"elev(-?\d+)/H-?\d+e(\d+)a\.wav$", RegexOptions.IgnoreCase);
            SortedDictionary<int, SortedDictionary<int, short[]>> rings = new SortedDictionary<int, SortedDictionary<int, short[]>>();
            int rate = 0, taps = 0, count = 0;
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    Match m = name.Match(entry.FullName.Replace('\\', '/'));
                    if (!m.Success) continue;
                    byte[] bytes;
                    using (Stream s = entry.Open())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    int fileRate;
                    short[] samples = ReadStereoWav(bytes, out fileRate);
                    if (rate == 0) { rate = fileRate; taps = samples.Length / 2; }
                    if (fileRate != rate || samples.Length / 2 != taps)
                        throw new InvalidDataException("Inconsistent WAV format in " + entry.FullName);
                    int el = int.Parse(m.Groups[1].Value), az = int.Parse(m.Groups[2].Value);
                    if (!rings.ContainsKey(el)) rings[el] = new SortedDictionary<int, short[]>();
                    rings[el][az] = samples;
                    count++;
                }
            }
            if (count == 0) throw new InvalidDataException("No HRIR files found in " + zipPath);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            using (BinaryWriter bw = new BinaryWriter(File.Create(outPath)))
            {
                bw.Write(new char[] { 'S', 'S', 'H', 'R' });
                bw.Write((ushort)1);
                bw.Write((uint)rate);
                bw.Write((ushort)taps);
                bw.Write((ushort)rings.Count);
                foreach (KeyValuePair<int, SortedDictionary<int, short[]>> ring in rings)
                {
                    bw.Write((short)ring.Key);
                    bw.Write((ushort)ring.Value.Count);
                    foreach (KeyValuePair<int, short[]> az in ring.Value)
                    {
                        bw.Write((ushort)az.Key);
                        foreach (short v in az.Value) bw.Write(v);
                    }
                }
            }
            return count;
        }

        static short[] ReadStereoWav(byte[] b, out int rate)
        {
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
                throw new InvalidDataException("Not a RIFF file.");
            rate = 0;
            int channels = 0, bits = 0;
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int size = BitConverter.ToInt32(b, p + 4);
                int body = p + 8;
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    if (channels != 2 || bits != 16) throw new InvalidDataException("Expected 16-bit stereo.");
                    size = Math.Min(size, b.Length - body);
                    short[] samples = new short[size / 2];
                    Buffer.BlockCopy(b, body, samples, 0, samples.Length * 2);
                    return samples;
                }
                p = body + size + (size & 1);
            }
            throw new InvalidDataException("No data chunk.");
        }
    }
}
