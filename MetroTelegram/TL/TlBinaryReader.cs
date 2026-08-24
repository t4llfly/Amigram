using System;
using System.IO;
using System.Text;

namespace MetroTelegram.TL
{
    public class TlBinaryReader : IDisposable
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public TlBinaryReader(byte[] data)
        {
            _stream = new MemoryStream(data ?? new byte[0]);
            _reader = new BinaryReader(_stream);
        }

        public int Position
        {
            get { return (int)_stream.Position; }
            set { _stream.Position = value; }
        }

        public int Length => (int)_stream.Length;
        public int Remaining => (int)(_stream.Length - _stream.Position);

        public byte ReadByte() => _reader.ReadByte();
        public int ReadInt32() => _reader.ReadInt32();
        public uint ReadUInt32() => _reader.ReadUInt32();
        public long ReadInt64() => _reader.ReadInt64();
        public double ReadDouble() => _reader.ReadDouble();
        public byte[] ReadRawBytes(int count) => _reader.ReadBytes(count);

        public bool ReadBool()
        {
            uint constructor = _reader.ReadUInt32();
            return constructor == 0x997275B5;
        }

        public DateTime ReadDateTime()
        {
            int unixTime = _reader.ReadInt32();
            if (unixTime <= 0) return DateTime.MinValue;
            try
            {
                return UnixEpoch.AddSeconds(unixTime).ToLocalTime();
            }
            catch
            {
                return DateTime.Now;
            }
        }

        public byte[] ReadBytes()
        {
            if (Remaining <= 0) return new byte[0];

            byte b = _reader.ReadByte();
            int length = b;
            int headerLen = 1;

            if (b == 254)
            {
                byte b0 = _reader.ReadByte();
                byte b1 = _reader.ReadByte();
                byte b2 = _reader.ReadByte();
                length = b0 | (b1 << 8) | (b2 << 16);
                headerLen = 4;
            }

            byte[] data = _reader.ReadBytes(length);

            int pad = (headerLen == 1)
                ? (4 - ((length + 1) % 4)) % 4
                : (4 - (length % 4)) % 4;

            if (pad > 0 && Remaining >= pad)
            {
                _reader.ReadBytes(pad);
            }

            return data;
        }

        public string ReadString()
        {
            byte[] bytes = ReadBytes();
            if (bytes == null || bytes.Length == 0) return string.Empty;
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }
}