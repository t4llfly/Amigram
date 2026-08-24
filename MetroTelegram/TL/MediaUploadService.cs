using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MetroTelegram.TL
{
    public class MediaUploadService
    {
        private readonly TelegramRpcEngine _rpcEngine;
        private readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        public MediaUploadService(TelegramRpcEngine rpcEngine)
        {
            _rpcEngine = rpcEngine;
        }
        public async Task<long> UploadPhotoBytesAsync(byte[] fileBytes, Action<int, int> progressCallback = null)
        {
            if (fileBytes == null || fileBytes.Length == 0)
                throw new ArgumentException("Файл пуст.");

            byte[] idBytes = new byte[8];
            _rng.GetBytes(idBytes);
            long fileId = BitConverter.ToInt64(idBytes, 0);

            int chunkSize = 32768;
            int totalParts = (fileBytes.Length + chunkSize - 1) / chunkSize;

            Debug.WriteLine(string.Format("[MediaUpload] Загрузка файла (FileId: 0x{0:X16}, Размер: {1}b, Частей: {2})...",
                fileId, fileBytes.Length, totalParts));

            for (int part = 0; part < totalParts; part++)
            {
                int offset = part * chunkSize;
                int count = Math.Min(chunkSize, fileBytes.Length - offset);

                byte[] partBytes = new byte[count];
                Buffer.BlockCopy(fileBytes, offset, partBytes, 0, count);

                byte[] queryBytes;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xb304a621);
                    writer.WriteInt64(fileId);
                    writer.WriteInt32(part);
                    writer.WriteBytes(partBytes);

                    queryBytes = writer.ToByteArray();
                }

                await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 15000);
                progressCallback?.Invoke(part + 1, totalParts);

                await Task.Delay(10);
            }

            Debug.WriteLine(string.Format("[MediaUpload] Загрузка файла 0x{0:X16} завершена!", fileId));
            return fileId;
        }

        public async Task SendPhotoMessageAsync(long peerId, long accessHash, int peerType, long fileId, int totalParts, byte[] photoBytes, string caption = "")
        {
            byte[] rndBytes = new byte[8];
            _rng.GetBytes(rndBytes);
            long randomId = BitConverter.ToInt64(rndBytes, 0);

            string md5Checksum = ComputeMd5Hex(photoBytes);

            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0x0330e77f);
                writer.WriteInt32(0);

                WriteInputPeer(writer, peerId, accessHash, peerType);

                writer.WriteUInt32(0x1e287d04);
                writer.WriteInt32(0);

                writer.WriteUInt32(0xf52ff27f);
                writer.WriteInt64(fileId);
                writer.WriteInt32(totalParts);
                writer.WriteString("photo.jpg");
                writer.WriteString(md5Checksum);

                writer.WriteString(caption ?? "");

                writer.WriteInt64(randomId);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[MediaUpload] Публикация messages.sendMedia (Peer: {0}, FileId: 0x{1:X16}, MD5: {2}, Размер: {3}b)...",
                peerId, fileId, md5Checksum, queryBytes.Length));

            await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 25000);
        }

        private static string ComputeMd5Hex(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            var digest = new Org.BouncyCastle.Crypto.Digests.MD5Digest();
            digest.BlockUpdate(data, 0, data.Length);
            byte[] hash = new byte[16];
            digest.DoFinal(hash, 0);

            var sb = new StringBuilder();
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2"));
            }
            return sb.ToString();
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
    }
}