using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MetroTelegram.ViewModels;
using System.Linq;

namespace MetroTelegram.TL
{
    public class MessagesService
    {
        private readonly TelegramRpcEngine _rpcEngine;
        private readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        public MessagesService(TelegramRpcEngine rpcEngine)
        {
            _rpcEngine = rpcEngine;
        }

        public async Task<List<MessageItemViewModel>> GetHistoryAsync(long peerId, long accessHash, int peerType, int limit = 30)
        {
            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0x4423e6c5);
                WriteInputPeer(writer, peerId, accessHash, peerType);
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                writer.WriteInt32(limit);
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                writer.WriteInt64(0L);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[MessagesService] Запрос истории для Peer {0} ({1} байт)...", peerId, queryBytes.Length));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return ParseHistoryResponse(response, peerType);
        }

        public async Task<long> SendMessageAsync(long peerId, long accessHash, int peerType, string text, int replyToMsgId = 0)
        {
            long randomId;
            byte[] rndBytes = new byte[8];
            _rng.GetBytes(rndBytes);
            randomId = BitConverter.ToInt64(rndBytes, 0);

            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                int flags = (replyToMsgId > 0) ? 1 : 0;

                writer.WriteUInt32(0x545cd15a);
                writer.WriteInt32(flags);

                WriteInputPeer(writer, peerId, accessHash, peerType);

                if (replyToMsgId > 0)
                {
                    writer.WriteUInt32(0x22c0f6d5);
                    writer.WriteInt32(0);
                    writer.WriteInt32(replyToMsgId);
                }

                writer.WriteString(text);
                writer.WriteInt64(randomId);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[MessagesService] Отправка сообщения (peer: {0}, random_id: 0x{1:X16})...", peerId, randomId));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return randomId;
        }

        public async Task SetTypingAsync(long peerId, long accessHash, int peerType)
        {
            try
            {
                byte[] queryBytes;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0x58943ee2);
                    writer.WriteInt32(0);
                    WriteInputPeer(writer, peerId, accessHash, peerType);
                    writer.WriteUInt32(0x16bf744e);

                    queryBytes = writer.ToByteArray();
                }

