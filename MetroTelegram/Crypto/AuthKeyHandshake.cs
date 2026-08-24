using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MetroTelegram.Transport;
using Org.BouncyCastle.Math;

namespace MetroTelegram.Crypto
{
    public class AuthKeyHandshake
    {
        private readonly ITcpTransport _transport;
        private readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        private static long _lastMessageId = 0;
        private static readonly object _msgIdLock = new object();

        public AuthKeyHandshake(ITcpTransport transport)
        {
            _transport = transport;
        }

        public async Task<byte[]> ExecuteAsync(AuthKeyStorage storage)
        {
            TaskCompletionSource<byte[]> tcs = null;
            EventHandler<byte[]> handler = null;

            Func<Task<byte[]>> waitForPacket = () =>
            {
                tcs = new TaskCompletionSource<byte[]>();
                handler = (s, data) => tcs.TrySetResult(data);
                _transport.PacketReceived += handler;
                return tcs.Task;
            };

            Action cleanup = () =>
            {
                if (handler != null)
                {
                    _transport.PacketReceived -= handler;
                }
            };

            try
            {
                Debug.WriteLine("[Handshake] ШАГ 1: req_pq_multi...");
                byte[] nonce = new byte[16];
                _rng.GetBytes(nonce);

                byte[] reqPqPacket;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write((long)0);
                    writer.Write(GenerateMessageId(0));
                    writer.Write(20);

                    writer.Write((uint)0xbe7e8ef1);
                    writer.Write(nonce);

                    reqPqPacket = ms.ToArray();
                }

                var receiveTask = waitForPacket();
                await _transport.SendPacketAsync(reqPqPacket);
                byte[] response1 = await receiveTask;
                cleanup();

                if (response1 == null || response1.Length < 20)
                {
                    throw new IOException("Сервер закрыл соединение на шаге 1 (resPQ).");
                }

                byte[] serverNonce = new byte[16];
                byte[] pqRawBytes;
                ulong pqValue = 0;
                List<long> fingerprints = new List<long>();

                using (MemoryStream ms = new MemoryStream(response1))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    ms.Seek(20, SeekOrigin.Begin);
                    uint constructor = reader.ReadUInt32();

                    byte[] respNonce = reader.ReadBytes(16);
                    serverNonce = reader.ReadBytes(16);

                    pqRawBytes = ReadTlBytes(reader);
                    for (int i = 0; i < pqRawBytes.Length; i++)
                    {
                        pqValue = (pqValue << 8) | pqRawBytes[i];
                    }

                    uint vectorConst = reader.ReadUInt32();
                    int fpCount = reader.ReadInt32();
                    for (int i = 0; i < fpCount; i++)
                    {
                        fingerprints.Add(reader.ReadInt64());
                    }
                }

                Debug.WriteLine($"[Handshake] Шаг 1 OK: pq = {pqValue}");

                Debug.WriteLine("[Handshake] ШАГ 2: Факторизация pq и RSA_PAD...");
                ulong p, q;
                Factorizer.Factorize(pqValue, out p, out q);

                byte[] newNonce = new byte[32];
                _rng.GetBytes(newNonce);

                long selectedFp;
                TelegramPublicKey rsaKey;
                TelegramRsa.FindMatchingKey(fingerprints, out selectedFp, out rsaKey);

                byte[] plainInnerData;
                using (MemoryStream innerMs = new MemoryStream())
                using (BinaryWriter innerWriter = new BinaryWriter(innerMs))
                {
                    innerWriter.Write((uint)0xa9f55f95);
                    TelegramRsa.WriteTlBytes(innerWriter, pqRawBytes);
                    TelegramRsa.WriteTlBytes(innerWriter, GetBigEndianBytes(p));
                    TelegramRsa.WriteTlBytes(innerWriter, GetBigEndianBytes(q));
                    innerWriter.Write(nonce);
                    innerWriter.Write(serverNonce);
                    innerWriter.Write(newNonce);
                    innerWriter.Write(_transport.CurrentDc.Id);

                    plainInnerData = innerMs.ToArray();
                }

                byte[] encryptedInnerData = CreateRsaPadEncryptedData(plainInnerData, rsaKey);

                byte[] reqDhPacket;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write((long)0);
                    writer.Write(GenerateMessageId(0));

