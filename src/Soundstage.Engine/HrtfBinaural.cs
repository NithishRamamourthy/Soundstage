// Head-tracked binaural renderer using measured ear responses, with an optional
// light room: six early reflections rendered through the same ear responses (so
// they also stay fixed in the room as the head turns) and a short diffuse tail.
using System;
using System.Numerics;

namespace Soundstage.Engine
{
    /// <summary>Anything that turns interleaved speaker channels into interleaved stereo.</summary>
    interface IRenderer
    {
        void Process(float[] input, int frames, float[] output);
    }

    /// <summary>Settings that can change while audio is running.</summary>
    public sealed class RenderSettings
    {
        volatile float _room = 0.35f;
        /// <summary>Amount of room simulation, 0 (none) to 1.</summary>
        public float Room
        {
            get { return _room; }
            set { _room = value < 0 ? 0 : (value > 1 ? 1 : value); }
        }
    }

    sealed class HrtfBinaural : IRenderer
    {
        const int LineLen = 4096;
        const int LineMask = LineLen - 1;
        const double MinDelay = 2.0;   // keeps the 4-point delay interpolation causal

        sealed class Source
        {
            public double Az, El;
            public double ExtraDelay;          // samples; the reflection's longer path
            public bool Reflection;
            public float[] Line = new float[LineLen];
            public double DelayL, DelayR, IncL, IncR;
            public float[] HistL, HistR, HL, HR;
        }

        // Early reflections: direction (azimuth, elevation) and arrival delay in ms.
        static readonly double[,] Reflections =
        {
            { 0, -40, 5.3 },    // floor
            { 0, 65, 6.1 },     // ceiling
            { -90, 0, 7.9 },    // left wall
            { 90, 0, 8.9 },     // right wall
            { 0, 10, 11.3 },    // front wall
            { 180, 0, 15.1 },   // back wall
        };
        const float ReflectionGain = 0.4f;
        const float TailGain = 0.10f;

        readonly int _sr, _block, _taps;
        readonly SpeakerLayout _layout;
        readonly IHeadProvider _head;
        readonly RenderSettings _settings;
        readonly HrtfSet _set;

        readonly Source[] _speakers;      // by channel; null for the subwoofer channel
        readonly Source[] _reflections;
        readonly float[][] _reflMix;      // [reflection][channel]
        readonly float[] _reflLp;
        readonly float _reflLpCoef;

        readonly float[] _mixL, _mixR, _lfe, _mono;
        int _pos, _blockLeft;
        bool _primed;
        float _room;
        bool _roomWasOn;

        // Diffuse tail: a 4-line feedback delay network.
        readonly float[][] _fdn = new float[4][];
        readonly int[] _fdnPos = new int[4];
        readonly float[] _fdnGain = new float[4];
        readonly float[] _fdnLp = new float[4];
        readonly float[] _preDelay;
        int _prePos;

        public double SpeakerGain = 0.6;
        public double LfeGain = 0.5;

        public HrtfBinaural(int sampleRate, SpeakerLayout layout, IHeadProvider head, RenderSettings settings)
        {
            _sr = sampleRate;
            _layout = layout;
            _head = head;
            _settings = settings;
            _set = HrtfSet.LoadEmbedded(sampleRate);
            _taps = _set.Taps;
            _block = Math.Max(16, (int)Math.Round(sampleRate / 1500.0));

            _speakers = new Source[layout.Count];
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout.IsLfe[i]) continue;
                _speakers[i] = NewSource(layout.Azimuth[i], layout.Elevation[i], 0, false);
            }

