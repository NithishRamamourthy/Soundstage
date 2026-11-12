// Connection to the headphones' control channel: the Bluetooth RFCOMM service
// the phone app talks to. This file handles the socket and the message framing;
// what the messages mean lives with the callers.
//
// Framing, as documented by the open-source SonyHeadphonesClient and
// Gadgetbridge projects:
//   3E  type  seq  len(4, big-endian)  payload...  checksum  3C
// where checksum is the 8-bit sum of type..payload, and any 3C/3D/3E between
// the markers is sent as 3D followed by the byte with bit 4 cleared.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Soundstage.Engine
{
    public sealed class SonyMessage
    {
        public byte Type;
        public byte Sequence;
        public byte[] Payload;

        public override string ToString()
        {
            return "type " + Type.ToString("X2") + " seq " + Sequence + " : " + SonyLink.Hex(Payload);
        }
    }

    public sealed class SonyLink : IDisposable
    {
        public const byte TypeAck = 0x01;
        public const byte TypeCommand1 = 0x0C;   // "table 1" commands
        public const byte TypeCommand2 = 0x0E;   // "table 2" commands

        /// <summary>The control service on newer models (XM4 and later).</summary>
        public static readonly Guid ServiceV2 = new Guid("956C7B26-D49A-4BA8-B03F-B17D393CB6E2");
        /// <summary>The control service on older models.</summary>
        public static readonly Guid ServiceV1 = new Guid("96CC203E-5068-46AD-B32D-E316F5E069BA");

        const int AF_BTH = 32, SOCK_STREAM = 1, BTHPROTO_RFCOMM = 3;
        const int SOL_SOCKET = 0xFFFF, SO_RCVTIMEO = 0x1006, SO_SNDTIMEO = 0x1005;

        [DllImport("ws2_32.dll", SetLastError = true)] static extern int WSAStartup(ushort version, byte[] data);
        [DllImport("ws2_32.dll", SetLastError = true)] static extern IntPtr socket(int af, int type, int protocol);
        [DllImport("ws2_32.dll", SetLastError = true)] static extern int connect(IntPtr s, byte[] name, int nameLen);
        [DllImport("ws2_32.dll", SetLastError = true)] static extern int send(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll", SetLastError = true)] static extern int recv(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll", SetLastError = true)] static extern int closesocket(IntPtr s);
        [DllImport("ws2_32.dll", SetLastError = true)] static extern int setsockopt(IntPtr s, int level, int name, ref int value, int len);
        [DllImport("ws2_32.dll")] static extern int WSAGetLastError();

        static readonly IntPtr InvalidSocket = new IntPtr(-1);

        IntPtr _socket = InvalidSocket;
        Thread _reader;
        volatile bool _closed;
        readonly object _sendLock = new object();
        readonly AutoResetEvent _ackEvent = new AutoResetEvent(false);
        byte _sequence;

        /// <summary>A command arrived from the headphones (already acknowledged).</summary>
        public event Action<SonyMessage> Received;
        /// <summary>The connection ended.</summary>
        public event Action Closed;

        public bool IsOpen { get { return !_closed && _socket != InvalidSocket; } }

        /// <summary>Opens the control channel. Returns null on success, or why it failed.</summary>
        public string Open(ulong bluetoothAddress, Guid service)
        {
            WSAStartup(0x0202, new byte[512]);
            IntPtr s = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
            if (s == InvalidSocket) return "Bluetooth is not available (socket error " + WSAGetLastError() + ").";

            // SOCKADDR_BTH, packed: family(2) address(8) serviceClassId(16) port(4).
            byte[] addr = new byte[30];
            addr[0] = AF_BTH;
            Array.Copy(BitConverter.GetBytes(bluetoothAddress), 0, addr, 2, 8);
            Array.Copy(service.ToByteArray(), 0, addr, 10, 16);
            if (connect(s, addr, addr.Length) != 0)
            {
                int err = WSAGetLastError();
                closesocket(s);
                return "Could not open the control channel (socket error " + err + ").";
            }
            int timeout = 3000;
            setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, ref timeout, 4);

            _socket = s;
            _closed = false;
            _sequence = 0;
            _reader = new Thread(ReadLoop);
            _reader.IsBackground = true;
            _reader.Name = "sony-link";
            _reader.Start();
            return null;
        }

        /// <summary>Sends a command and waits briefly for the headphones to acknowledge it.</summary>
        public bool Send(byte type, params byte[] payload)
        {
            lock (_sendLock)
            {
                if (!IsOpen) return false;
                byte[] frame = Encode(type, _sequence, payload);
                _ackEvent.Reset();
                if (send(_socket, frame, frame.Length, 0) != frame.Length) return false;
                // The acknowledgement carries the next sequence number we must use; the
                // reader adopts it. Replies can arrive before the ACK, so they are
                // delivered independently of this wait.
                return _ackEvent.WaitOne(1500);
            }
        }

        public static byte[] Encode(byte type, byte sequence, byte[] payload)
        {
            byte[] body = new byte[7 + payload.Length];
            body[0] = type;
            body[1] = sequence;
            body[2] = (byte)(payload.Length >> 24);
            body[3] = (byte)(payload.Length >> 16);
            body[4] = (byte)(payload.Length >> 8);
            body[5] = (byte)payload.Length;
            Array.Copy(payload, 0, body, 6, payload.Length);
            int sum = 0;
            for (int i = 0; i < body.Length - 1; i++) sum += body[i];
            body[body.Length - 1] = (byte)sum;

            List<byte> frame = new List<byte>(body.Length + 8);
            frame.Add(0x3E);
            foreach (byte b in body)
            {
                if (b == 0x3C || b == 0x3D || b == 0x3E) { frame.Add(0x3D); frame.Add((byte)(b & 0xEF)); }
                else frame.Add(b);
            }
            frame.Add(0x3C);
            return frame.ToArray();
        }

        /// <summary>Decodes the bytes between the markers; null if the frame is damaged.</summary>
        public static SonyMessage Decode(List<byte> escaped)
        {
            List<byte> body = new List<byte>(escaped.Count);
            for (int i = 0; i < escaped.Count; i++)
            {
                byte b = escaped[i];
                if (b == 0x3D)
                {
                    if (++i >= escaped.Count) return null;
                    b = (byte)(escaped[i] | 0x10);
                }
                body.Add(b);
            }
            if (body.Count < 7) return null;
            int sum = 0;
            for (int i = 0; i < body.Count - 1; i++) sum += body[i];
            if ((byte)sum != body[body.Count - 1]) return null;
            int length = (body[2] << 24) | (body[3] << 16) | (body[4] << 8) | body[5];
            if (length != body.Count - 7) return null;
            SonyMessage m = new SonyMessage();
            m.Type = body[0];
            m.Sequence = body[1];
            m.Payload = body.GetRange(6, length).ToArray();
            return m;
        }

        void ReadLoop()
        {
            byte[] buf = new byte[2048];
            List<byte> current = null;
            try
            {
                while (!_closed)
                {
                    int n = recv(_socket, buf, buf.Length, 0);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b == 0x3E) { current = new List<byte>(); continue; }
                        if (current == null) continue;
                        if (b != 0x3C) { current.Add(b); continue; }
                        SonyMessage m = Decode(current);
                        current = null;
                        if (m != null) Handle(m);
                    }
                }
            }
            catch (Exception) { }
            bool wasOpen = !_closed;
            _closed = true;
            if (wasOpen)
            {
                Action h = Closed;
                if (h != null) h();
            }
        }

        void Handle(SonyMessage m)
        {
            if (m.Type == TypeAck)
            {
                _sequence = m.Sequence;
                _ackEvent.Set();
                return;
            }
            // Every command must be acknowledged with the complement of its sequence number.
            byte[] ack = Encode(TypeAck, (byte)(1 - m.Sequence), new byte[0]);
            send(_socket, ack, ack.Length, 0);
            Action<SonyMessage> h = Received;
            if (h != null) h(m);
        }

        public void Dispose()
        {
            _closed = true;
            IntPtr s = _socket;
            _socket = InvalidSocket;
            if (s != InvalidSocket) closesocket(s);
        }

        public static string Hex(byte[] bytes)
        {
            if (bytes == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
