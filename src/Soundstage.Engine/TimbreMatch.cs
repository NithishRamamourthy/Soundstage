// Makes the binaural path sound like plain playback.
//
// A stereo signal sent to two front speakers and through a dummy-head HRTF comes
// out with a different magnitude response, and about 4 dB quieter, than the same
// signal played straight to the headphones. This builds one correction filter from
// the loaded HRIR set: measure what the front pair actually does to a flat signal,
// invert that magnitude (smoothed and limited), and normalise the level.
//
// The filter is applied to both ears equally, so it only changes timbre and level.
// The interaural time and level differences, which are what make the sound sit
// outside your head, are left alone.
using System;
using System.Numerics;

namespace Soundstage.Engine
{
    sealed class TimbreMatch
    {
        /// <summary>Symmetric FIR, odd length; its centre is the latency.</summary>
        public float[] Taps { get; private set; }

        /// <summary>Makeup gain folded into the taps; kept for the report.</summary>
        public float Gain { get; private set; }

        /// <summary>Correction actually applied, in dB, at 1/3-octave centres.</summary>
        public double[] ReportDb { get; private set; }
        public double[] ReportHz { get; private set; }

        readonly float[] _histL, _histR;
        int _pos;

        TimbreMatch(int length)
        {
            Taps = new float[length];
            _histL = new float[length];
            _histR = new float[length];
        }

        public void Reset()
        {
            Array.Clear(_histL, 0, _histL.Length);
            Array.Clear(_histR, 0, _histR.Length);
            _pos = 0;
        }

        /// <summary>Filters a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int frames)
        {
            float[] h = Taps;
            int n = h.Length;
            for (int i = 0; i < frames; i++)
            {
                _histL[_pos] = left[i];
                _histR[_pos] = right[i];
                float sl = 0, sr = 0;
                int idx = _pos;
                for (int k = 0; k < n; k++)
                {
                    float g = h[k];
                    sl += g * _histL[idx];
                    sr += g * _histR[idx];
                    if (--idx < 0) idx = n - 1;
                }
                left[i] = sl;
                right[i] = sr;
                if (++_pos == n) _pos = 0;
            }
        }

        // ------------------------------------------------------------ design

        /// <summary>Builds the correction for a layout, using the front pair as the reference.</summary>
        public static TimbreMatch Build(HrtfSet set, SpeakerLayout layout, double speakerGain, int sampleRate)
        {
            const int Analysis = 2048;
            const int FirLength = 512;

            Complex[] sumL = new Complex[Analysis];
            int used = 0;

            for (int sp = 0; sp < layout.Count; sp++)
            {
                if (layout.IsLfe[sp]) continue;
                double az = layout.Azimuth[sp], el = layout.Elevation[sp];
                // Only the front pair carries a plain stereo signal.
                if (Math.Abs(az) > 45) continue;

                float[] hL = new float[set.Taps], hR = new float[set.Taps];
                double dl, dr;
                set.Interpolate(az, el, hL, hR, out dl, out dr);
                used++;

                Complex[] fl = new Complex[Analysis], fr = new Complex[Analysis];
                // The stored responses are time-reversed; flip them back.
                for (int t = 0; t < set.Taps && t < Analysis; t++)
                {
                    fl[t] = new Complex(hL[set.Taps - 1 - t], 0);
                    fr[t] = new Complex(hR[set.Taps - 1 - t], 0);
                }
                Fft(fl, false);
                Fft(fr, false);

                double shiftL = dl + 2.0, shiftR = dr + 2.0;   // MinDelay in the renderer
                for (int k = 0; k < Analysis; k++)
                {
                    double w = -2.0 * Math.PI * k / Analysis;
                    Complex el2 = Complex.FromPolarCoordinates(1.0, w * shiftL);
                    Complex er2 = Complex.FromPolarCoordinates(1.0, w * shiftR);
                    // A stereo signal carries different audio in each channel, so the two
                    // ears are summed coherently per speaker and incoherently across them.
                    Complex mono = fl[k] * el2 + fr[k] * er2;
                    sumL[k] += new Complex(mono.Magnitude * mono.Magnitude, 0);
                }
            }

            TimbreMatch result = new TimbreMatch(FirLength);
            if (used == 0) { result.Taps = new float[0]; return result; }   // nothing to correct

            // What a flat signal looks like after the front pair and the speaker gain.
            // A stereo input carries two independent channels, so the mono sum is twice
            // as loud as one channel; halve it to compare like with like.
            int half = Analysis / 2;
            double[] power = new double[half + 1];
            for (int k = 0; k <= half; k++) power[k] = sumL[k].Real * 0.5 * speakerGain * speakerGain;

            // Smooth before inverting: a dummy-head response has deep, narrow notches
            // that cannot be filled without ringing.
            double[] smoothed = Smooth(power, sampleRate, Analysis);

            double mean = 0;
            for (int k = 0; k <= half; k++) mean += smoothed[k];
            mean /= half + 1;
            mean = Math.Max(mean, 1e-12);

            // Limit the correction to +-9 dB around the average.
            double[] corr = new double[half + 1];
            for (int k = 0; k <= half; k++)
            {
                double db = 10.0 * Math.Log10(Math.Max(smoothed[k], 1e-12) / mean);
                db = Math.Max(-9.0, Math.Min(9.0, db));
                corr[k] = Math.Pow(10.0, -db / 20.0);
            }

            // Frequency-sampled linear-phase FIR: the spectrum must be real and
            // symmetric, so the upper half mirrors the lower one.
            Complex[] spec = new Complex[FirLength];
            for (int k = 0; k < FirLength; k++)
            {
                int mirror = k <= FirLength / 2 ? k : FirLength - k;
                double bin = (double)mirror / FirLength * Analysis;   // 0..Nyquist
                int b0 = (int)Math.Floor(bin), b1 = Math.Min(b0 + 1, half);
                double t = bin - b0;
                double c = corr[Math.Min(b0, half)] * (1 - t) + corr[b1] * t;
                spec[k] = new Complex(c, 0);
            }
            Fft(spec, true);

            // Rotate so the response is centred, then window it to stop it ringing.
            float[] taps = new float[FirLength];
            for (int k = 0; k < FirLength; k++)
            {
                double v = spec[(k + FirLength / 2) % FirLength].Real;
                double win = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * k / (FirLength - 1));
                taps[k] = (float)(v * win);
            }

