using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using MetroTelegram.ViewModels;

namespace MetroTelegram.TL
{
    public class PeerInfo
    {
        public long Id { get; set; }
        public long AccessHash { get; set; }
        public long PhotoId { get; set; }
        public string Title { get; set; }
        public string Initials { get; set; }
        public bool IsChannel { get; set; }
        public bool IsGroup { get; set; }
    }

    public class DialogsService
    {
        private readonly TelegramRpcEngine _rpcEngine;

        public DialogsService(TelegramRpcEngine rpcEngine)
        {
            _rpcEngine = rpcEngine;
        }

        public async Task<List<ChatItemViewModel>> GetDialogsAsync(int limit = 30)
        {
            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0xa0f4cb4f);
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                writer.WriteUInt32(0x7f3b18ea);
                writer.WriteInt32(limit);
                writer.WriteInt64(0L);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[DialogsService] Запрос messages.getDialogs ({0} байт)...", queryBytes.Length));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return ParseDialogsResponse(response);
        }

        private List<ChatItemViewModel> ParseDialogsResponse(byte[] response)
        {
            var result = new List<ChatItemViewModel>();

            var peersDict = new Dictionary<long, PeerInfo>();
            var messagesDict = new Dictionary<long, RawMessage>();
            var peerLastMsgDict = new Dictionary<long, RawMessage>();
            var rawDialogs = new List<RawDialog>();

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
                            if (user != null && user.Id != 0 && !peersDict.ContainsKey(user.Id))
                            {
                                peersDict[user.Id] = user;
                                App.CacheUser(user.Id, user.Title);
                            }
                        }
                        else if (cons == 0xd49f34c6 || cons == 0x1c32b11c || cons == 0x833eed5d || cons == 0xa086f67e ||
                                 cons == 0x41cbf256 || cons == 0xd63d27e7 || cons == 0xd155047b || cons == 0x826fe213 ||
                                 cons == 0x0736424e || cons == 0x17d493d5 || cons == 0x65efe954 || cons == 0x6592a1a7 || 
                                 cons == 0x29562865 || cons == 0x94f592db)
                        {
                            if (cons != 0x41cbf256 && cons != 0x6592a1a7 && cons != 0x29562865)
                            {
                                int dumpLen = Math.Min(60, reader.Remaining);
                                int savedPos = reader.Position;
                                byte[] raw = reader.ReadRawBytes(dumpLen);
                                reader.Position = savedPos;
                            }

                            var chat = ReadChatSafe(reader, cons);
                            if (chat != null && chat.Id != 0 && !peersDict.ContainsKey(chat.Id))
                            {
                                peersDict[chat.Id] = chat;
                                peersDict[-chat.Id] = chat;
                            }
                        }
                        else if (cons == 0xd58a08c6 || cons == 0xfc89f7f3)
                        {
                            int flags = ReadInt32Safe(reader);
                            bool isPinned = (flags & 4) != 0;
                            int pType;
                            long peerId = ReadPeer(reader, out pType);
                            int topMsg = ReadInt32Safe(reader);
                            int readInbox = ReadInt32Safe(reader);
                            int readOutbox = ReadInt32Safe(reader);
                            int unread = ReadInt32Safe(reader);

                            if (peerId != 0)
                            {
                                rawDialogs.Add(new RawDialog { 
                                    PeerId = peerId, 
                                    PeerType = pType, 
                                    TopMessageId = topMsg, 
                                    UnreadCount = unread,
                                    IsPinned = isPinned 
                                });
                            }
                        }
                        else if (cons == 0x76bec211 || cons == 0x9cb490e9 || cons == 0x3ae56482 || cons == 0x38116eed ||
                                 cons == 0x761450c7 || cons == 0x85d691f8 || cons == 0xaf0e3651 || cons == 0x38116ee0 ||
                                 cons == 0x7600b9d3 || cons == 0x7a800e0a || cons == 0x2b085862)
                        {
                            var msg = ReadMessageTextSafe(reader, cons);
                            if (msg != null && msg.Id != 0)
                            {
                                if (!messagesDict.ContainsKey(msg.Id)) messagesDict[msg.Id] = msg;
                                if (msg.PeerId != 0 && !peerLastMsgDict.ContainsKey(msg.PeerId)) peerLastMsgDict[msg.PeerId] = msg;
                            }
                        }
                    }
                    catch { }

                    reader.Position = startPos + 1;
                }
            }

            Debug.WriteLine(string.Format("[DialogsService] ИТОГИ: Диалогов={0}, Сообщений={1}, Сущностей={2}",
                rawDialogs.Count, messagesDict.Count, peersDict.Count));

            foreach (var dialog in rawDialogs)
            {
                PeerInfo peerInfo = null;
                if (!peersDict.TryGetValue(dialog.PeerId, out peerInfo))
                    peersDict.TryGetValue(Math.Abs(dialog.PeerId), out peerInfo);

                RawMessage lastMsg = null;
                if (dialog.TopMessageId > 0)
                    messagesDict.TryGetValue(dialog.TopMessageId, out lastMsg);
                if (lastMsg == null)
                {
                    if (!peerLastMsgDict.TryGetValue(dialog.PeerId, out lastMsg))
                        peerLastMsgDict.TryGetValue(Math.Abs(dialog.PeerId), out lastMsg);
                }

                string title;
                if (peerInfo != null && !string.IsNullOrEmpty(peerInfo.Title)) title = peerInfo.Title;
                else if (dialog.PeerType == 3) title = "Канал " + dialog.PeerId;
                else if (dialog.PeerType == 2) title = "Группа " + dialog.PeerId;
                else title = "Пользователь " + dialog.PeerId;

                string initials = peerInfo != null && !string.IsNullOrEmpty(peerInfo.Initials) ? peerInfo.Initials : GetInitials(title);

                string messageText = (lastMsg != null && !string.IsNullOrEmpty(lastMsg.Text)) ? lastMsg.Text : (dialog.PeerType == 3 ? "[Пост в канале]" : "[Сообщение]");
                DateTime date = (lastMsg != null && lastMsg.Date > new DateTime(2015, 1, 1)) ? lastMsg.Date : DateTime.Now;

                int unread = dialog.UnreadCount;
                if (unread < 0 || unread > 99999) unread = 0;

                result.Add(new ChatItemViewModel
                {
                    Id = dialog.PeerId,
                    AccessHash = peerInfo != null ? peerInfo.AccessHash : 0,
                    PhotoId = peerInfo != null ? peerInfo.PhotoId : 0,
                    PeerType = dialog.PeerType,
                    IsPinned = dialog.IsPinned,
                    Title = title,
                    LastMessage = messageText.Replace("\r", "").Replace("\n", " ").Trim(),
                    Date = date,
                    UnreadCount = unread,
                    AvatarInitials = initials,
                    IsChannel = peerInfo != null ? peerInfo.IsChannel : (dialog.PeerType == 3)
                });
            }

            return result;
        }

        private PeerInfo ReadUserSafe(TlBinaryReader reader, uint constructor)
        {
            if (constructor == 0xd3bc4b7a)
            {
                long emptyId = ReadInt64Safe(reader);
                return new PeerInfo { Id = emptyId, Title = "Пользователь", Initials = "TG" };
            }

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
                string phone = ((flags & 16) != 0) ? ReadStringSafe(reader) : "";

                long photoId = 0;
                if ((flags & 32) != 0)
                {
                    uint photoCons = ReadUInt32Safe(reader);
                    if (photoCons == 0x82d1f706)
                    {
                        int pFlags = ReadInt32Safe(reader);
                        photoId = ReadInt64Safe(reader);
                    }
                }

                if (id > 0 && id < 100000000000L)
                {
                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;
                    if (string.IsNullOrEmpty(fullName)) fullName = "Пользователь " + id;
                    return new PeerInfo { Id = id, AccessHash = accessHash, PhotoId = photoId, Title = fullName, Initials = GetInitials(fullName) };
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
                string phone = ((flags & 16) != 0) ? ReadStringSafe(reader) : "";

                long photoId = 0;
                if ((flags & 32) != 0)
                {
                    uint photoCons = ReadUInt32Safe(reader);
                    if (photoCons == 0x82d1f706)
                    {
                        int pFlags = ReadInt32Safe(reader);
                        photoId = ReadInt64Safe(reader);
                    }
                }

                if (id > 0 && id < 100000000000L)
                {
                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;
                    if (string.IsNullOrEmpty(fullName)) fullName = "Пользователь " + id;
                    return new PeerInfo { Id = id, AccessHash = accessHash, PhotoId = photoId, Title = fullName, Initials = GetInitials(fullName) };
                }
            }
            catch { }

            throw new Exception("User parse failed");
        }

        private PeerInfo ReadChatSafe(TlBinaryReader reader, uint constructor)
        {
            if (constructor == 0x6592a1a7)
            {
                long fId = ReadInt64Safe(reader);
                string fTitle = ReadStringSafe(reader);
                if (fId > 0 && fId < 100000000000L && fTitle != null && fTitle.Length < 150)
                    return new PeerInfo { Id = fId, Title = fTitle, Initials = GetInitials(fTitle), IsChannel = false, IsGroup = true };
                throw new Exception("chatForbidden parse failed");
            }

            if (constructor == 0x29562865)
            {
                long eId = ReadInt64Safe(reader);
                return new PeerInfo { Id = eId, Title = "Группа " + eId, Initials = "TG" };
            }

            int startPos = reader.Position;
            bool isGroup = (constructor == 0x41cbf256 || constructor == 0xd63d27e7 || constructor == 0xd155047b);

            if (isGroup)
            {
                try
                {
                    int flags = ReadInt32Safe(reader);
                    long id = ReadInt64Safe(reader);
                    string title = ReadStringSafe(reader);

                    long photoId = 0;
                    uint photoCons = ReadUInt32Safe(reader);
                    if (photoCons == 0x1c6e1c11)
                    {
                        int pFlags = ReadInt32Safe(reader);
                        photoId = ReadInt64Safe(reader);
                    }

                    if (id > 0 && id < 100000000000L && title != null && title.Length < 150)
                    {
                        return new PeerInfo { Id = id, AccessHash = 0, PhotoId = photoId, Title = title, Initials = GetInitials(title), IsChannel = false, IsGroup = true };
                    }
                }
                catch { }
            }

            reader.Position = startPos;
            try
            {
                int flags = ReadInt32Safe(reader);
                int flags2 = ReadInt32Safe(reader);

                long id = ReadInt64Safe(reader);
                long accessHash = (!isGroup && ((flags & 8192) != 0)) ? ReadInt64Safe(reader) : 0;
                string title = ReadStringSafe(reader);

                string username = "";
                if (!isGroup && (flags & 64) != 0)
                {
                    username = ReadStringSafe(reader);
                }

                long photoId = 0;
                uint photoCons = ReadUInt32Safe(reader);
                if (photoCons == 0x1c6e1c11)
                {
                    int pFlags = ReadInt32Safe(reader);
                    photoId = ReadInt64Safe(reader);
                }

                if (id > 0 && id < 100000000000L && title != null && title.Length < 150)
                {
                    return new PeerInfo { Id = id, AccessHash = accessHash, PhotoId = photoId, Title = title, Initials = GetInitials(title), IsChannel = !isGroup, IsGroup = isGroup };
                }
            }
            catch { }

            reader.Position = startPos;
            try
            {
                int flags = ReadInt32Safe(reader);

                long id = ReadInt64Safe(reader);
                long accessHash = (!isGroup && ((flags & 8192) != 0)) ? ReadInt64Safe(reader) : 0;
                string title = ReadStringSafe(reader);

                string username = "";
                if (!isGroup && (flags & 64) != 0)
                {
                    username = ReadStringSafe(reader);
                }

                long photoId = 0;
                uint photoCons = ReadUInt32Safe(reader);
                if (photoCons == 0x1c6e1c11)
                {
                    int pFlags = ReadInt32Safe(reader);
                    photoId = ReadInt64Safe(reader);
                }

                if (id > 0 && id < 100000000000L && title != null && title.Length < 150)
                {
                    return new PeerInfo { Id = id, AccessHash = accessHash, PhotoId = photoId, Title = title, Initials = GetInitials(title), IsChannel = !isGroup, IsGroup = isGroup };
                }
            }
            catch { }

            throw new Exception("Chat parse failed");
        }

        private RawMessage ReadMessageTextSafe(TlBinaryReader reader, uint constructor)
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

                return new RawMessage { Id = sMsgId, PeerId = sPeerId, Text = "[Сервисное сообщение]", Date = sDate };
            }

            int msgFlags = ReadInt32Safe(reader);
            int flags2 = 0;
            if (constructor == 0x76bec211 || constructor == 0x9cb490e9 || constructor == 0x3ae56482 || constructor == 0x7600b9d3)
            {
                flags2 = ReadInt32Safe(reader);
            }

            int mId = ReadInt32Safe(reader);

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
                msgText = "[Медиа / Вложение]";
            }

            DateTime date = (dUnixSafe > 1000000000) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(dUnixSafe).ToLocalTime() : DateTime.Now;

            return new RawMessage { Id = mId, PeerId = pId, Text = msgText, Date = date };
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

        private string GetInitials(string title)
        {
            if (string.IsNullOrEmpty(title)) return "TG";
            string[] words = title.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpper();
            return (title.Length >= 2 ? title.Substring(0, 2) : title.Substring(0, 1)).ToUpper();
        }

        private class RawDialog
        {
            public long PeerId { get; set; }
            public int PeerType { get; set; }
            public int TopMessageId { get; set; }
            public int UnreadCount { get; set; }
            public bool IsPinned { get; set; }
        }

        private class RawMessage
        {
            public long Id { get; set; }
            public long PeerId { get; set; }
            public string Text { get; set; }
            public DateTime Date { get; set; }
        }
    }
}