                await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 5000);
                Debug.WriteLine(string.Format("[MessagesService] Статус 'печатает...' отправлен для Peer {0}", peerId));
            }
            catch { }
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
                if (accessHash != 0)
                {
                    writer.WriteUInt32(0x27bcbbfc);
                    writer.WriteInt64(rawId);
                    writer.WriteInt64(accessHash);
                }
                else
                {
                    writer.WriteUInt32(0x35a95cb9);
                    writer.WriteInt64(rawId);
                }
            }
            else
            {
                writer.WriteUInt32(0x27bcbbfc);
                writer.WriteInt64(rawId);
                writer.WriteInt64(accessHash);
            }
        }

        private List<MessageItemViewModel> ParseHistoryResponse(byte[] response, int peerType)
        {
            var rawMessages = new List<MessageItemViewModel>();
            var usersDict = new Dictionary<long, string>();

            using (var reader = new TlBinaryReader(response))
            {
                uint topConstructor = ReadUInt32Safe(reader);
                if ((topConstructor & 0xFFFFFF00) == 0x2144CA00 || topConstructor == 0x2144ca10)
                {
                    int errorCode = ReadInt32Safe(reader);
                    string errorMessage = ReadStringSafe(reader);
                    throw new InvalidOperationException(string.Format("Telegram Error {0}: {1}", errorCode, errorMessage));
                }

                while (reader.Remaining >= 4)
                {
                    int startPos = reader.Position;
                    uint cons = ReadUInt32Safe(reader);

                    try
                    {
                        if (cons == 0x215c4438 || cons == 0x83314057 || cons == 0x93b272a7 || cons == 0x2e56d744 || cons == 0xd23c81a3 || cons == 0x3ff6ecb0 || cons == 0xb1b8cc83 || cons == 0xabb5f120)
                        {
                            var user = ReadUserSafe(reader, cons);
                            if (user != null && user.Id != 0 && !usersDict.ContainsKey(user.Id))
                            {
                                usersDict[user.Id] = user.Title;
                                App.CacheUser(user.Id, user.Title);
                            }
                        }
                        else if (cons == 0x76bec211 || cons == 0x9cb490e9 || cons == 0x3ae56482 || cons == 0x38116eed ||
                                 cons == 0x761450c7 || cons == 0x85d691f8 || cons == 0xaf0e3651 || cons == 0x38116ee0 ||
                                 cons == 0x7600b9d3 || cons == 0x7a800e0a || cons == 0x2b085862)
                        {
                            var msg = ReadMessageDetail(reader, cons);
                            if (msg != null && msg.Id != 0)
                            {
                                rawMessages.Add(msg);
                            }
                        }
                    }
                    catch { }

                    reader.Position = startPos + 1;
                }
            }

            foreach (var m in rawMessages)
            {
                if (!m.IsOutgoing && m.FromId != 0)
                {
                    string author;
                    if (usersDict.TryGetValue(m.FromId, out author))
                        m.AuthorName = author;
                    else if (peerType == 2 || peerType == 3)
                        m.AuthorName = "Участник " + m.FromId;
                }
            }

            var byId = new Dictionary<long, MessageItemViewModel>();
            foreach (var m in rawMessages)
                if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;

            foreach (var m in rawMessages)
            {
                if (m.ReplyToMsgId <= 0) continue;

                MessageItemViewModel original;
                if (byId.TryGetValue(m.ReplyToMsgId, out original))
                {
                    if (original.IsOutgoing)
                        m.ReplyAuthor = "Вы";
                    else if (!string.IsNullOrEmpty(original.AuthorName))
                        m.ReplyAuthor = original.AuthorName;
                    else if (original.FromId != 0)
                    {
                        string rAuthor;
                        m.ReplyAuthor = usersDict.TryGetValue(original.FromId, out rAuthor)
                            ? rAuthor
                            : App.GetUserName(original.FromId);
                    }
                    else
                        m.ReplyAuthor = "";

                    m.ReplyText = !string.IsNullOrEmpty(original.Text)
                        ? original.Text
                        : (original.HasPhoto ? "[Фото]" : "[Сообщение]");
                }
                else if (!string.IsNullOrEmpty(m.ReplyQuoteText))
                {
                    m.ReplyAuthor = "";
                    m.ReplyText = m.ReplyQuoteText;
                }
                else
                {
                    m.ReplyAuthor = "";
                    m.ReplyText = "Сообщение недоступно";
                }
            }

            foreach (var m in rawMessages)
            {
                if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;
            }

            foreach (var m in rawMessages)
            {
                if (m.ReplyToMsgId <= 0) continue;

                MessageItemViewModel target;
                if (byId.TryGetValue(m.ReplyToMsgId, out target))
                {
                    m.ReplyAuthor = target.IsOutgoing ? "Вы" : (target.AuthorName ?? App.GetUserName(target.FromId));
                    m.ReplyText = !string.IsNullOrEmpty(target.Text) ? target.Text
                                : (target.HasPhoto ? "[Фото]" : "[Сообщение]");
                }
                else if (!string.IsNullOrEmpty(m.ReplyQuoteText))
                {
                    m.ReplyAuthor = "";
                    m.ReplyText = m.ReplyQuoteText;
                }
                else
                {
                    m.ReplyAuthor = "";
                    m.ReplyText = "Сообщение недоступно";
                }
            }

            rawMessages.Reverse();
            return rawMessages;
        }

        private MessageItemViewModel ReadMessageDetail(TlBinaryReader reader, uint constructor)
        {
            if (constructor == 0x7a800e0a || constructor == 0x2b085862)
            {
                int sFlags = ReadInt32Safe(reader);
                int sMsgId = ReadInt32Safe(reader);
                int sPType;
                if ((sFlags & 256) != 0) ReadPeer(reader, out sPType);
                long sPeerId = ReadPeer(reader, out sPType);
                int dUnix = ReadInt32Safe(reader);
                DateTime sDate = (dUnix > 1000000000) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(dUnix).ToLocalTime() : DateTime.Now;

                return new MessageItemViewModel { Id = sMsgId, Text = "[Служебное сообщение]", Date = sDate, IsOutgoing = false, IsService = true };
            }

            int msgFlags = ReadInt32Safe(reader);
            int flags2 = 0;
            if (constructor == 0x76bec211 || constructor == 0x9cb490e9 || constructor == 0x3ae56482 || constructor == 0x7600b9d3)
            {
                flags2 = ReadInt32Safe(reader);
            }

            int mId = ReadInt32Safe(reader);
            bool isOut = (msgFlags & 2) != 0;
            Debug.WriteLine(string.Format("[MSG-TRACE] cons=0x{0:X8} flags=0x{1:X8} flags2=0x{2:X8} id={3} pos={4}",
                constructor, msgFlags, flags2, mId, reader.Position));

            int peerT;
            long fromId = ((msgFlags & 256) != 0) ? ReadPeer(reader, out peerT) : 0;
            if ((msgFlags & 536870912) != 0) ReadInt32Safe(reader);
            if ((flags2 & 4096) != 0) ReadStringSafe(reader);

            long pId = ReadPeer(reader, out peerT);
            if ((msgFlags & 268435456) != 0) ReadPeer(reader, out peerT);

            if ((msgFlags & 4) != 0) SkipFwdHeaderSafe(reader);
            if ((msgFlags & 2048) != 0) ReadInt64Safe(reader);
            if ((flags2 & 1) != 0) ReadInt64Safe(reader);
            if ((flags2 & 524288) != 0) ReadPeer(reader, out peerT);

            int replyToMsgId = 0;
            string replyQuoteText = null;
            if ((msgFlags & 8) != 0) ReadReplyHeaderSafe(reader, out replyToMsgId, out replyQuoteText);

            int dUnixSafe = ReadInt32Safe(reader);
            string msgText = ReadStringSafe(reader);

            long photoId = 0;
            long photoAccessHash = 0;
            byte[] photoFileRef = null;
            string thumbSize = "m";
            string fullThumbSize = "x";

            if ((msgFlags & 512) != 0)
            {
                ReadPhotoMedia(reader, out photoId, out photoAccessHash, out photoFileRef, out thumbSize, out fullThumbSize);
                if (photoId == 0 && string.IsNullOrEmpty(msgText))
                {
                    msgText = "[Вложение]";
                }
            }

            DateTime msgDate = (dUnixSafe > 1000000000) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(dUnixSafe).ToLocalTime() : DateTime.Now;

            Debug.WriteLine(string.Format("[MSG-TRACE]   text='{0}' pos_after_text={1}", msgText, reader.Position));
            return new MessageItemViewModel
            {
                Id = mId,
                FromId = fromId,
                Text = msgText,
                Date = msgDate,
                IsOutgoing = isOut,
                IsService = false,
                ReplyToMsgId = replyToMsgId,
                ReplyQuoteText = replyQuoteText,
                PhotoId = photoId,
                PhotoAccessHash = photoAccessHash,
                PhotoFileReference = photoFileRef,
                PhotoThumbSize = thumbSize,
                PhotoFullThumbSize = fullThumbSize
            };
        }

        private void ReadPhotoMedia(TlBinaryReader reader, out long photoId, out long accessHash, out byte[] fileRef, out string thumbSize, out string fullThumbSize)
        {
            photoId = 0;
            accessHash = 0;
            fileRef = null;
            thumbSize = "m";
            fullThumbSize = "x";

            try
            {
                uint mediaCons = ReadUInt32Safe(reader);

                if (mediaCons == 0x695150d7 || mediaCons == 0x4cf6d3d2)
                {
                    int pFlags = ReadInt32Safe(reader);
                    if ((pFlags & 1) != 0)
                    {
                        uint photoCons = ReadUInt32Safe(reader);

                        if (photoCons == 0xfb197a65 || photoCons == 0xd072acb4)
                        {
                            int photoFlags = ReadInt32Safe(reader);
                            photoId = ReadInt64Safe(reader);
                            accessHash = ReadInt64Safe(reader);
                            fileRef = ReadBytesSafe(reader);
                            int date = ReadInt32Safe(reader);

                            uint vectorCons = ReadUInt32Safe(reader);
                            int sizeCount = ReadInt32Safe(reader);

                            string bestThumb = "m";
                            string bestFull = "x";

                            for (int i = 0; i < sizeCount; i++)
                            {
                                uint sCons = ReadUInt32Safe(reader);
                                string type = ReadStringSafe(reader);

                                if (sCons == 0x75c78e60)
                                {
                                    ReadInt32Safe(reader);
                                    ReadInt32Safe(reader);
                                    ReadInt32Safe(reader);

                                    if (type == "s" || type == "m") bestThumb = type;
                                    if (type == "x" || type == "y" || type == "w" || type == "z") bestFull = type;
                                }
                                else if (sCons == 0xfa3d5507)
                                {
                                    ReadInt32Safe(reader);
                                    ReadInt32Safe(reader);
                                    uint vCons = ReadUInt32Safe(reader);
                                    int vCount = ReadInt32Safe(reader);
                                    for (int v = 0; v < vCount; v++) ReadInt32Safe(reader);

                                    if (type == "s" || type == "m") bestThumb = type;
                                    if (type == "x" || type == "y" || type == "w" || type == "z") bestFull = type;
                                }
                                else if (sCons == 0x021e1ad6 || sCons == 0x21e1ad6)
                                {
                                    ReadInt32Safe(reader);
                                    ReadInt32Safe(reader);
                                    ReadBytesSafe(reader);
                                }
                                else if (sCons == 0xe0b0bc2e)
                                {
                                    ReadBytesSafe(reader);
                                }
                            }

                            thumbSize = bestThumb;
                            fullThumbSize = bestFull;

                            if ((photoFlags & 2) != 0)
                            {
                                uint vCons = ReadUInt32Safe(reader);
                                int vCount = ReadInt32Safe(reader);
                                for (int v = 0; v < vCount; v++)
                                {
                                    ReadUInt32Safe(reader);
                                    int vf = ReadInt32Safe(reader);
                                    ReadStringSafe(reader);
                                    ReadDoubleSafe(reader);
                                }
                            }

                            int dcId = ReadInt32Safe(reader);
                        }
                    }
                }
            }
            catch { }
        }

        public async Task DeleteMessagesAsync(long peerId, long accessHash, int peerType, int messageId, bool revoke = true)
        {
            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                long rawId = Math.Abs(peerId);

                if (peerType == 3 && accessHash != 0)
                {
                    writer.WriteUInt32(0x84c1fd4e);
                    writer.WriteUInt32(0xf35aec28);
                    writer.WriteInt64(rawId);
                    writer.WriteInt64(accessHash);

                    writer.WriteUInt32(0x1cb5c415);
                    writer.WriteInt32(1);
                    writer.WriteInt32(messageId);
                }
                else
                {
                    writer.WriteUInt32(0xa26f40bd);
                    writer.WriteInt32(revoke ? 1 : 0);

                    writer.WriteUInt32(0x1cb5c415);
                    writer.WriteInt32(1);
                    writer.WriteInt32(messageId);
                }

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[MessagesService] Удаление сообщения ID {0} (peer: {1}, revoke: {2})...",
                messageId, peerId, revoke));

            await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 10000);
        }

        public async Task ForwardMessagesAsync(long fromPeerId, long fromAccessHash, int fromPeerType, long toPeerId, long toAccessHash, int toPeerType, int messageId)
        {
            long randomId;
            byte[] rndBytes = new byte[8];
            _rng.GetBytes(rndBytes);
            randomId = BitConverter.ToInt64(rndBytes, 0);

            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0xcc302905);
                writer.WriteInt32(0);

                WriteInputPeer(writer, fromPeerId, fromAccessHash, fromPeerType);

                writer.WriteUInt32(0x1cb5c415);
                writer.WriteInt32(1);
                writer.WriteInt32(messageId);

                writer.WriteUInt32(0x1cb5c415);
                writer.WriteInt32(1);
                writer.WriteInt64(randomId);

                WriteInputPeer(writer, toPeerId, toAccessHash, toPeerType);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[MessagesService] Пересылка сообщения ID {0} в Peer {1}...", messageId, toPeerId));
            await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 15000);
        }

        private double ReadDoubleSafe(TlBinaryReader reader)
        {
            try { return reader.ReadDouble(); } catch { return 0; }
        }

        private byte[] ReadBytesSafe(TlBinaryReader reader)
        {
            try { return reader.ReadBytes(); } catch { return new byte[0]; }
        }

        private PeerInfo ReadUserSafe(TlBinaryReader reader, uint constructor)
        {
            int startPos = reader.Position;
            try
            {
                int flags = ReadInt32Safe(reader);
                int flags2 = ReadInt32Safe(reader);
                long id = ReadInt64Safe(reader);
                long accessHash = ((flags & 1) != 0) ? ReadInt64Safe(reader) : 0;
                string firstName = ((flags & 2) != 0) ? ReadStringSafe(reader) : "";
                string lastName = ((flags & 4) != 0) ? ReadStringSafe(reader) : "";
                string username = ((flags & 8) != 0) ? ReadStringSafe(reader) : "";

                if (id > 0 && id < 100000000000L)
                {
                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;
                    return new PeerInfo { Id = id, Title = fullName };
                }
            }
            catch { }

            reader.Position = startPos;
            try
            {
                int flags = ReadInt32Safe(reader);
                long id = ReadInt64Safe(reader);
                long accessHash = ((flags & 1) != 0) ? ReadInt64Safe(reader) : 0;
                string firstName = ((flags & 2) != 0) ? ReadStringSafe(reader) : "";
                string lastName = ((flags & 4) != 0) ? ReadStringSafe(reader) : "";
                string username = ((flags & 8) != 0) ? ReadStringSafe(reader) : "";

                if (id > 0 && id < 100000000000L)
                {
                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;
                    return new PeerInfo { Id = id, Title = fullName };
                }
            }
            catch { }

            return null;
        }

        private void SkipFwdHeaderSafe(TlBinaryReader reader)
        {
            uint cons = ReadUInt32Safe(reader);
            int flags = ReadInt32Safe(reader);
            int pType;
            if ((flags & 1) != 0) ReadPeer(reader, out pType);
            if ((flags & 32) != 0) ReadStringSafe(reader);
            ReadInt32Safe(reader);
            if ((flags & 4) != 0) ReadInt32Safe(reader);
            if ((flags & 8) != 0) ReadStringSafe(reader);
            if ((flags & 16) != 0) { ReadPeer(reader, out pType); ReadInt32Safe(reader); }
            if ((flags & 64) != 0) ReadStringSafe(reader);
            if ((flags & 256) != 0) ReadStringSafe(reader);
            if ((flags & 512) != 0) ReadPeer(reader, out pType);
            if ((flags & 1024) != 0) ReadInt32Safe(reader);
        }

        private void ReadReplyHeaderSafe(TlBinaryReader reader, out int replyToMsgId, out string quoteText)
        {
            replyToMsgId = 0;
            quoteText = null;
            uint cons = ReadUInt32Safe(reader);
            int pType;

            if (cons == 0x6917560b)
            {
                int flags = ReadInt32Safe(reader);

                if ((flags & 16) != 0) replyToMsgId = ReadInt32Safe(reader);
                if ((flags & 1) != 0) ReadPeer(reader, out pType);
                if ((flags & 32) != 0) SkipFwdHeaderSafe(reader);
                if ((flags & 256) != 0) SkipMediaLightSafe(reader);
                if ((flags & 2) != 0) ReadInt32Safe(reader);
                if ((flags & 64) != 0) quoteText = ReadStringSafe(reader);
                if ((flags & 128) != 0) SkipMessageEntitiesSafe(reader);
                if ((flags & 1024) != 0) ReadInt32Safe(reader);
                if ((flags & 2048) != 0) ReadInt32Safe(reader);
            }
            else
            {
                int flags = ReadInt32Safe(reader);
                replyToMsgId = ReadInt32Safe(reader);
                if ((flags & 1) != 0) ReadPeer(reader, out pType);
                if ((flags & 2) != 0) ReadInt32Safe(reader);
            }
        }

        private void SkipMessageEntitiesSafe(TlBinaryReader reader)
        {
            ReadUInt32Safe(reader);
            int count = ReadInt32Safe(reader);
            for (int i = 0; i < count; i++)
            {
                uint eCons = ReadUInt32Safe(reader);
                if (eCons == 0xf1ccaaac)
                {
                    ReadInt32Safe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader);
                    continue;
                }
                ReadInt32Safe(reader); ReadInt32Safe(reader);
                if (eCons == 0x73924be0 || eCons == 0x76a6d327) ReadStringSafe(reader);
                else if (eCons == 0xdc7b1140 || eCons == 0xc8cf05f8) ReadInt64Safe(reader);
            }
        }

        private void SkipMediaLightSafe(TlBinaryReader reader)
        {
            int peekPos = reader.Position;
            uint mediaCons = ReadUInt32Safe(reader);
            reader.Position = peekPos;

            if (mediaCons == 0x3ded6320 || mediaCons == 0x9f84f49e) { ReadUInt32Safe(reader); return; }

            if (mediaCons == 0x695150d7 || mediaCons == 0x4cf6d3d2)
            {
                long pId, pAh; byte[] pRef; string t1, t2;
                ReadPhotoMedia(reader, out pId, out pAh, out pRef, out t1, out t2);
                return;
            }

            if (mediaCons == 0x52d8ccd9)
            {
                ReadUInt32Safe(reader);
                int flags = ReadInt32Safe(reader);
                if ((flags & 1) != 0)
                {
                    uint docCons = ReadUInt32Safe(reader);
                    if (docCons == 0x8fd4c4d8)
                    {
                        int dFlags = ReadInt32Safe(reader);
                        ReadInt64Safe(reader);
                        ReadInt64Safe(reader);
                        ReadBytesSafe(reader);
                        ReadInt32Safe(reader);
                        ReadStringSafe(reader);
                        ReadInt64Safe(reader);
                        if ((dFlags & 1) != 0) { ReadUInt32Safe(reader); int c = ReadInt32Safe(reader); for (int i = 0; i < c; i++) SkipPhotoSizeItemSafe(reader); }
                        if ((dFlags & 2) != 0) { ReadUInt32Safe(reader); int c = ReadInt32Safe(reader); for (int i = 0; i < c; i++) { ReadUInt32Safe(reader); ReadInt32Safe(reader); ReadStringSafe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader); } }
                        ReadInt32Safe(reader);
                        ReadUInt32Safe(reader); int attrCount = ReadInt32Safe(reader);
                        for (int i = 0; i < attrCount; i++) SkipDocumentAttributeItemSafe(reader);
                    }
                    else if (docCons == 0x36f8c871) ReadInt64Safe(reader);
                }
                if ((flags & 4) != 0) ReadInt32Safe(reader);
                return;
            }

            throw new Exception("Неизвестный тип reply_media: 0x" + mediaCons.ToString("X8"));
        }

        private void SkipPhotoSizeItemSafe(TlBinaryReader reader)
        {
            uint cons = ReadUInt32Safe(reader);
            if (cons == 0xe17e23c) { ReadStringSafe(reader); return; }
            if (cons == 0x75c78e60) { ReadStringSafe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader); return; }
            if (cons == 0x21e1ad6) { ReadStringSafe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader); ReadBytesSafe(reader); return; }
            if (cons == 0xe0b0bc2e) { ReadStringSafe(reader); ReadBytesSafe(reader); return; }
            if (cons == 0xfa3efb95) { ReadStringSafe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader); ReadUInt32Safe(reader); int c = ReadInt32Safe(reader); for (int i = 0; i < c; i++) ReadInt32Safe(reader); return; }
            if (cons == 0xd8214d41) { ReadStringSafe(reader); ReadBytesSafe(reader); return; }
        }

        private void SkipDocumentAttributeItemSafe(TlBinaryReader reader)
        {
            uint cons = ReadUInt32Safe(reader);
            if (cons == 0x15590068) { ReadStringSafe(reader); return; }
            if (cons == 0x6c37c15c) { ReadInt32Safe(reader); ReadInt32Safe(reader); return; }
            if (cons == 0x11b58939 || cons == 0x9801d2f7) return;
            if (cons == 0xef02ce6)
            {
                int f = ReadInt32Safe(reader);
                ReadDoubleSafe(reader); ReadInt32Safe(reader); ReadInt32Safe(reader);
                if ((f & 4) != 0) ReadInt32Safe(reader);
                if ((f & 16) != 0) ReadDoubleSafe(reader);
                if ((f & 32) != 0) ReadStringSafe(reader);
                return;
            }
            if (cons == 0x9852f9c6)
            {
                int f = ReadInt32Safe(reader);
                ReadInt32Safe(reader);
                if ((f & 1) != 0) ReadStringSafe(reader);
                if ((f & 2) != 0) ReadStringSafe(reader);
                if ((f & 4) != 0) ReadBytesSafe(reader);
                return;
            }
            throw new Exception("Неизвестный DocumentAttribute: 0x" + cons.ToString("X8"));
        }

        private long ReadPeer(TlBinaryReader reader, out int peerType)
        {
            uint constructor = ReadUInt32Safe(reader);
            long id = ReadInt64Safe(reader);

            if (constructor == 0x59511722 || constructor == 0x9db1fac9) { peerType = 1; return id; }
            if (constructor == 0x36c6019a || constructor == 0x36c60888 || constructor == 0xbad0e5bb) { peerType = 2; return id; }
            if (constructor == 0xa2a5371e || constructor == 0xa2a5e630 || constructor == 0xbddde42c || constructor == 0x546e164a) { peerType = 3; return id; }

            peerType = 1;
            return id;
        }

        private uint ReadUInt32Safe(TlBinaryReader reader)
        {
            if (reader.Remaining < 4) throw new Exception();
            return reader.ReadUInt32();
        }
        private int ReadInt32Safe(TlBinaryReader reader)
        {
            if (reader.Remaining < 4) throw new Exception();
            return reader.ReadInt32();
        }
        private long ReadInt64Safe(TlBinaryReader reader)
        {
            if (reader.Remaining < 8) throw new Exception();
            return reader.ReadInt64();
        }
        private string ReadStringSafe(TlBinaryReader reader)
        {
            if (reader.Remaining < 1) throw new Exception();
            byte b = reader.ReadRawBytes(1)[0];
            int len = b;
            int header = 1;
            if (b == 254)
            {
                if (reader.Remaining < 3) throw new Exception();
                byte[] b3 = reader.ReadRawBytes(3);
                len = b3[0] | (b3[1] << 8) | (b3[2] << 16);
                header = 4;
            }
            if (len < 0 || len > 100000 || reader.Remaining < len) throw new Exception();
            byte[] data = reader.ReadRawBytes(len);
            int pad = (header == 1) ? (4 - ((len + 1) % 4)) % 4 : (4 - (len % 4)) % 4;
            if (pad > 0 && reader.Remaining >= pad) reader.ReadRawBytes(pad);
            return Encoding.UTF8.GetString(data, 0, data.Length);
        }
    }
}