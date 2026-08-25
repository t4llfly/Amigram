using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetroTelegram.TL
{
    public class IncomingMessageEventArgs : EventArgs
    {
        public long MsgId { get; set; }
        public long PeerId { get; set; }
        public int PeerType { get; set; }
        public long FromId { get; set; }
        public string Text { get; set; }
        public DateTime Date { get; set; }
        public bool IsOut { get; set; }
    }

    public class OutboxReadEventArgs : EventArgs
    {
        public long PeerId { get; set; }
        public int MaxId { get; set; }
    }

    public class UserTypingEventArgs : EventArgs
    {
        public long PeerId { get; set; }
        public long UserId { get; set; }
    }

    public class UpdatesService : IDisposable
    {
        private readonly TelegramRpcEngine _rpcEngine;
        private Timer _heartbeatTimer;
        private bool _isDisposed = false;

        public event EventHandler<IncomingMessageEventArgs> MessageReceived;
        public event EventHandler<OutboxReadEventArgs> OutboxRead;
        public event EventHandler<UserTypingEventArgs> UserTyping;

        public UpdatesService(TelegramRpcEngine rpcEngine)
        {
            _rpcEngine = rpcEngine;
            _rpcEngine.UpdateReceived += OnUpdateReceived;

            _heartbeatTimer = new Timer(async (s) => await SendKeepAliveAsync(), null, 20000, 45000);
        }

        public async Task SendKeepAliveAsync()
        {
            if (_isDisposed) return;

            try
            {
                byte[] queryBytes;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xedd4882a);
                    queryBytes = writer.ToByteArray();
                }

                await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 10000);
            }
            catch { }
        }

        public async Task ReadHistoryAsync(long peerId, long accessHash, int peerType, int maxId)
        {
            try
            {
                byte[] queryBytes;
                using (var writer = new TlBinaryWriter())
                {
                    long rawId = Math.Abs(peerId);

                    if (peerType == 3 && accessHash != 0)
                    {
                        writer.WriteUInt32(0xcc104937);
                        writer.WriteUInt32(0xf35aec28);
                        writer.WriteInt64(rawId);
                        writer.WriteInt64(accessHash);
                        writer.WriteInt32(maxId);
                    }
                    else
                    {
                        writer.WriteUInt32(0x0e306d3a);

                        if (peerType == 1)
                        {
                            writer.WriteUInt32(0xdde8a54c);
                            writer.WriteInt64(rawId);
                            writer.WriteInt64(accessHash);
                        }
                        else
                        {
                            writer.WriteUInt32(0x35a95cb9);
                            writer.WriteInt64(rawId);
                        }

                        writer.WriteInt32(maxId);
                    }

                    queryBytes = writer.ToByteArray();
                }

                await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false, timeoutMs: 8000);
                Debug.WriteLine(string.Format("[UpdatesService] Диалог {0} прочитан на сервере до max_id: {1}", peerId, maxId));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdatesService] Ошибка readHistory: " + ex.Message);
            }
        }

        private void OnUpdateReceived(object sender, byte[] updateData)
        {
            try
            {
                using (var reader = new TlBinaryReader(updateData))
                {
                    uint constructor = reader.ReadUInt32();
                    Debug.WriteLine(string.Format("[UpdatesService] Событие сокета: 0x{0:X8}", constructor));

                    if (constructor == 0x313bc7f8)
                    {
                        int flags = reader.ReadInt32();
                        int id = reader.ReadInt32();
                        long userId = reader.ReadInt64();
                        string text = reader.ReadString();
                        int pts = reader.ReadInt32();
                        int ptsCount = reader.ReadInt32();
                        int dateUnix = reader.ReadInt32();

                        DateTime date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(dateUnix).ToLocalTime();
                        bool isOut = (flags & 2) != 0;

                        DispatchMessage(new IncomingMessageEventArgs
                        {
                            MsgId = id,
                            PeerId = userId,
                            PeerType = 1,
                            FromId = isOut ? 0 : userId,
                            Text = text,
                            Date = date,
                            IsOut = isOut
                        });
                    }
                    else if (constructor == 0x4d6deea8)
                    {
                        int flags = reader.ReadInt32();
                        int id = reader.ReadInt32();
                        long fromId = reader.ReadInt64();
                        long chatId = reader.ReadInt64();
                        string text = reader.ReadString();
                        int pts = reader.ReadInt32();
                        int ptsCount = reader.ReadInt32();
                        int dateUnix = reader.ReadInt32();

                        DateTime date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(dateUnix).ToLocalTime();
                        bool isOut = (flags & 2) != 0;

                        DispatchMessage(new IncomingMessageEventArgs
                        {
                            MsgId = id,
                            PeerId = chatId,
                            PeerType = 2,
                            FromId = fromId,
                            Text = text,
                            Date = date,
                            IsOut = isOut
                        });
                    }
                    else if (constructor == 0x2f2f21bf)
                    {
                        ReadOutboxHistorySafe(reader);
                    }
                    else if (constructor == 0xb75f99a9)
                    {
                        ReadOutboxChannelSafe(reader);
                    }
                    else if (constructor == 0xc01e857f)
                    {
                        long userId = ReadInt64Safe(reader);
                        UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = userId, UserId = userId });
                    }
                    else if (constructor == 0x9a65ea1f)
                    {
                        long chatId = ReadInt64Safe(reader);
                        int pType;
                        long fromId = ReadPeer(reader, out pType);
                        UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = chatId, UserId = fromId });
                    }
                    else if (constructor == 0x78d4dec1)
                    {
                        uint updateCons = reader.ReadUInt32();

                        if (updateCons == 0x2f2f21bf)
                        {
                            ReadOutboxHistorySafe(reader);
                        }
                        else if (updateCons == 0xb75f99a9)
                        {
                            ReadOutboxChannelSafe(reader);
                        }
                        else if (updateCons == 0xc01e857f)
                        {
                            long userId = ReadInt64Safe(reader);
                            UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = userId, UserId = userId });
                        }
                        else if (updateCons == 0x9a65ea1f)
                        {
                            long chatId = ReadInt64Safe(reader);
                            int pType;
                            long fromId = ReadPeer(reader, out pType);
                            UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = chatId, UserId = fromId });
                        }
                        else if (updateCons == 0x8c88c923)
                        {
                            int flags = ReadInt32Safe(reader);
                            if ((flags & 1) != 0) ReadInt32Safe(reader);
                            long channelId = ReadInt64Safe(reader);
                            int pType;
                            long fromId = ReadPeer(reader, out pType);
                            UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = channelId, UserId = fromId });
                        }
                        else
                        {
                            ScanAndDispatchAllMessages(updateData);
                        }
                    }
                    else
                    {
                        ScanAndDispatchAllMessages(updateData);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdatesService] Ошибка парсинга: " + ex.Message);
            }
        }

        private void ReadOutboxHistorySafe(TlBinaryReader reader)
        {
            try
            {
                int pType;
                long pId = ReadPeer(reader, out pType);
                int maxId = ReadInt32Safe(reader);
                Debug.WriteLine(string.Format("[UpdatesService] СООБЩЕНИЯ ПРОЧИТАНЫ (Outbox) в чате {0} до msg_id {1}", pId, maxId));
                OutboxRead?.Invoke(this, new OutboxReadEventArgs { PeerId = pId, MaxId = maxId });
            }
            catch { }
        }

        private void ReadOutboxChannelSafe(TlBinaryReader reader)
        {
            try
            {
                long channelId = ReadInt64Safe(reader);
                int maxId = ReadInt32Safe(reader);
                Debug.WriteLine(string.Format("[UpdatesService] СООБЩЕНИЯ ПРОЧИТАНЫ (Channel Outbox) в канале {0} до msg_id {1}", channelId, maxId));
                OutboxRead?.Invoke(this, new OutboxReadEventArgs { PeerId = channelId, MaxId = maxId });
            }
            catch { }
        }

        private void TryCacheUser(TlBinaryReader reader, uint constructor)
        {
            try
            {
                if (constructor == 0xd3bc4b7a) { ReadInt64Safe(reader); return; }

                int startPos = reader.Position;
                try
                {
                    int flags = ReadInt32Safe(reader);
                    int flags2 = ReadInt32Safe(reader);
                    long id = ReadInt64Safe(reader);
                    if ((flags & 1) != 0) ReadInt64Safe(reader);
                    string firstName = ((flags & 2) != 0) ? ReadStringSafe(reader) : "";
                    string lastName = ((flags & 4) != 0) ? ReadStringSafe(reader) : "";
                    string username = ((flags & 8) != 0) ? ReadStringSafe(reader) : "";

                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;

                    if (id > 0 && id < 100000000000L && !string.IsNullOrEmpty(fullName))
                    {
                        App.CacheUser(id, fullName);
                        return;
                    }
                    throw new Exception();
                }
                catch
                {
                    reader.Position = startPos;
                    int flags = ReadInt32Safe(reader);
                    long id = ReadInt64Safe(reader);
                    if ((flags & 1) != 0) ReadInt64Safe(reader);
                    string firstName = ((flags & 2) != 0) ? ReadStringSafe(reader) : "";
                    string lastName = ((flags & 4) != 0) ? ReadStringSafe(reader) : "";
                    string username = ((flags & 8) != 0) ? ReadStringSafe(reader) : "";

                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;

                    if (id > 0 && id < 100000000000L && !string.IsNullOrEmpty(fullName))
                    {
                        App.CacheUser(id, fullName);
                    }
                }
            }
            catch { }
        }

        private void TryCacheChat(TlBinaryReader reader, uint constructor)
        {
            try
            {
                if (constructor == 0x6592a1a7)
                {
                    long fId = ReadInt64Safe(reader);
                    string fTitle = ReadStringSafe(reader);
                    if (fId > 0 && !string.IsNullOrEmpty(fTitle)) App.CacheUser(fId, fTitle);
                    return;
                }
                if (constructor == 0x29562865) return;

                bool isGroup = (constructor == 0x41cbf256 || constructor == 0xd63d27e7 || constructor == 0xd155047b);
                int startPos = reader.Position;

                try
                {
                    int flags = ReadInt32Safe(reader);
                    int flags2 = ReadInt32Safe(reader);
                    long id = ReadInt64Safe(reader);
                    long accessHash = (!isGroup && (flags & 8192) != 0) ? ReadInt64Safe(reader) : 0;
                    string title = ReadStringSafe(reader);

                    if (id > 0 && !string.IsNullOrEmpty(title) && title.Length < 150)
                    {
                        MetroTelegram.App.CacheUser(id, title);
                        if (accessHash != 0) MetroTelegram.App.CacheAccessHash(id, accessHash);
                        return;
                    }
                    throw new Exception();
                }
                catch
                {
                    reader.Position = startPos;
                    int flags = ReadInt32Safe(reader);
                    long id = ReadInt64Safe(reader);
                    if (!isGroup && (flags & 8192) != 0) ReadInt64Safe(reader);
                    string title = ReadStringSafe(reader);

                    if (id > 0 && !string.IsNullOrEmpty(title) && title.Length < 150)
                    {
                        MetroTelegram.App.CacheUser(id, title);
                    }
                }
            }
            catch { }
        }

        private void ScanAndDispatchAllMessages(byte[] updateData)
        {
            var parsedMessages = new List<IncomingMessageEventArgs>();
            var seenMsgIds = new HashSet<long>();

            using (var reader = new TlBinaryReader(updateData))
            {
                while (reader.Remaining >= 4)
                {
                    int startPos = reader.Position;
                    uint cons = ReadUInt32Safe(reader);

                    try
                    {
                        if (cons == 0x2f2f21bf)
                        {
                            ReadOutboxHistorySafe(reader);
                        }
                        else if (cons == 0xb75f99a9)
                        {
                            ReadOutboxChannelSafe(reader);
                        }
                        else if (cons == 0xc01e857f)
                        {
                            long userId = ReadInt64Safe(reader);
                            UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = userId, UserId = userId });
                        }
                        else if (cons == 0x9a65ea1f)
                        {
                            long chatId = ReadInt64Safe(reader);
                            int pType;
                            long fromId = ReadPeer(reader, out pType);
                            UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = chatId, UserId = fromId });
                        }
                        else if (cons == 0x8c88c923)
                        {
                            int flags = ReadInt32Safe(reader);
                            if ((flags & 1) != 0) ReadInt32Safe(reader);
                            long channelId = ReadInt64Safe(reader);
                            int pType;
                            long fromId = ReadPeer(reader, out pType);
                            UserTyping?.Invoke(this, new UserTypingEventArgs { PeerId = channelId, UserId = fromId });
                        }
                        else if (cons == 0x215c4438 || cons == 0x83314057 || cons == 0x93b272a7 || cons == 0x2e56d744 ||
                                 cons == 0xd23c81a3 || cons == 0x3ff6ecb0 || cons == 0xb1b8cc83 || cons == 0xabb5f120)
                        {
                            TryCacheUser(reader, cons);
                        }
                        else if (cons == 0xd49f34c6 || cons == 0x1c32b11c || cons == 0x833eed5d || cons == 0xa086f67e ||
                                 cons == 0x41cbf256 || cons == 0xd63d27e7 || cons == 0xd155047b || cons == 0x826fe213 ||
                                 cons == 0x0736424e || cons == 0x17d493d5 || cons == 0x65efe954 || cons == 0x6592a1a7 ||
                                 cons == 0x29562865 || cons == 0x94f592db)
                        {
                            TryCacheChat(reader, cons);
                        }
                        else if (cons == 0x76bec211 || cons == 0x9cb490e9 || cons == 0x3ae56482 || cons == 0x38116eed ||
                                 cons == 0x761450c7 || cons == 0x85d691f8 || cons == 0xaf0e3651 || cons == 0x38116ee0 ||
                                 cons == 0x7600b9d3 || cons == 0x7a800e0a || cons == 0x2b085862)
                        {
                            var msg = ReadMessageDetailSafe(reader, cons);
                            if (msg != null && msg.MsgId != 0 && !seenMsgIds.Contains(msg.MsgId))
                            {
                                seenMsgIds.Add(msg.MsgId);
                                parsedMessages.Add(msg);
                            }
                        }
                    }
                    catch { }

                    reader.Position = startPos + 1;
                }
            }

            foreach (var m in parsedMessages)
            {
                DispatchMessage(m);
            }
        }

        private IncomingMessageEventArgs ReadMessageDetailSafe(TlBinaryReader reader, uint constructor)
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

                return new IncomingMessageEventArgs
                {
                    MsgId = sMsgId,
                    PeerId = sPeerId,
                    PeerType = sPType,
                    FromId = 0,
                    Text = "[Служебное сообщение]",
                    Date = sDate,
                    IsOut = false
                };
            }

            int msgFlags = ReadInt32Safe(reader);
            int flags2 = 0;
            if (constructor == 0x76bec211 || constructor == 0x9cb490e9 || constructor == 0x3ae56482 || constructor == 0x7600b9d3)
            {
                flags2 = ReadInt32Safe(reader);
            }

            int mId = ReadInt32Safe(reader);
            bool isOut = (msgFlags & 2) != 0;

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

            if ((msgFlags & 8) != 0) SkipReplyHeaderSafe(reader);

            int dUnixSafe = ReadInt32Safe(reader);
            string msgText = ReadStringSafe(reader);

            if ((msgFlags & 512) != 0 && string.IsNullOrEmpty(msgText))
            {
                msgText = "[Вложение]";
            }

            DateTime msgDate = (dUnixSafe > 1000000000) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(dUnixSafe).ToLocalTime() : DateTime.Now;

            long targetChatId = pId;
            if (peerT == 1 && !isOut && fromId != 0)
            {
                targetChatId = fromId;
            }

            return new IncomingMessageEventArgs
            {
                MsgId = mId,
                PeerId = targetChatId,
                PeerType = peerT,
                FromId = fromId,
                Text = !string.IsNullOrEmpty(msgText) ? msgText : "[Сообщение]",
                Date = msgDate,
                IsOut = isOut
            };
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

        private void SkipReplyHeaderSafe(TlBinaryReader reader)
        {
            uint cons = ReadUInt32Safe(reader);
            int flags = ReadInt32Safe(reader);
            int pType;
            if (cons == 0xa6d57763 || cons == 0xafb67427 || cons == 0x6917560b)
            {
                ReadInt32Safe(reader);
                if ((flags & 1) != 0) ReadPeer(reader, out pType);
                if ((flags & 2) != 0) ReadInt32Safe(reader);
                if ((flags & 16) != 0) ReadInt32Safe(reader);
                if ((flags & 32) != 0) SkipFwdHeaderSafe(reader);
                if ((flags & 64) != 0) ReadStringSafe(reader);
            }
            else
            {
                if ((flags & 16) != 0) ReadInt32Safe(reader);
                if ((flags & 1) != 0) ReadInt32Safe(reader);
                if ((flags & 2) != 0) ReadPeer(reader, out pType);
                if ((flags & 32) != 0) SkipFwdHeaderSafe(reader);
            }
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

        private void DispatchMessage(IncomingMessageEventArgs args)
        {
            Debug.WriteLine(string.Format("[UpdatesService] ВХОДЯЩЕЕ СООБЩЕНИЕ! Chat ID: {0}, От: {1}, Текст: '{2}'",
                args.PeerId, args.FromId, args.Text));
            MessageReceived?.Invoke(this, args);
        }

        public void Dispose()
        {
            _isDisposed = true;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }
    }
}