            int rc = Reflections.GetLength(0);
            _reflections = new Source[rc];
            _reflMix = new float[rc][];
            _reflLp = new float[rc];
            _reflLpCoef = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 5000.0 / sampleRate));
            for (int r = 0; r < rc; r++)
            {
                _reflections[r] = NewSource(Reflections[r, 0], Reflections[r, 1], Reflections[r, 2] * 0.001 * sampleRate, true);
                _reflMix[r] = new float[layout.Count];
            }
            // How much of each speaker feeds each reflection: mostly the wall it faces.
            for (int sp = 0; sp < layout.Count; sp++)
            {
                if (layout.IsLfe[sp]) continue;
                double sum = 0;
                double[] w = new double[rc];
                for (int r = 0; r < rc; r++)
                {
                    w[r] = 0.25 + Math.Max(0.0, CosBetween(layout.Azimuth[sp], layout.Elevation[sp], Reflections[r, 0], Reflections[r, 1]));
                    sum += w[r] * w[r];
                }
                double norm = 1.0 / Math.Sqrt(sum);
                for (int r = 0; r < rc; r++) _reflMix[r][sp] = (float)(w[r] * norm);
            }

            _mixL = new float[_block]; _mixR = new float[_block];
            _lfe = new float[_block]; _mono = new float[_block];

            int[] lengths = { 1031, 1327, 1523, 1801 };   // samples at 48 kHz, mutually prime
            const double Rt60 = 0.35;
            for (int i = 0; i < 4; i++)
            {
                int len = (int)(lengths[i] * (sampleRate / 48000.0)) | 1;
                _fdn[i] = new float[len];
                _fdnGain[i] = (float)Math.Pow(10.0, -3.0 * len / (Rt60 * sampleRate));
            }
            _preDelay = new float[(int)(0.020 * sampleRate)];
        }

        Source NewSource(double az, double el, double extraDelay, bool reflection)
        {
            Source s = new Source();
            s.Az = az; s.El = el; s.ExtraDelay = extraDelay; s.Reflection = reflection;
            s.HistL = new float[_taps - 1 + _block];
            s.HistR = new float[_taps - 1 + _block];
            s.HL = new float[_taps];
            s.HR = new float[_taps];
            return s;
        }

        static double CosBetween(double az1, double el1, double az2, double el2)
        {
            const double D = Math.PI / 180.0;
            double x1 = Math.Sin(az1 * D) * Math.Cos(el1 * D), y1 = Math.Sin(el1 * D), z1 = Math.Cos(az1 * D) * Math.Cos(el1 * D);
            double x2 = Math.Sin(az2 * D) * Math.Cos(el2 * D), y2 = Math.Sin(el2 * D), z2 = Math.Cos(az2 * D) * Math.Cos(el2 * D);
            return x1 * x2 + y1 * y2 + z1 * z2;
        }

        void UpdateTargets(bool snap)
        {
            double yaw, pitch, roll;
            _head.GetPose((double)_block / _sr, out yaw, out pitch, out roll);

            float targetRoom = _settings.Room;
            _room = snap ? targetRoom : _room + (targetRoom - _room) * 0.05f;
            if (targetRoom == 0 && _room < 0.001f) _room = 0;
            bool roomOn = _room > 0;

            foreach (Source s in _speakers) if (s != null) Aim(s, yaw, pitch, roll, snap);
            if (roomOn)
            {
                foreach (Source s in _reflections) Aim(s, yaw, pitch, roll, snap || !_roomWasOn);
            }
            else if (_roomWasOn)
            {
                // Room just switched off: forget the old tail so it cannot replay later.
                foreach (Source s in _reflections) { Array.Clear(s.HistL, 0, s.HistL.Length); Array.Clear(s.HistR, 0, s.HistR.Length); }
                for (int i = 0; i < 4; i++) { Array.Clear(_fdn[i], 0, _fdn[i].Length); _fdnLp[i] = 0; }
                Array.Clear(_preDelay, 0, _preDelay.Length);
            }
            _roomWasOn = roomOn;
        }

        void Aim(Source s, double yaw, double pitch, double roll, bool snap)
        {
            double hx, hy, hz;
            Binaural.ToHeadFrame(s.Az, s.El, yaw, pitch, roll, out hx, out hy, out hz);
            double az = Math.Atan2(hx, hz) * 180.0 / Math.PI;
            double el = Math.Asin(Math.Max(-1.0, Math.Min(1.0, hy))) * 180.0 / Math.PI;
            double dl, dr;
            _set.Interpolate(az, el, s.HL, s.HR, out dl, out dr);
            dl += MinDelay + s.ExtraDelay;
            dr += MinDelay + s.ExtraDelay;
            if (snap)
            {
                s.DelayL = dl; s.DelayR = dr; s.IncL = 0; s.IncR = 0;
            }
            else
            {
                s.IncL = (dl - s.DelayL) / _block;
                s.IncR = (dr - s.DelayR) / _block;
            }
        }

        public void Process(float[] input, int frames, float[] output)
        {
            int done = 0;
            while (done < frames)
            {
                if (_blockLeft == 0)
                {
                    UpdateTargets(!_primed);
                    _primed = true;
                    _blockLeft = _block;
                }
                int n = Math.Min(frames - done, _blockLeft);
                RenderChunk(input, done, n, output);
                done += n;
                _blockLeft -= n;
            }
        }

        void RenderChunk(float[] input, int offset, int n, float[] output)
        {
            int ch = _layout.Count;
            float spkGain = (float)SpeakerGain, lfeGain = (float)LfeGain;
            bool roomOn = _room > 0;
            Array.Clear(_mixL, 0, n);
            Array.Clear(_mixR, 0, n);

            // 1. Feed the delay lines.
            for (int f = 0; f < n; f++)
            {
                int inBase = (offset + f) * ch;
                int p = (_pos + f) & LineMask;
                float lfe = 0, mono = 0;
                for (int sp = 0; sp < ch; sp++)
                {
                    float x = input[inBase + sp];
                    Source s = _speakers[sp];
                    if (s == null) { lfe += x * lfeGain; continue; }
                    x *= spkGain;
                    s.Line[p] = x;
                    mono += x;
                }
                _lfe[f] = lfe;
                _mono[f] = mono;
                for (int r = 0; r < _reflections.Length; r++)
                {
                    float v = 0;
                    float[] mix = _reflMix[r];
                    for (int sp = 0; sp < ch; sp++)
                    {
                        if (_speakers[sp] != null) v += mix[sp] * _speakers[sp].Line[p];
                    }
                    _reflLp[r] += _reflLpCoef * (v - _reflLp[r]);   // walls absorb the top end
                    _reflections[r].Line[p] = _reflLp[r];
                }
            }

            // 2. Each source: per-ear delay, then the ear response.
            foreach (Source s in _speakers) if (s != null) RenderSource(s, n, 1f);
            if (roomOn)
            {
                float g = ReflectionGain * _room;
                foreach (Source s in _reflections) RenderSource(s, n, g);
            }
            else
            {
                foreach (Source s in _reflections) { s.DelayL += s.IncL * n; s.DelayR += s.IncR * n; }
            }

            // 3. Diffuse tail, subwoofer and output.
            float tail = TailGain * _room;
            for (int f = 0; f < n; f++)
            {
                float l = _mixL[f] + _lfe[f], r = _mixR[f] + _lfe[f];
                if (roomOn)
                {
                    float fed = _preDelay[_prePos];
                    _preDelay[_prePos] = _mono[f];
                    if (++_prePos == _preDelay.Length) _prePos = 0;

                    float y0 = Tap(0), y1 = Tap(1), y2 = Tap(2), y3 = Tap(3);
                    float a = y0 + y1, b = y0 - y1, c = y2 + y3, d = y2 - y3;
                    Push(0, fed + _fdnGain[0] * 0.5f * (a + c));
                    Push(1, fed + _fdnGain[1] * 0.5f * (b + d));
                    Push(2, fed + _fdnGain[2] * 0.5f * (a - c));
                    Push(3, fed + _fdnGain[3] * 0.5f * (b - d));
                    l += tail * (y0 + y2);
                    r += tail * (y1 + y3);
                }
                int o = (offset + f) * 2;
                output[o] = Limit(l);
                output[o + 1] = Limit(r);
            }
            _pos = (_pos + n) & LineMask;
        }

        float Tap(int i)
        {
            float y = _fdn[i][_fdnPos[i]];
            _fdnLp[i] += 0.45f * (y - _fdnLp[i]);   // high frequencies die away faster
            return _fdnLp[i];
        }

        void Push(int i, float v)
        {
            _fdn[i][_fdnPos[i]] = v;
            if (++_fdnPos[i] == _fdn[i].Length) _fdnPos[i] = 0;
        }

        void RenderSource(Source s, int n, float gain)
        {
            int h = _taps - 1;
            float[] line = s.Line;
            for (int f = 0; f < n; f++)
            {
                int p = _pos + f;
                s.HistL[h + f] = ReadDelayed(line, p, s.DelayL);
                s.HistR[h + f] = ReadDelayed(line, p, s.DelayR);
                s.DelayL += s.IncL;
                s.DelayR += s.IncR;
            }
            for (int f = 0; f < n; f++)
            {
                _mixL[f] += gain * Dot(s.HL, s.HistL, f, _taps);
                _mixR[f] += gain * Dot(s.HR, s.HistR, f, _taps);
            }
            Array.Copy(s.HistL, n, s.HistL, 0, h);
            Array.Copy(s.HistR, n, s.HistR, 0, h);
        }

        /// <summary>Reads the line at a fractional delay with 4-point interpolation.</summary>
        static float ReadDelayed(float[] line, int pos, double delay)
        {
            int whole = (int)delay;
            float t = (float)(delay - whole);
            int b = pos - whole;
            float p0 = line[(b + 1) & LineMask], p1 = line[b & LineMask], p2 = line[(b - 1) & LineMask], p3 = line[(b - 2) & LineMask];
            float a = -0.5f * p0 + 1.5f * p1 - 1.5f * p2 + 0.5f * p3;
            float bb = p0 - 2.5f * p1 + 2f * p2 - 0.5f * p3;
            float c = -0.5f * p0 + 0.5f * p2;
            return ((a * t + bb) * t + c) * t + p1;
        }

        static float Dot(float[] a, float[] b, int bOffset, int length)
        {
            Vector<float> acc = Vector<float>.Zero;
            int width = Vector<float>.Count;
            int i = 0;
            for (; i <= length - width; i += width)
                acc += new Vector<float>(a, i) * new Vector<float>(b, bOffset + i);
            float sum = Vector.Sum(acc);
            for (; i < length; i++) sum += a[i] * b[bOffset + i];
            return sum;
        }

        /// <summary>Transparent below 0.8, then eases smoothly toward full scale.</summary>
        static float Limit(float v)
        {
            float a = Math.Abs(v);
            if (a <= 0.8f) return v;
            float y = 0.8f + 0.2f * (float)Math.Tanh((a - 0.8f) / 0.2f);
            return v < 0 ? -y : y;
        }
    }
}
