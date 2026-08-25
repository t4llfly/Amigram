using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MetroTelegram.Crypto;
using MetroTelegram.Transport;
using System.Threading;

namespace MetroTelegram.TL
{
    public class MediaService
    {
        private readonly TelegramRpcEngine _defaultRpcEngine;
        private static readonly Dictionary<string, byte[]> _rawImageCache = new Dictionary<string, byte[]>();
        private static readonly Dictionary<int, TelegramRpcEngine> _dcEngines = new Dictionary<int, TelegramRpcEngine>();
        private static readonly SemaphoreSlim _dcLock = new SemaphoreSlim(1, 1);

        public MediaService(TelegramRpcEngine rpcEngine)
        {
            _defaultRpcEngine = rpcEngine;
        }

        public async Task<byte[]> LoadAvatarBytesAsync(long peerId, long accessHash, int peerType, long photoId)
        {
            if (photoId == 0 || peerId == 0) return null;

            string cacheKey = string.Format("avatar_{0}_{1}", peerId, photoId);
            lock (_rawImageCache)
            {
                if (_rawImageCache.ContainsKey(cacheKey))
                    return _rawImageCache[cacheKey];
            }

            TelegramRpcEngine currentEngine = _defaultRpcEngine;

            while (true)
            {
                try
                {
                    byte[] queryBytes;
                    using (var writer = new TlBinaryWriter())
                    {
                        writer.WriteUInt32(0xbe5335be);
                        writer.WriteInt32(0);

                        writer.WriteUInt32(0x37257e99);
                        writer.WriteInt32(0);

                        WriteInputPeer(writer, peerId, accessHash, peerType);
                        writer.WriteInt64(photoId);

                        writer.WriteInt64(0L);
                        writer.WriteInt32(65536);

                        queryBytes = writer.ToByteArray();
                    }

                    byte[] response = await currentEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 10000);
                    byte[] imageBytes = ParseFileResponse(response);

                    if (imageBytes != null && imageBytes.Length > 0)
                    {
                        lock (_rawImageCache)
                        {
                            _rawImageCache[cacheKey] = imageBytes;
                        }
                        Debug.WriteLine(string.Format("[MediaService] Аватар для {0} успешно загружен ({1} байт)", peerId, imageBytes.Length));
                        return imageBytes;
                    }

                    return null;
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.Contains("FILE_MIGRATE_"))
                    {
                        int targetDc = ExtractTargetDc(ex.Message);
                        Debug.WriteLine(string.Format("[MediaService] Аватар {0} лежит на DC{1}. Подключение...", peerId, targetDc));

                        currentEngine = await GetOrCreateDcEngineAsync(targetDc);
                        continue;
                    }

                    Debug.WriteLine(string.Format("[MediaService] Ошибка аватара {0}: {1}", peerId, ex.Message));
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format("[MediaService] Ошибка сети аватара {0}: {1}", peerId, ex.Message));
                    return null;
                }
            }
        }

        private void WriteInputPeer(TlBinaryWriter writer, long peerId, long accessHash, int peerType)
        {
            long rawId = Math.Abs(peerId);

            if (peerType == 1)
            {
                writer.WriteUInt32(0xdde8a54c);
                writer.WriteInt64(rawId);
                writer.WriteInt64(accessHash);
            }
            else if (peerType == 2)
            {
                writer.WriteUInt32(0x35a956c2);
                writer.WriteInt64(rawId);
            }
            else
            {
                writer.WriteUInt32(0x27bcbbfc);
                writer.WriteInt64(rawId);
                writer.WriteInt64(accessHash);
            }
        }

        public async Task<byte[]> LoadPhotoBytesAsync(long photoId, long accessHash, byte[] fileReference, string thumbSize = "m")
        {
            if (photoId == 0 || fileReference == null || fileReference.Length == 0)
                return null;

            string cacheKey = string.Format("{0}_{1}", photoId, thumbSize);
            lock (_rawImageCache)
            {
                if (_rawImageCache.ContainsKey(cacheKey))
                    return _rawImageCache[cacheKey];
            }

            TelegramRpcEngine currentEngine = _defaultRpcEngine;
            int targetDcId = App.Storage.CurrentDcId;

            while (true)
            {
                try
                {
                    byte[] queryBytes;
                    using (var writer = new TlBinaryWriter())
                    {
                        writer.WriteUInt32(0xbe5335be);
                        writer.WriteInt32(0);

                        writer.WriteUInt32(0x40181ffe);
                        writer.WriteInt64(photoId);
                        writer.WriteInt64(accessHash);
                        writer.WriteBytes(fileReference);
                        writer.WriteString(thumbSize);

                        writer.WriteInt64(0L);
                        writer.WriteInt32(131072);

                        queryBytes = writer.ToByteArray();
                    }

                    Debug.WriteLine(string.Format("[MediaService] Запрос фото ID {0} к DC{1} ({2} байт)...", photoId, targetDcId, queryBytes.Length));
                    byte[] response = await currentEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 15000);

                    byte[] imageBytes = ParseFileResponse(response);
                    if (imageBytes != null && imageBytes.Length > 0)
                    {
                        lock (_rawImageCache)
                        {
                            _rawImageCache[cacheKey] = imageBytes;
                        }

                        Debug.WriteLine(string.Format("[MediaService] Фото {0} УСПЕШНО СКАЧАНО! Размер: {1} байт", photoId, imageBytes.Length));
                        return imageBytes;
                    }

                    return null;
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.Contains("FILE_MIGRATE_"))
                    {
                        int targetDc = ExtractTargetDc(ex.Message);
                        Debug.WriteLine(string.Format("[MediaService] Фото {0} лежит на DC{1}. Подключение к файловому серверу...", photoId, targetDc));

                        currentEngine = await GetOrCreateDcEngineAsync(targetDc);
                        targetDcId = targetDc;
                        continue;
                    }

                    Debug.WriteLine(string.Format("[MediaService] Ошибка загрузки фото {0}: {1}", photoId, ex.Message));
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format("[MediaService] Ошибка сети для фото {0}: {1}", photoId, ex.Message));
                    return null;
                }
            }
        }

        private async Task<TelegramRpcEngine> GetOrCreateDcEngineAsync(int dcId)
        {
            await _dcLock.WaitAsync();
            try
            {
                if (_dcEngines.ContainsKey(dcId))
                    return _dcEngines[dcId];

                DataCenter dc = DataCenter.GetDc(dcId);
                Debug.WriteLine(string.Format("[MediaService] Создание изолированной сессии для DC{0}...", dcId));

                var transport = new MtprotoTcpTransport();
                await transport.ConnectAsync(dc);

                var tempStorage = new AuthKeyStorage(isInMemoryOnly: true);
                tempStorage.CurrentDcId = dcId;

                var handshake = new AuthKeyHandshake(transport);
                await handshake.ExecuteAsync(tempStorage);

                var engine = new TelegramRpcEngine(transport, tempStorage);

                byte[] configQuery;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xc4f9186b);
                    configQuery = writer.ToByteArray();
                }
                await engine.SendRpcQueryAsync(configQuery, wrapInitConnection: true);

                Debug.WriteLine(string.Format("[MediaService] Экспорт авторизации на DC{0}...", dcId));
                byte[] exportQuery;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xe5bfffcd);
                    writer.WriteInt32(dcId);
                    exportQuery = writer.ToByteArray();
                }

                byte[] exportResponse = await _defaultRpcEngine.SendRpcQueryAsync(exportQuery, wrapInitConnection: false);

                long authId = 0;
                byte[] authBytes = null;
                using (var reader = new TlBinaryReader(exportResponse))
                {
                    uint cons = reader.ReadUInt32();
                    authId = reader.ReadInt64();
                    authBytes = reader.ReadBytes();
                }

                if (authBytes != null && authBytes.Length > 0)
                {
                    Debug.WriteLine(string.Format("[MediaService] Импорт авторизации на DC{0} (ID: {1})...", dcId, authId));
                    byte[] importQuery;
                    using (var writer = new TlBinaryWriter())
                    {
                        writer.WriteUInt32(0xa57a7dad);
                        writer.WriteInt64(authId);
                        writer.WriteBytes(authBytes);
                        importQuery = writer.ToByteArray();
                    }

                    await engine.SendRpcQueryAsync(importQuery, wrapInitConnection: false);
                    Debug.WriteLine(string.Format("[MediaService] Авторизация на DC{0} УСПЕШНО ЗАВЕРШЕНА!", dcId));
                }

                _dcEngines[dcId] = engine;
                return engine;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format("[MediaService] Ошибка сессии DC{0}: {1}", dcId, ex.Message));
                throw;
            }
            finally
            {
                _dcLock.Release();
            }
        }

        private int ExtractTargetDc(string message)
        {
            int idx = message.LastIndexOf('_');
            if (idx >= 0 && idx < message.Length - 1)
            {
                int dc;
                if (int.TryParse(message.Substring(idx + 1).Trim(), out dc))
                {
                    return dc;
                }
            }
            return 4;
        }

        private byte[] ParseFileResponse(byte[] response)
        {
            using (var reader = new TlBinaryReader(response))
            {
                uint constructor = reader.ReadUInt32();
                if ((constructor & 0xFFFFFF00) == 0x2144CA00 || constructor == 0x2144ca10)
                {
                    int errorCode = reader.ReadInt32();
                    string errorMessage = reader.ReadString();
                    throw new InvalidOperationException(errorMessage);
                }

                if (constructor == 0x096a18d5)
                {
                    uint typeCons = reader.ReadUInt32();
                    int mtime = reader.ReadInt32();
                    byte[] fileBytes = reader.ReadBytes();
                    return fileBytes;
                }

                while (reader.Remaining >= 8)
                {
                    int startPos = reader.Position;
                    byte b = reader.ReadRawBytes(1)[0];
                    if (b == 254 && reader.Remaining >= 3)
                    {
                        reader.Position = startPos;
                        return reader.ReadBytes();
                    }
                    reader.Position = startPos + 1;
                }
            }

            return null;
        }
    }
}