            // Normalise so a flat input comes out at the same level it went in.
            Complex[] check = new Complex[Analysis];
            for (int k = 0; k < FirLength; k++) check[k] = new Complex(taps[k], 0);
            Fft(check, false);
            double outPower = 0;
            for (int k = 0; k <= half; k++)
            {
                double m = check[k].Magnitude;
                outPower += power[k] * m * m;
            }
            outPower = Math.Max(outPower / (half + 1), 1e-12);
            double gain = 1.0 / Math.Sqrt(outPower);
            for (int k = 0; k < FirLength; k++) taps[k] = (float)(taps[k] * gain);

            result.Taps = taps;
            result.Gain = (float)gain;

            // Report at 1/3-octave centres.
            double[] centres = { 63, 125, 250, 500, 1000, 2000, 4000, 8000, 12000 };
            result.ReportHz = centres;
            result.ReportDb = new double[centres.Length];
            for (int i = 0; i < centres.Length; i++)
            {
                int bin = (int)Math.Round(centres[i] * Analysis / (double)sampleRate);
                if (bin > half) bin = half;
                double c = corr[bin] * gain;
                result.ReportDb[i] = 20.0 * Math.Log10(Math.Max(c, 1e-9));
            }
            return result;
        }

        /// <summary>Fractional-octave power smoothing (1/6 octave).</summary>
        static double[] Smooth(double[] p, int sampleRate, int analysis)
        {
            int half = analysis / 2;
            double[] o = new double[half + 1];
            double ratio = Math.Pow(2.0, 1.0 / 12.0);   // half a 1/6-octave window each side
            for (int k = 0; k <= half; k++)
            {
                double f = Math.Max(k, 1) * (double)sampleRate / analysis;
                int lo = (int)(f / ratio * analysis / sampleRate);
                int hi = (int)(f * ratio * analysis / sampleRate);
                lo = Math.Max(0, Math.Min(half, lo));
                hi = Math.Max(lo, Math.Min(half, hi));
                double sum = 0;
                for (int i = lo; i <= hi; i++) sum += p[i];
                o[k] = sum / (hi - lo + 1);
            }
            return o;
        }

        // --------------------------------------------------------------- fft

        internal static void Fft(Complex[] a, bool inverse)
        {
            int n = a.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { Complex t = a[i]; a[i] = a[j]; a[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2.0 * Math.PI / len * (inverse ? 1 : -1);
                Complex wl = Complex.FromPolarCoordinates(1.0, ang);
                for (int i = 0; i < n; i += len)
                {
                    Complex w = Complex.One;
                    for (int k = 0; k < len / 2; k++)
                    {
                        Complex u = a[i + k], v = a[i + k + len / 2] * w;
                        a[i + k] = u + v;
                        a[i + k + len / 2] = u - v;
                        w *= wl;
                    }
                }
            }
            if (inverse)
            {
                for (int i = 0; i < n; i++) a[i] /= n;
            }
        }
    }
}
