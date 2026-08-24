using System;
using System.Threading.Tasks;

namespace MetroTelegram.Transport
{
    public interface ITcpTransport : IDisposable
    {
        bool IsConnected { get; }
        DataCenter CurrentDc { get; }

        event EventHandler<byte[]> PacketReceived;
        event EventHandler<Exception> ConnectionClosed;

        Task ConnectAsync(DataCenter dc);
        Task SendPacketAsync(byte[] payload);
        void Disconnect();
    }
}