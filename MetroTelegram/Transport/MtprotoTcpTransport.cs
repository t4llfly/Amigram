using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace MetroTelegram.Transport
{
    public class MtprotoTcpTransport : ITcpTransport
    {
        private StreamSocket _socket;
        private DataWriter _writer;
        private DataReader _reader;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public bool IsConnected { get; private set; }
        public DataCenter CurrentDc { get; private set; }

        public event EventHandler<byte[]> PacketReceived;
        public event EventHandler<Exception> ConnectionClosed;

        private static readonly byte[] IntermediateHeader = new byte[] { 0xEE, 0xEE, 0xEE, 0xEE };

        public async Task ConnectAsync(DataCenter dc)
        {
            Disconnect();

            CurrentDc = dc ?? DataCenter.Default;
            _cts = new CancellationTokenSource();

            bool connected = false;
            Exception lastException = null;

            foreach (string host in CurrentDc.FallbackHosts)
            {
                foreach (int port in DataCenter.FallbackPorts)
                {
                    try
                    {
                        Debug.WriteLine(string.Format("[Transport] Попытка подключения к DC{0} ({1}:{2})...", CurrentDc.Id, host, port));

                        _socket?.Dispose();
                        _socket = new StreamSocket();
                        _socket.Control.NoDelay = true;
                        _socket.Control.KeepAlive = true;

                        HostName hostName = new HostName(host);

                        var connectTask = _socket.ConnectAsync(hostName, port.ToString()).AsTask(_cts.Token);
                        var timeoutTask = Task.Delay(4000, _cts.Token);

                        var completed = await Task.WhenAny(connectTask, timeoutTask);
                        if (completed == timeoutTask)
                        {
                            Debug.WriteLine(string.Format("[Transport] Таймаут {1}:{2}, пробуем следующий порт...", CurrentDc.Id, host, port));
                            continue;
                        }

                        await connectTask;

                        _writer = new DataWriter(_socket.OutputStream);
                        _reader = new DataReader(_socket.InputStream);
                        _reader.InputStreamOptions = InputStreamOptions.None;

                        _writer.WriteBytes(IntermediateHeader);
                        await _writer.StoreAsync();

                        IsConnected = true;
                        CurrentDc.Host = host;
                        CurrentDc.Port = port;
                        connected = true;

                        Debug.WriteLine(string.Format("[Transport] УСПЕХ! Подключено к DC{0} через порт {1}:{2}", CurrentDc.Id, host, port));
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        Debug.WriteLine(string.Format("[Transport] Порт {0}:{1} недоступен ({2}), пробуем следующий...", host, port, ex.Message));
                    }
                }

                if (connected) break;
            }

            if (!connected)
            {
                Disconnect();
                throw new IOException(string.Format("Не удалось подключиться к DC{0} ни по одному из портов (443, 80, 5222).", CurrentDc.Id), lastException);
            }

            Task unused = Task.Run(() => ReceiveLoopAsync());
        }

        public async Task SendPacketAsync(byte[] payload)
        {
            if (!IsConnected || _writer == null || payload == null || payload.Length == 0)
                return;

            await _sendLock.WaitAsync();
            try
            {
                if (!IsConnected || _writer == null) return;

                byte[] lengthHeader = BitConverter.GetBytes(payload.Length);
                _writer.WriteBytes(lengthHeader);
                _writer.WriteBytes(payload);
                await _writer.StoreAsync();
            }
            catch (Exception ex)
            {
                HandleDisconnect(ex);
                throw;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            CancellationToken token = _cts.Token;

            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    byte[] lengthBuffer = await ReadExactBytesAsync(4, token);
                    if (lengthBuffer == null) break;

                    int packetLength = BitConverter.ToInt32(lengthBuffer, 0);

                    if (packetLength < 0)
                    {
                        int errorCode = -packetLength;
                        Debug.WriteLine(string.Format("[Transport Error] Код ошибки: {0}", errorCode));
                        throw new IOException(string.Format("Transport Error: {0}", errorCode));
                    }

                    if (packetLength == 0 || packetLength > 16 * 1024 * 1024)
                    {
                        throw new InvalidDataException("Некорректная длина пакета MTProto.");
                    }

                    byte[] packetData = await ReadExactBytesAsync(packetLength, token);
                    if (packetData == null) break;

                    PacketReceived?.Invoke(this, packetData);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    HandleDisconnect(ex);
                }
            }
        }

        private async Task<byte[]> ReadExactBytesAsync(int count, CancellationToken token)
        {
            try
            {
                uint loaded = await _reader.LoadAsync((uint)count).AsTask(token);
                if (loaded < (uint)count) return null;

                byte[] result = new byte[count];
                _reader.ReadBytes(result);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private void HandleDisconnect(Exception ex)
        {
            if (!IsConnected) return;
            Disconnect();
            ConnectionClosed?.Invoke(this, ex);
        }

        public void Disconnect()
        {
            IsConnected = false;

            try { _cts?.Cancel(); } catch { }

            if (_writer != null)
            {
                try { _writer.DetachStream(); } catch { }
                _writer.Dispose();
                _writer = null;
            }

            if (_reader != null)
            {
                try { _reader.DetachStream(); } catch { }
                _reader.Dispose();
                _reader = null;
            }

            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null;
            }
        }

        public void Dispose()
        {
            Disconnect();
            _sendLock.Dispose();
        }
    }
}