// Head-tracked binaural renderer.
//
// Each input channel is treated as a virtual loudspeaker fixed in the room. For
// every speaker and each ear we synthesize the direction-dependent cues with a
// structural head model (Brown & Duda, 1998): interaural time delay, a head
// shadow filter and a few pinna echoes. The cues are recomputed from the head
// pose every ControlBlock samples and ramped per sample, so head movement never
// produces clicks.
using System;

namespace Soundstage.Engine
{
    /// <summary>Supplies the head pose (degrees) for a given moment in the audio stream.</summary>
    public interface IHeadProvider
    {
        void GetPose(double dt, out double yaw, out double pitch, out double roll);
    }

    sealed class FixedHead : IHeadProvider
    {
        public double Yaw, Pitch, Roll;
        public void GetPose(double dt, out double yaw, out double pitch, out double roll)
        {
            yaw = Yaw; pitch = Pitch; roll = Roll;
        }
    }

    public sealed class SpeakerLayout
    {
        public double[] Azimuth;   // degrees, positive to the right
        public double[] Elevation; // degrees, positive up
        public bool[] IsLfe;
        public string[] Names;

        public int Count { get { return Azimuth.Length; } }

        public static SpeakerLayout Single(double azimuthDeg)
        {
            SpeakerLayout l = new SpeakerLayout();
            l.Azimuth = new double[] { azimuthDeg };
            l.Elevation = new double[] { 0 };
            l.IsLfe = new bool[] { false };
            l.Names = new string[] { "SRC" };
            return l;
        }

        /// <summary>Builds a layout from a WASAPI channel count and channel mask.</summary>
        public static SpeakerLayout FromMask(int channels, uint mask)
        {
            if (mask == 0)
            {
                if (channels == 1) mask = 0x4;
                else if (channels == 2) mask = 0x3;
                else if (channels == 4) mask = 0x33;
                else if (channels == 6) mask = 0x3F;
                else if (channels == 8) mask = 0x63F;
            }
            bool hasSide = (mask & 0x600) != 0;
            bool hasBack = (mask & 0x30) != 0;

            SpeakerLayout l = new SpeakerLayout();
            l.Azimuth = new double[channels];
            l.Elevation = new double[channels];
            l.IsLfe = new bool[channels];
            l.Names = new string[channels];

            int ch = 0;
            for (int bit = 0; bit < 18 && ch < channels; bit++)
            {
                uint b = 1u << bit;
                if ((mask & b) == 0) continue;
                double az = 0, el = 0;
                bool lfe = false;
                string name;
                switch (b)
                {
                    case 0x1: az = -30; name = "FL"; break;
                    case 0x2: az = 30; name = "FR"; break;
                    case 0x4: az = 0; name = "FC"; break;
                    case 0x8: lfe = true; name = "LFE"; break;
                    case 0x10: az = hasSide ? -150 : -110; name = "BL"; break;
                    case 0x20: az = hasSide ? 150 : 110; name = "BR"; break;
                    case 0x40: az = -15; name = "FLC"; break;
                    case 0x80: az = 15; name = "FRC"; break;
                    case 0x100: az = 180; name = "BC"; break;
                    case 0x200: az = hasBack ? -90 : -110; name = "SL"; break;
                    case 0x400: az = hasBack ? 90 : 110; name = "SR"; break;
                    case 0x800: el = 90; name = "TC"; break;
                    case 0x1000: az = -45; el = 45; name = "TFL"; break;
                    case 0x2000: az = 0; el = 45; name = "TFC"; break;
                    case 0x4000: az = 45; el = 45; name = "TFR"; break;
                    case 0x8000: az = -135; el = 45; name = "TBL"; break;
                    case 0x10000: az = 180; el = 45; name = "TBC"; break;
                    default: az = 135; el = 45; name = "TBR"; break;
                }
                l.Azimuth[ch] = az;
                l.Elevation[ch] = el;
                l.IsLfe[ch] = lfe;
                l.Names[ch] = name;
                ch++;
            }
            // Channels the mask does not describe: play them from the front.
            for (; ch < channels; ch++) l.Names[ch] = "CH" + ch;
            return l;
        }