                    using (MemoryStream tlMs = new MemoryStream())
                    using (BinaryWriter tlWriter = new BinaryWriter(tlMs))
                    {
                        tlWriter.Write((uint)0xd712e4be);
                        tlWriter.Write(nonce);
                        tlWriter.Write(serverNonce);
                        TelegramRsa.WriteTlBytes(tlWriter, GetBigEndianBytes(p));
                        TelegramRsa.WriteTlBytes(tlWriter, GetBigEndianBytes(q));
                        tlWriter.Write(selectedFp);
                        TelegramRsa.WriteTlBytes(tlWriter, encryptedInnerData);

                        byte[] tlBytes = tlMs.ToArray();
                        writer.Write(tlBytes.Length);
                        writer.Write(tlBytes);
                    }

                    reqDhPacket = ms.ToArray();
                }

                receiveTask = waitForPacket();
                await _transport.SendPacketAsync(reqDhPacket);
                byte[] response2 = await receiveTask;
                cleanup();

                if (response2 == null || response2.Length < 20)
                {
                    throw new IOException("Сервер закрыл соединение на шаге 2 (req_DH_params).");
                }

                byte[] encryptedAnswer;
                using (MemoryStream ms = new MemoryStream(response2))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    ms.Seek(20, SeekOrigin.Begin);
                    uint constructor = reader.ReadUInt32();
                    if (constructor != 0xd0e8075c)
                    {
                        throw new InvalidDataException(string.Format("Ошибка шага 2: 0x{0:X8}", constructor));
                    }

