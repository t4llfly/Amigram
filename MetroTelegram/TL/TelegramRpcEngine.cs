using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MetroTelegram.Crypto;
using MetroTelegram.Transport;

namespace MetroTelegram.TL
{
    public class TelegramRpcEngine
    {
        private readonly ITcpTransport _transport;
        private readonly AuthKeyStorage _storage;
        private readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        private long _sessionId;
        private int _seqNo = 0;
        private long _lastMsgId = 0;
        private bool _isConnectionInited = false;
        private readonly object _sessionLock = new object();

        private readonly Dictionary<long, TaskCompletionSource<byte[]>> _pendingRequests = new Dictionary<long, TaskCompletionSource<byte[]>>();
        private readonly List<long> _ackQueue = new List<long>();
        private bool _isSendingAck = false;

        public event EventHandler<byte[]> UpdateReceived;

        public const int CurrentLayer = 165;

        public TelegramRpcEngine(ITcpTransport transport, AuthKeyStorage storage)
        {
            _transport = transport;
            _storage = storage;
            _storage.Load();

            byte[] sessionBytes = new byte[8];
            _rng.GetBytes(sessionBytes);
            _sessionId = BitConverter.ToInt64(sessionBytes, 0);

            _transport.PacketReceived += OnPacketReceived;

            Debug.WriteLine(string.Format("[RPC Engine] Сессия сокета: 0x{0:X16}, DC: {1}, AuthKeyId: 0x{2:X16}",
                _sessionId, _storage.CurrentDcId, _storage.AuthKeyId));
        }

        public async Task<byte[]> SendRpcQueryAsync(byte[] queryBytes, bool wrapInitConnection = false, int timeoutMs = 25000)
        {
            if (!_storage.HasAuthKey)
            {
                throw new InvalidOperationException("Сессионный AuthKey не найден. Выполните Handshake.");
            }

            int attempts = 0;
            while (attempts < 3)
            {
                attempts++;
                long msgId;
                int seqNo;
                byte[] payloadToSend = queryBytes;

                if (wrapInitConnection && !_isConnectionInited)
                {
                    using (var writer = new TlBinaryWriter())
                    {
                        writer.WriteUInt32(0xda9b0d0d);
                        writer.WriteInt32(CurrentLayer);

                        writer.WriteUInt32(0xc1cd5ea9);
                        writer.WriteInt32(0);
                        writer.WriteInt32(AppConfig.ApiId);
                        writer.WriteString(AppConfig.DeviceModel);
                        writer.WriteString(AppConfig.SystemVersion);
                        writer.WriteString(AppConfig.AppVersion);
                        writer.WriteString(AppConfig.LangCode);
                        writer.WriteString(AppConfig.LangPack);
                        writer.WriteString(AppConfig.LangCode);

                        writer.WriteRawBytes(queryBytes);
                        payloadToSend = writer.ToByteArray();
                    }
                    _isConnectionInited = true;
                }

                lock (_sessionLock)
                {
                    msgId = GenerateMessageId(_storage.TimeOffset);
                    seqNo = _seqNo * 2 + 1;
                    _seqNo++;
                }

                var tcs = new TaskCompletionSource<byte[]>();
                lock (_pendingRequests)
                {
                    _pendingRequests[msgId] = tcs;
                }

                Debug.WriteLine(string.Format("[RPC Engine] Отправка MTProto (msg_id: {0}, seq_no: {1}, размер: {2}b)...",
                    msgId, seqNo, payloadToSend.Length));

                byte[] encryptedPacket = EncryptPacket(payloadToSend, msgId, seqNo);
                await _transport.SendPacketAsync(encryptedPacket);

                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    lock (_pendingRequests)
                    {
                        _pendingRequests.Remove(msgId);
                    }
                    throw new TimeoutException("Сервер Telegram не ответил вовремя.");
                }

                try
                {
                    return await tcs.Task;
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message == "BAD_SERVER_SALT")
                    {
                        Debug.WriteLine("[RPC Engine] Авто-повтор запроса с обновленным Server Salt...");
                        continue;
                    }
                    throw;
                }
            }

