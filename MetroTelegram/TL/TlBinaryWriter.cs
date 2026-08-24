using System;
using System.IO;
using System.Text;

namespace MetroTelegram.TL
{
    public class TlBinaryWriter : IDisposable
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;

        public TlBinaryWriter()
        {
            _stream = new MemoryStream();
            _writer = new BinaryWriter(_stream);
        }

        public void WriteByte(byte value) => _writer.Write(value);
        public void WriteInt32(int value) => _writer.Write(value);
        public void WriteUInt32(uint value) => _writer.Write(value);
        public void WriteInt64(long value) => _writer.Write(value);
        public void WriteUInt64(ulong value) => _writer.Write(value);
        public void WriteDouble(double value) => _writer.Write(value);
        public void WriteBool(bool value) => _writer.Write(value ? 0x997275B5 : 0xBC799737);

        public void WriteDateTime(DateTime dateTime)
        {
            if (dateTime == DateTime.MinValue)
            {
                _writer.Write(0);
                return;
            }
            int unixTime = (int)(dateTime.ToUniversalTime() - UnixEpoch).TotalSeconds;
            _writer.Write(unixTime);
        }

        public void WriteRawBytes(byte[] bytes)
        {
            if (bytes != null && bytes.Length > 0)
                _writer.Write(bytes);
        }

        public void WriteString(string value)
        {
            byte[] bytes = string.IsNullOrEmpty(value) ? new byte[0] : Encoding.UTF8.GetBytes(value);
            WriteBytes(bytes);
        }

        public void WriteBytes(byte[] bytes)
        {
            if (bytes == null) bytes = new byte[0];

            if (bytes.Length < 254)
            {
                _writer.Write((byte)bytes.Length);
                _writer.Write(bytes);
                int pad = (4 - ((bytes.Length + 1) % 4)) % 4;
                for (int i = 0; i < pad; i++) _writer.Write((byte)0);
            }
            else
            {
                _writer.Write((byte)254);
                _writer.Write((byte)(bytes.Length & 0xFF));
                _writer.Write((byte)((bytes.Length >> 8) & 0xFF));
                _writer.Write((byte)((bytes.Length >> 16) & 0xFF));
                _writer.Write(bytes);
                int pad = (4 - (bytes.Length % 4)) % 4;
                for (int i = 0; i < pad; i++) _writer.Write((byte)0);
            }
        }

        public byte[] ToByteArray() => _stream.ToArray();

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}