                    reader.ReadBytes(32);
                    encryptedAnswer = ReadTlBytes(reader);
                }

                byte[] tmpAesKey = new byte[32];
                byte[] tmpAesIv = new byte[32];
                ComputeTmpAesKeys(newNonce, serverNonce, tmpAesKey, tmpAesIv);

                byte[] decryptedDh = AesIge.Decrypt(encryptedAnswer, tmpAesKey, tmpAesIv);

                int g;
                byte[] dhPrimeBytes;
                byte[] gABytes;
                int serverTime;

                using (MemoryStream ms = new MemoryStream(decryptedDh))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    byte[] answerHash = reader.ReadBytes(20);
                    uint constructor = reader.ReadUInt32();
                    reader.ReadBytes(32);
                    g = reader.ReadInt32();
                    dhPrimeBytes = ReadTlBytes(reader);
                    gABytes = ReadTlBytes(reader);
                    serverTime = reader.ReadInt32();
                }

                Debug.WriteLine("[Handshake] Шаг 2 OK: DH-параметры получены!");

                Debug.WriteLine("[Handshake] ШАГ 3: Вычисление и верификация AuthKey...");
                BigInteger dhPrime = new BigInteger(1, dhPrimeBytes);
                BigInteger gA = new BigInteger(1, gABytes);

                byte[] bBytes = new byte[256];
                _rng.GetBytes(bBytes);
                BigInteger b = new BigInteger(1, bBytes);

                BigInteger gB = BigInteger.ValueOf(g).ModPow(b, dhPrime);
                byte[] gBBytes = Align256(gB.ToByteArrayUnsigned());

                BigInteger authKeyBigInt = gA.ModPow(b, dhPrime);
                byte[] authKey = Align256(authKeyBigInt.ToByteArrayUnsigned());

                byte[] encryptedClientDh;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write((uint)0x6643b654);
                    writer.Write(nonce);
                    writer.Write(serverNonce);
                    writer.Write((long)0);
                    TelegramRsa.WriteTlBytes(writer, gBBytes);

                    byte[] clientDhPlain = ms.ToArray();
                    using (SHA1Managed sha1 = new SHA1Managed())
                    {
                        byte[] clientDhHash = sha1.ComputeHash(clientDhPlain);
                        int paddedLen = 20 + clientDhPlain.Length;
                        if (paddedLen % 16 != 0) paddedLen += (16 - (paddedLen % 16));

                        byte[] blockToEncrypt = new byte[paddedLen];
                        Buffer.BlockCopy(clientDhHash, 0, blockToEncrypt, 0, 20);
                        Buffer.BlockCopy(clientDhPlain, 0, blockToEncrypt, 20, clientDhPlain.Length);

                        encryptedClientDh = AesIge.Encrypt(blockToEncrypt, tmpAesKey, tmpAesIv);
                    }
                }

                byte[] setDhPacket;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write((long)0);
                    writer.Write(GenerateMessageId(0));

                    using (MemoryStream tlMs = new MemoryStream())
                    using (BinaryWriter tlWriter = new BinaryWriter(tlMs))
                    {
                        tlWriter.Write((uint)0xf5045f1f);
                        tlWriter.Write(nonce);
                        tlWriter.Write(serverNonce);
                        TelegramRsa.WriteTlBytes(tlWriter, encryptedClientDh);

                        byte[] tlBytes = tlMs.ToArray();
                        writer.Write(tlBytes.Length);
                        writer.Write(tlBytes);
                    }

                    setDhPacket = ms.ToArray();
                }

                receiveTask = waitForPacket();
                await _transport.SendPacketAsync(setDhPacket);
                byte[] response3 = await receiveTask;
                cleanup();

                if (response3 == null || response3.Length < 20)
                {
                    throw new IOException("Сервер закрыл соединение на шаге 3 (set_client_DH_params).");
                }

                byte[] serverNewNonceHash1;
                using (MemoryStream ms = new MemoryStream(response3))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    ms.Seek(20, SeekOrigin.Begin);
                    uint constructor = reader.ReadUInt32();
                    if (constructor != 0x3bcbf734)
                    {
                        throw new InvalidDataException(string.Format("Ошибка генерации DH: 0x{0:X8}", constructor));
                    }

                    reader.ReadBytes(32);
                    serverNewNonceHash1 = reader.ReadBytes(16);
                }

                byte[] expectedHash1 = ComputeNewNonceHash(authKey, newNonce, 1);
                if (!ByteArraysEqual(serverNewNonceHash1, expectedHash1))
                {
                    throw new CryptographicException("Критическая ошибка: AuthKey не прошел верификацию new_nonce_hash1!");
                }

                ulong salt = BitConverter.ToUInt64(newNonce, 0) ^ BitConverter.ToUInt64(serverNonce, 0);
                int timeOffset = serverTime - (int)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

                storage.Save(authKey, salt, timeOffset);

                Debug.WriteLine($"[Handshake] КЛЮЧ 100% ВЕРИФИЦИРОВАН! AuthKey ID: 0x{storage.AuthKeyId:X16}, Salt: 0x{salt:X16}");

                return authKey;
            }
            finally
            {
                cleanup();
            }
        }

        private static byte[] Align256(byte[] data)
        {
            if (data.Length == 256) return data;
            if (data.Length > 256)
            {
                byte[] trimmed = new byte[256];
                Buffer.BlockCopy(data, data.Length - 256, trimmed, 0, 256);
                return trimmed;
            }
            byte[] padded = new byte[256];
            Buffer.BlockCopy(data, 0, padded, 256 - data.Length, data.Length);
            return padded;
        }

        private static byte[] ComputeNewNonceHash(byte[] authKey, byte[] newNonce, byte number)
        {
            using (SHA1Managed sha1 = new SHA1Managed())
            {
                byte[] authKeyAux = new byte[8];
                byte[] fullSha = sha1.ComputeHash(authKey);
                Buffer.BlockCopy(fullSha, 0, authKeyAux, 0, 8);

                byte[] toHash = new byte[32 + 1 + 8];
                Buffer.BlockCopy(newNonce, 0, toHash, 0, 32);
                toHash[32] = number;
                Buffer.BlockCopy(authKeyAux, 0, toHash, 33, 8);

                byte[] hash = sha1.ComputeHash(toHash);
                byte[] result = new byte[16];
                Buffer.BlockCopy(hash, 4, result, 0, 16);
                return result;
            }
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private byte[] CreateRsaPadEncryptedData(byte[] data, TelegramPublicKey rsaKey)
        {
            byte[] dataWithPadding = new byte[192];
            Buffer.BlockCopy(data, 0, dataWithPadding, 0, data.Length);
            byte[] randomPad = new byte[192 - data.Length];
            _rng.GetBytes(randomPad);
            Buffer.BlockCopy(randomPad, 0, dataWithPadding, data.Length, randomPad.Length);

            byte[] dataPadReversed = new byte[192];
            for (int i = 0; i < 192; i++)
            {
                dataPadReversed[i] = dataWithPadding[191 - i];
            }

            while (true)
            {
                byte[] tempKey = new byte[32];
                _rng.GetBytes(tempKey);

                byte[] hash;
                using (SHA256Managed sha256 = new SHA256Managed())
                {
                    byte[] toHash = new byte[32 + 192];
                    Buffer.BlockCopy(tempKey, 0, toHash, 0, 32);
                    Buffer.BlockCopy(dataWithPadding, 0, toHash, 32, 192);
                    hash = sha256.ComputeHash(toHash);
                }

                byte[] dataWithHash = new byte[224];
                Buffer.BlockCopy(dataPadReversed, 0, dataWithHash, 0, 192);
                Buffer.BlockCopy(hash, 0, dataWithHash, 192, 32);

                byte[] zeroIv = new byte[32];
                byte[] aesEncrypted = AesIge.Encrypt(dataWithHash, tempKey, zeroIv);

                byte[] tempKeyXor = new byte[32];
                using (SHA256Managed sha256 = new SHA256Managed())
                {
                    byte[] aesHash = sha256.ComputeHash(aesEncrypted);
                    for (int i = 0; i < 32; i++)
                    {
                        tempKeyXor[i] = (byte)(tempKey[i] ^ aesHash[i]);
                    }
                }

                byte[] keyAesEncrypted = new byte[256];
                Buffer.BlockCopy(tempKeyXor, 0, keyAesEncrypted, 0, 32);
                Buffer.BlockCopy(aesEncrypted, 0, keyAesEncrypted, 32, 224);

                BigInteger keyNumber = new BigInteger(1, keyAesEncrypted);
                if (keyNumber.CompareTo(rsaKey.Modulus) >= 0)
                {
                    continue;
                }

                return TelegramRsa.EncryptWithRsa(rsaKey, keyAesEncrypted);
            }
        }

        private static long GenerateMessageId(int timeOffset)
        {
            lock (_msgIdLock)
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var now = DateTime.UtcNow;
                long unixSeconds = (long)(now - epoch).TotalSeconds + timeOffset;
                long millis = now.Millisecond;
                long fraction = (millis * 4294967296L) / 1000L;

                long msgId = (unixSeconds << 32) | (fraction & ~3L);

                if (msgId <= _lastMessageId)
                {
                    msgId = _lastMessageId + 4;
                }
                _lastMessageId = msgId;
                return msgId;
            }
        }

        private static void ComputeTmpAesKeys(byte[] newNonce, byte[] serverNonce, byte[] key, byte[] iv)
        {
            using (SHA1Managed sha1 = new SHA1Managed())
            {
                byte[] buf1 = new byte[newNonce.Length + serverNonce.Length];
                Buffer.BlockCopy(newNonce, 0, buf1, 0, newNonce.Length);
                Buffer.BlockCopy(serverNonce, 0, buf1, newNonce.Length, serverNonce.Length);
                byte[] hash1 = sha1.ComputeHash(buf1);

                byte[] buf2 = new byte[serverNonce.Length + newNonce.Length];
                Buffer.BlockCopy(serverNonce, 0, buf2, 0, serverNonce.Length);
                Buffer.BlockCopy(newNonce, 0, buf2, serverNonce.Length, newNonce.Length);
                byte[] hash2 = sha1.ComputeHash(buf2);

                byte[] buf3 = new byte[newNonce.Length + newNonce.Length];
                Buffer.BlockCopy(newNonce, 0, buf3, 0, newNonce.Length);
                Buffer.BlockCopy(newNonce, 0, buf3, newNonce.Length, newNonce.Length);
                byte[] hash3 = sha1.ComputeHash(buf3);

                Buffer.BlockCopy(hash1, 0, key, 0, 20);
                Buffer.BlockCopy(hash2, 0, key, 20, 12);

                Buffer.BlockCopy(hash2, 12, iv, 0, 8);
                Buffer.BlockCopy(hash3, 0, iv, 8, 20);
                Buffer.BlockCopy(newNonce, 0, iv, 28, 4);
            }
        }

        private static byte[] ReadTlBytes(BinaryReader reader)
        {
            byte b = reader.ReadByte();
            int length = b;
            int headerLen = 1;

            if (b == 254)
            {
                byte b0 = reader.ReadByte();
                byte b1 = reader.ReadByte();
                byte b2 = reader.ReadByte();
                length = b0 | (b1 << 8) | (b2 << 16);
                headerLen = 4;
            }

            byte[] data = reader.ReadBytes(length);
            int pad = (4 - ((length + headerLen) % 4)) % 4;
            if (pad > 0) reader.ReadBytes(pad);

            return data;
        }

        private static byte[] GetBigEndianBytes(ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            int start = 0;
            while (start < bytes.Length - 1 && bytes[start] == 0) start++;
            byte[] trimmed = new byte[bytes.Length - start];
            Buffer.BlockCopy(bytes, start, trimmed, 0, trimmed.Length);
            return trimmed;
        }
    }
}