            throw new TimeoutException("Превышено количество попыток запроса.");
        }

        private byte[] EncryptPacket(byte[] messageData, long msgId, int seqNo)
        {
            byte[] plaintext;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt64(_storage.ServerSalt);
                writer.WriteInt64(_sessionId);
                writer.WriteInt64(msgId);
                writer.WriteInt32(seqNo);
                writer.WriteInt32(messageData.Length);
                writer.WriteRawBytes(messageData);

                int baseLength = 8 + 8 + 8 + 4 + 4 + messageData.Length;
                int padLen = 16 - (baseLength % 16);
                if (padLen < 12) padLen += 16;

                byte[] randomPad = new byte[padLen];
                _rng.GetBytes(randomPad);
                writer.WriteRawBytes(randomPad);

                plaintext = writer.ToByteArray();
            }

            byte[] msgKey = MtprotoKdf.ComputeMsgKey(_storage.AuthKey, plaintext, true);
            byte[] aesKey, aesIv;
            MtprotoKdf.ComputeKeys(_storage.AuthKey, msgKey, true, out aesKey, out aesIv);

            byte[] encryptedData = AesIge.Encrypt(plaintext, aesKey, aesIv);

            byte[] finalPacket = new byte[8 + 16 + encryptedData.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(_storage.AuthKeyId), 0, finalPacket, 0, 8);
            Buffer.BlockCopy(msgKey, 0, finalPacket, 8, 16);
            Buffer.BlockCopy(encryptedData, 0, finalPacket, 24, encryptedData.Length);

            return finalPacket;
        }

        private void OnPacketReceived(object sender, byte[] encryptedPacket)
        {
            if (encryptedPacket.Length < 24) return;

            try
            {
                long authKeyId = BitConverter.ToInt64(encryptedPacket, 0);
                if (authKeyId != _storage.AuthKeyId) return;

                byte[] msgKey = new byte[16];
                Buffer.BlockCopy(encryptedPacket, 8, msgKey, 0, 16);

                int cipherLen = encryptedPacket.Length - 24;
                byte[] cipherText = new byte[cipherLen];
                Buffer.BlockCopy(encryptedPacket, 24, cipherText, 0, cipherLen);

                byte[] aesKey, aesIv;
                MtprotoKdf.ComputeKeys(_storage.AuthKey, msgKey, false, out aesKey, out aesIv);

                byte[] plaintext = AesIge.Decrypt(cipherText, aesKey, aesIv);

                using (var reader = new TlBinaryReader(plaintext))
                {
                    ulong serverSalt = (ulong)reader.ReadInt64();
                    long sessionId = reader.ReadInt64();
                    long msgId = reader.ReadInt64();
                    int seqNo = reader.ReadInt32();
                    int msgLen = reader.ReadInt32();

                    if (msgLen <= 0 || msgLen > reader.Remaining) return;

                    byte[] messageData = reader.ReadRawBytes(msgLen);
                    _storage.ServerSalt = serverSalt;

                    QueueAck(msgId);
                    ProcessIncomingMessage(msgId, messageData);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[RPC Engine] Ошибка дешифрования: " + ex.Message);
            }
        }

        private void ProcessIncomingMessage(long incomingMsgId, byte[] messageData)
        {
            using (var reader = new TlBinaryReader(messageData))
            {
                uint constructor = reader.ReadUInt32();

                if (constructor == 0xf35c6d01)
                {
                    long reqMsgId = reader.ReadInt64();
                    byte[] resultData = reader.ReadRawBytes(reader.Remaining);

                    if (resultData.Length >= 4)
                    {
                        uint innerCons = BitConverter.ToUInt32(resultData, 0);
                        if (innerCons == 0x3072cfa1)
                        {
                            try { resultData = DecompressGzipPacked(resultData); } catch { }
                        }

                        if (resultData.Length >= 4)
                        {
                            uint checkErrorCons = BitConverter.ToUInt32(resultData, 0);
                            if ((checkErrorCons & 0xFFFFFF00) == 0x2144CA00 || checkErrorCons == 0x2144ca10)
                            {
                                using (var errReader = new TlBinaryReader(resultData))
                                {
                                    errReader.ReadUInt32();
                                    int errorCode = errReader.ReadInt32();
                                    string errorMessage = errReader.ReadString();
                                    Debug.WriteLine(string.Format("[RPC ERROR] {0}: {1}", errorCode, errorMessage));

                                    lock (_pendingRequests)
                                    {
                                        if (_pendingRequests.ContainsKey(reqMsgId))
                                        {
                                            _pendingRequests[reqMsgId].TrySetException(new InvalidOperationException(errorMessage));
                                            _pendingRequests.Remove(reqMsgId);
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    lock (_pendingRequests)
                    {
                        if (_pendingRequests.ContainsKey(reqMsgId))
                        {
                            _pendingRequests[reqMsgId].TrySetResult(resultData);
                            _pendingRequests.Remove(reqMsgId);
                        }
                    }
                }
                else if (constructor == 0x73f1f8dc)
                {
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        long innerMsgId = reader.ReadInt64();
                        int innerSeqNo = reader.ReadInt32();
                        int innerLen = reader.ReadInt32();
                        byte[] innerData = reader.ReadRawBytes(innerLen);

                        QueueAck(innerMsgId);
                        ProcessIncomingMessage(innerMsgId, innerData);
                    }
                }
                else if (constructor == 0x3072cfa1)
                {
                    try
                    {
                        byte[] decompressed = DecompressGzipPacked(messageData);
                        ProcessIncomingMessage(incomingMsgId, decompressed);
                    }
                    catch { }
                }
                else if (constructor == 0xedab4477 || constructor == 0xedab447b)
                {
                    long badMsgId = reader.ReadInt64();
                    int badSeqNo = reader.ReadInt32();
                    int errorCode = reader.ReadInt32();
                    ulong newSalt = (ulong)reader.ReadInt64();

                    Debug.WriteLine(string.Format("[RPC Engine] Обновлен Salt: 0x{0:X16} для запроса msg_id {1}", newSalt, badMsgId));
                    _storage.ServerSalt = newSalt;
                    _storage.Save(_storage.AuthKey, newSalt, _storage.TimeOffset);

                    lock (_pendingRequests)
                    {
                        if (_pendingRequests.ContainsKey(badMsgId))
                        {
                            _pendingRequests[badMsgId].TrySetException(new InvalidOperationException("BAD_SERVER_SALT"));
                            _pendingRequests.Remove(badMsgId);
                        }
                    }
                }
                else if (constructor == 0x78d4dec1 || constructor == 0x74ae4240 || constructor == 0x725b04c2 ||
                         constructor == 0x313bc7f8 || constructor == 0x4d6deea8 || constructor == 0x9010ef6f ||
                         constructor == 0xe317af7e)
                {
                    UpdateReceived?.Invoke(this, messageData);
                }
            }
        }

        private void QueueAck(long msgId)
        {
            lock (_ackQueue)
            {
                _ackQueue.Add(msgId);
                if (_ackQueue.Count >= 8 && !_isSendingAck)
                {
                    _isSendingAck = true;
                    Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        await SendAckAsync();
                    });
                }
            }
        }

        private async Task SendAckAsync()
        {
            long[] ids;
            lock (_ackQueue)
            {
                if (_ackQueue.Count == 0)
                {
                    _isSendingAck = false;
                    return;
                }
                ids = _ackQueue.ToArray();
                _ackQueue.Clear();
                _isSendingAck = false;
            }

            try
            {
                byte[] ackQuery;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0x62d6b459);
                    writer.WriteUInt32(0x1cb5c415);
                    writer.WriteInt32(ids.Length);
                    for (int i = 0; i < ids.Length; i++)
                    {
                        writer.WriteInt64(ids[i]);
                    }
                    ackQuery = writer.ToByteArray();
                }

                long msgId;
                int seqNo;
                lock (_sessionLock)
                {
                    msgId = GenerateMessageId(_storage.TimeOffset);
                    seqNo = _seqNo * 2;
                }

                byte[] encrypted = EncryptPacket(ackQuery, msgId, seqNo);
                await _transport.SendPacketAsync(encrypted);
            }
            catch { }
        }

        private static byte[] DecompressGzipPacked(byte[] packedData)
        {
            using (var reader = new TlBinaryReader(packedData))
            {
                uint cons = reader.ReadUInt32();
                byte[] gzippedBytes = reader.ReadBytes();

                int offset = 0;
                if (gzippedBytes.Length >= 10 && gzippedBytes[0] == 0x1F && gzippedBytes[1] == 0x8B)
                {
                    offset = 10;
                }

                using (var compressedStream = new MemoryStream(gzippedBytes, offset, gzippedBytes.Length - offset))
                using (var zIn = new Org.BouncyCastle.Utilities.Zlib.ZInputStream(compressedStream, true))
                using (var resultStream = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = zIn.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        resultStream.Write(buffer, 0, read);
                    }
                    return resultStream.ToArray();
                }
            }
        }

        private long GenerateMessageId(int timeOffset)
        {
            lock (_sessionLock)
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var now = DateTime.UtcNow;
                long unixSeconds = (long)(now - epoch).TotalSeconds + timeOffset;
                long millis = now.Millisecond;
                long fraction = (millis * 4294967296L) / 1000L;

                long msgId = (unixSeconds << 32) | (fraction & ~3L);

                if (msgId <= _lastMsgId)
                {
                    msgId = _lastMsgId + 4;
                }
                _lastMsgId = msgId;
                return msgId;
            }
        }
    }
}