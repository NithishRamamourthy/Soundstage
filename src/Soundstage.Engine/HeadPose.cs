// Turns the tracker's 25 Hz orientation samples into a smooth, slightly
// predicted pose for the renderer, and listens for those samples on UDP.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Soundstage.Engine
{
    public sealed class HeadPose : IHeadProvider
    {
        readonly object _lock = new object();
        readonly Stopwatch _clock = Stopwatch.StartNew();

        // Latest raw sample (after sign flips, yaw unwrapped) and its velocity.
        double _yaw, _pitch, _roll;
        double _vYaw, _vPitch, _vRoll;
        double _sampleTime = -1;
        bool _hasSample;

        // What "straight ahead" currently means.
        double _offYaw, _offPitch, _offRoll;

        // Smoothed pose handed to the renderer.
        double _sYaw, _sPitch, _sRoll;

        public bool Enabled = true;
        public bool AutoCenter = true;
        public int YawSign = 1, PitchSign = 1, RollSign = 1;
        /// <summary>How far ahead to extrapolate head motion, to offset Bluetooth latency.</summary>
        public double PredictSeconds = 0.10;
        public double SmoothSeconds = 0.015;
        public double AutoCenterSeconds = 12.0;

        public int SampleCount;

        public void OnSample(double yaw, double pitch, double roll)
        {
            lock (_lock)
            {
                double now = _clock.Elapsed.TotalSeconds;
                yaw *= YawSign; pitch *= PitchSign; roll *= RollSign;
                if (_hasSample)
                {
                    // Unwrap yaw so crossing +/-180 does not spin the scene.
                    double d = yaw - _yaw;
                    d -= 360.0 * Math.Round(d / 360.0);
                    yaw = _yaw + d;
                    double dt = now - _sampleTime;
                    if (dt < 0.005)
                    {
                        // Duplicate delivery (e.g. two tracker instances broadcasting):
                        // too close together to say anything about velocity.
                        SampleCount++;
                        return;
                    }
                    if (dt < 0.25)
                    {
                        _vYaw = 0.5 * _vYaw + 0.5 * (yaw - _yaw) / dt;
                        _vPitch = 0.5 * _vPitch + 0.5 * (pitch - _pitch) / dt;
                        _vRoll = 0.5 * _vRoll + 0.5 * (roll - _roll) / dt;
                    }
                    else
                    {
                        _vYaw = _vPitch = _vRoll = 0;
                    }
                }
                _yaw = yaw; _pitch = pitch; _roll = roll;
                _sampleTime = now;
                if (!_hasSample)
                {
                    _hasSample = true;
                    _offYaw = yaw; _offPitch = pitch; _offRoll = roll;
                }
                SampleCount++;
            }
        }

        /// <summary>Forgets all samples; the next one received defines "front".</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _hasSample = false;
                _vYaw = _vPitch = _vRoll = 0;
                _sYaw = _sPitch = _sRoll = 0;
            }
        }

        /// <summary>Makes the current head direction the new "front".</summary>
        public void Recenter()
        {
            lock (_lock)
            {
                _offYaw = _yaw; _offPitch = _pitch; _offRoll = _roll;
                _sYaw = _sPitch = _sRoll = 0;
            }
        }

        public void FlipSign(char axis)
        {
            lock (_lock)
            {
                if (axis == 'y') { YawSign = -YawSign; _yaw = -_yaw; _offYaw = -_offYaw; _vYaw = -_vYaw; }
                if (axis == 'p') { PitchSign = -PitchSign; _pitch = -_pitch; _offPitch = -_offPitch; _vPitch = -_vPitch; }
                if (axis == 'r') { RollSign = -RollSign; _roll = -_roll; _offRoll = -_offRoll; _vRoll = -_vRoll; }
            }
        }

        public void GetPose(double dt, out double yaw, out double pitch, out double roll)
        {
            lock (_lock)
            {
                double tYaw = 0, tPitch = 0, tRoll = 0;
                if (Enabled && _hasSample)
                {
                    double age = _clock.Elapsed.TotalSeconds - _sampleTime;
                    // Extrapolate through the gap since the last sample plus the latency
                    // budget; stop extrapolating if the tracker has gone quiet.
                    double ahead = age < 0.3 ? Math.Min(age + PredictSeconds, 0.25) : 0;
                    tYaw = _yaw + _vYaw * ahead - _offYaw;
                    tPitch = _pitch + _vPitch * ahead - _offPitch;
                    tRoll = _roll + _vRoll * ahead - _offRoll;
                }
                double k = 1.0 - Math.Exp(-dt / SmoothSeconds);
                _sYaw += (tYaw - _sYaw) * k;
                _sPitch += (tPitch - _sPitch) * k;
                _sRoll += (tRoll - _sRoll) * k;

                if (Enabled && AutoCenter && _hasSample)
                {
                    // Let "front" drift slowly toward where the head points, which also
                    // absorbs gyro drift.
                    _offYaw += _sYaw * dt / AutoCenterSeconds;
                }
                yaw = _sYaw; pitch = _sPitch; roll = _sRoll;
            }
        }

        public void Snapshot(out double yaw, out double pitch, out double roll)
        {
            lock (_lock) { yaw = _sYaw; pitch = _sPitch; roll = _sRoll; }
        }
    }

    /// <summary>Receives the tracker bridge's JSON datagrams on 127.0.0.1.</summary>
    sealed class TrackerListener
    {
        readonly HeadPose _pose;
        readonly int _port;
        UdpClient _udp;
        Thread _thread;
        volatile bool _stop;

        public string Device;

        public TrackerListener(HeadPose pose, int port)
        {
            _pose = pose;
            _port = port;
        }

        public bool Start()
        {
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
            }
            catch (SocketException)
            {
                return false;
            }
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "tracker-udp";
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            _stop = true;
            if (_udp != null) _udp.Close();
        }

        void Run()
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                byte[] data;
                try { data = _udp.Receive(ref from); }
                catch (Exception) { if (_stop) return; continue; }
                string json = Encoding.UTF8.GetString(data);
                double y, p, r;
                if (TryParseYpr(json, out y, out p, out r)) _pose.OnSample(y, p, r);
                if (Device == null) Device = ParseString(json, "\"device\":\"");
            }
        }

        static string ParseString(string json, string key)
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int e = json.IndexOf('"', i);
            return e > i ? json.Substring(i, e - i) : null;
        }

        static bool TryParseYpr(string json, out double yaw, out double pitch, out double roll)
        {
            yaw = pitch = roll = 0;
            const string key = "\"yprDegrees\":[";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return false;
            i += key.Length;
            int e = json.IndexOf(']', i);
            if (e < 0) return false;
            string[] parts = json.Substring(i, e - i).Split(',');
            if (parts.Length != 3) return false;
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out yaw)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out pitch)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out roll);
        }
    }
}