        public override string ToString()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Names[i]);
                if (!IsLfe[i]) sb.Append('(').Append(Azimuth[i].ToString("0")).Append(')');
            }
            return sb.ToString();
        }
    }

    sealed class Binaural : IRenderer
    {
        public const int ControlBlock = 32;

        const int Taps = 6;            // direct path + 5 pinna echoes
        const int DelayLen = 512;      // power of two, far above the longest delay used
        const int DelayMask = DelayLen - 1;
        const double HeadRadius = 0.0875;   // metres
        const double SpeedOfSound = 343.0;  // m/s

        // Pinna echo model parameters (Brown & Duda), delays in samples at 44.1 kHz.
        static readonly double[] PinnaRho = { 0.5, -1.0, 0.5, -0.25, 0.25 };
        static readonly double[] PinnaA = { 1, 5, 5, 5, 5 };
        static readonly double[] PinnaB = { 2, 4, 7, 11, 13 };
        static readonly double[] PinnaD = { 1, 0.5, 0.5, 0.5, 0.5 };

        readonly int _sr;
        readonly SpeakerLayout _layout;
        readonly IHeadProvider _head;
        readonly int _n;

        readonly float[][] _delay;
        int _pos;

        // Indexed [speaker * 2 + ear]; delays additionally * Taps + tap.
        readonly double[] _curDelay, _incDelay;
        readonly double[] _b0, _b1, _a1, _ib0, _ib1, _ia1;
        readonly double[] _x1, _y1;
        readonly double[] _tgtDelay = new double[Taps];
        readonly double[] _tapGain = new double[Taps];
        bool _primed;
        int _blockLeft;

        /// <summary>Gain applied to every virtual speaker (headroom for multichannel sums).</summary>
        public double SpeakerGain = 0.6;
        /// <summary>Gain of the LFE channel, which is sent equally to both ears.</summary>
        public double LfeGain = 0.5;
        /// <summary>0 disables the pinna echoes, 1 is the full published model.</summary>
        public double PinnaAmount = 0.5;

        public Binaural(int sampleRate, SpeakerLayout layout, IHeadProvider head)
        {
            _sr = sampleRate;
            _layout = layout;
            _head = head;
            _n = layout.Count;
            _delay = new float[_n][];
            for (int i = 0; i < _n; i++) _delay[i] = new float[DelayLen];
            int k = _n * 2;
            _curDelay = new double[k * Taps];
            _incDelay = new double[k * Taps];
            _b0 = new double[k]; _b1 = new double[k]; _a1 = new double[k];
            _ib0 = new double[k]; _ib1 = new double[k]; _ia1 = new double[k];
            _x1 = new double[k]; _y1 = new double[k];
        }

        /// <summary>Direction of a room-fixed source expressed in the head's own frame
        /// (x right, y up, z forward). Yaw is positive turning right, pitch positive
        /// looking up, roll positive tilting the right ear down.</summary>
        public static void ToHeadFrame(double azDeg, double elDeg, double yawDeg, double pitchDeg, double rollDeg,
                                       out double hx, out double hy, out double hz)
        {
            const double D = Math.PI / 180.0;
            double ce = Math.Cos(elDeg * D);
            double x = Math.Sin(azDeg * D) * ce, y = Math.Sin(elDeg * D), z = Math.Cos(azDeg * D) * ce;

            double c = Math.Cos(yawDeg * D), s = Math.Sin(yawDeg * D);
            double x1 = c * x - s * z, y1 = y, z1 = s * x + c * z;

            c = Math.Cos(pitchDeg * D); s = Math.Sin(pitchDeg * D);
            double y2 = c * y1 - s * z1, z2 = s * y1 + c * z1;

            c = Math.Cos(rollDeg * D); s = Math.Sin(rollDeg * D);
            hx = c * x1 - s * y2;
            hy = s * x1 + c * y2;
            hz = z2;
        }

        void UpdateTargets(bool snap)
        {
            double yaw, pitch, roll;
            _head.GetPose((double)ControlBlock / _sr, out yaw, out pitch, out roll);

            double beta = 2.0 * SpeedOfSound / HeadRadius;
            double fs2 = 2.0 * _sr;
            double aOverC = HeadRadius / SpeedOfSound * _sr;   // samples
            double srScale = _sr / 44100.0;
            double inv = 1.0 / ControlBlock;

            _tapGain[0] = 1.0;
            for (int t = 1; t < Taps; t++) _tapGain[t] = PinnaRho[t - 1] * PinnaAmount;

            for (int sp = 0; sp < _n; sp++)
            {
                if (_layout.IsLfe[sp]) continue;
                double hx, hy, hz;
                ToHeadFrame(_layout.Azimuth[sp], _layout.Elevation[sp], yaw, pitch, roll, out hx, out hy, out hz);
                double az = Math.Atan2(hx, hz);
                double el = Math.Asin(Math.Max(-1.0, Math.Min(1.0, hy)));
                double cosHalfAz = Math.Cos(az * 0.5);

                for (int ear = 0; ear < 2; ear++)
                {
                    int k = sp * 2 + ear;
                    double cosTheta = Math.Max(-1.0, Math.Min(1.0, ear == 1 ? hx : -hx));
                    double theta = Math.Acos(cosTheta);   // angle between source and this ear's axis

                    // Head shadow: one-pole/one-zero filter whose zero moves with the angle.
                    double alpha = 1.05 + 0.95 * Math.Cos(theta * 1.2);
                    double den = beta + fs2;
                    double b0 = (beta + alpha * fs2) / den;
                    double b1 = (beta - alpha * fs2) / den;
                    double a1 = (beta - fs2) / den;

                    // Interaural delay (Woodworth), offset so it is never negative.
                    double itd = theta < Math.PI / 2
                        ? aOverC * (1.0 - cosTheta)
                        : aOverC * (1.0 + theta - Math.PI / 2);

                    _tgtDelay[0] = itd;
                    for (int t = 1; t < Taps; t++)
                    {
                        double echo = PinnaA[t - 1] * cosHalfAz * Math.Sin(PinnaD[t - 1] * (Math.PI / 2 - el)) + PinnaB[t - 1];
                        _tgtDelay[t] = itd + echo * srScale;
                    }

                    int d = k * Taps;
                    if (snap)
                    {
                        for (int t = 0; t < Taps; t++) { _curDelay[d + t] = _tgtDelay[t]; _incDelay[d + t] = 0; }
                        _b0[k] = b0; _b1[k] = b1; _a1[k] = a1;
                        _ib0[k] = 0; _ib1[k] = 0; _ia1[k] = 0;
                    }
                    else
                    {
                        for (int t = 0; t < Taps; t++) _incDelay[d + t] = (_tgtDelay[t] - _curDelay[d + t]) * inv;
                        _ib0[k] = (b0 - _b0[k]) * inv;
                        _ib1[k] = (b1 - _b1[k]) * inv;
                        _ia1[k] = (a1 - _a1[k]) * inv;
                    }
                }
            }
        }

        /// <summary>Renders interleaved multichannel input to interleaved stereo.</summary>
        public void Process(float[] input, int frames, float[] output)
        {
            int n = _n;
            float spkGain = (float)SpeakerGain;
            float lfeGain = (float)LfeGain;

            for (int f = 0; f < frames; f++)
            {
                if (_blockLeft == 0)
                {
                    UpdateTargets(!_primed);
                    _primed = true;
                    _blockLeft = ControlBlock;
                }
                _blockLeft--;

                double outL = 0, outR = 0;
                int inBase = f * n;
                for (int sp = 0; sp < n; sp++)
                {
                    float x = input[inBase + sp];
                    if (_layout.IsLfe[sp])
                    {
                        outL += x * lfeGain;
                        outR += x * lfeGain;
                        continue;
                    }
                    float[] line = _delay[sp];
                    line[_pos] = x * spkGain;

                    for (int ear = 0; ear < 2; ear++)
                    {
                        int k = sp * 2 + ear;
                        int d = k * Taps;
                        double acc = 0;
                        for (int t = 0; t < Taps; t++)
                        {
                            double g = _tapGain[t];
                            double dl = _curDelay[d + t];
                            _curDelay[d + t] = dl + _incDelay[d + t];
                            if (g == 0) continue;
                            int whole = (int)dl;
                            double frac = dl - whole;
                            int i0 = (_pos - whole) & DelayMask;
                            int i1 = (i0 - 1) & DelayMask;
                            acc += g * (line[i0] + frac * (line[i1] - line[i0]));
                        }
                        double y = _b0[k] * acc + _b1[k] * _x1[k] - _a1[k] * _y1[k];
                        _x1[k] = acc;
                        _y1[k] = y;
                        _b0[k] += _ib0[k];
                        _b1[k] += _ib1[k];
                        _a1[k] += _ia1[k];
                        if (ear == 0) outL += y; else outR += y;
                    }
                }
                _pos = (_pos + 1) & DelayMask;
                output[f * 2] = Limit(outL);
                output[f * 2 + 1] = Limit(outR);
            }
        }

        /// <summary>Transparent below 0.8, then eases smoothly toward full scale.</summary>
        static float Limit(double v)
        {
            double a = Math.Abs(v);
            if (a <= 0.8) return (float)v;
            double y = 0.8 + 0.2 * Math.Tanh((a - 0.8) / 0.2);
            return (float)(v < 0 ? -y : y);
        }
    }
}
