using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MetroTelegram.ViewModels;

namespace MetroTelegram.TL
{
    public class FullProfileResult
    {
        public long Id { get; set; }
        public long AccessHash { get; set; }
        public int PeerType { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public string Phone { get; set; }
        public string About { get; set; }
        public string Status { get; set; }
        public string Initials { get; set; }
        public int ParticipantsCount { get; set; }
        public List<ContactItemViewModel> Members { get; set; }

        public FullProfileResult()
        {
            Members = new List<ContactItemViewModel>();
        }
    }

    public class ProfileService
    {
        private readonly TelegramRpcEngine _rpcEngine;

        public ProfileService(TelegramRpcEngine rpcEngine)
        {
            _rpcEngine = rpcEngine;
        }

        public async Task<FullProfileResult> GetFullProfileAsync(long peerId, long accessHash, int peerType, string currentTitle)
        {
            long rawId = Math.Abs(peerId);
            byte[] queryBytes;

            using (var writer = new TlBinaryWriter())
            {
                if (peerType == 1)
                {
                    writer.WriteUInt32(0xb60dc69b);
                    writer.WriteUInt32(0xf21158d7);
                    writer.WriteInt64(rawId);
                    writer.WriteInt64(accessHash);
                }
                else if (peerType == 2)
                {
                    writer.WriteUInt32(0xaeb00b34);
                    writer.WriteInt64(rawId);
                }
                else
                {
                    writer.WriteUInt32(0x08736a09);
                    writer.WriteUInt32(0xf35aec28);
                    writer.WriteInt64(rawId);
                    writer.WriteInt64(accessHash);
                }

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[ProfileService] Запрос профиля для Peer {0} (тип {1})...", peerId, peerType));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return ParseFullProfileResponse(response, peerId, accessHash, peerType, currentTitle);
        }

        private FullProfileResult ParseFullProfileResponse(byte[] response, long peerId, long accessHash, int peerType, string title)
        {
            var result = new FullProfileResult
            {
                Id = peerId,
                AccessHash = accessHash,
                PeerType = peerType,
                Title = title,
                Initials = GetInitials(title),
                Status = peerType == 3 ? "канал" : (peerType == 2 ? "группа" : "был(а) недавно")
            };

            using (var reader = new TlBinaryReader(response))
            {
                uint constructor = ReadUInt32Safe(reader);


                while (reader.Remaining >= 4)
                {
                    int startPos = reader.Position;
                    uint cons = ReadUInt32Safe(reader);

                    try
                    {
                        if (cons == 0x215c4438 || cons == 0x83314057 || cons == 0x93b272a7 ||
                            cons == 0x2e56d744 || cons == 0xd23c81a3 || cons == 0x3ff6ecb0 ||
                            cons == 0xb1b8cc83 || cons == 0xabb5f120)
                        {
                            var user = ReadUserSafe(reader, cons);
                            if (user != null && user.UserId != 0 && user.UserId != peerId)
                            {
                                if (!result.Members.Exists(m => m.UserId == user.UserId))
                                {
                                    result.Members.Add(user);
                                }
                            }
                        }
                    }
                    catch { }

                    reader.Position = startPos + 1;
                }
            }

            result.ParticipantsCount = result.Members.Count;
            if (peerType == 2 || peerType == 3)
            {
                result.Status = string.Format("{0} участников", Math.Max(result.ParticipantsCount, 1));
            }

            return result;
        }

        public async Task<List<ContactItemViewModel>> GetChannelParticipantsAsync(long peerId, long accessHash, int offset = 0, int limit = 200)
        {
            long rawId = Math.Abs(peerId);
            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0x77ced9d0);
                writer.WriteUInt32(0xf35aec28);
                writer.WriteInt64(rawId);
                writer.WriteInt64(accessHash);
                writer.WriteUInt32(0xde3f3c79);
                writer.WriteInt32(offset);
                writer.WriteInt32(limit);
                writer.WriteInt64(0);
                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[ProfileService] Запрос участников канала {0} (offset={1}, limit={2})...", peerId, offset, limit));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return ParseParticipantsResponse(response);
        }

        private List<ContactItemViewModel> ParseParticipantsResponse(byte[] response)
        {
            var members = new List<ContactItemViewModel>();

            using (var reader = new TlBinaryReader(response))
            {
                uint topConstructor = ReadUInt32Safe(reader);
                Debug.WriteLine(string.Format("[ProfileService] Ответ участников: top=0x{0:X8}, размер={1}b", topConstructor, response.Length));

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
                        if (cons == 0x215c4438 || cons == 0x83314057 || cons == 0x93b272a7 ||
                            cons == 0x2e56d744 || cons == 0xd23c81a3 || cons == 0x3ff6ecb0 ||
                            cons == 0xb1b8cc83 || cons == 0xabb5f120)
                        {
                            var user = ReadUserSafe(reader, cons);
                            if (user != null && user.UserId != 0 && !members.Exists(m => m.UserId == user.UserId))
                            {
                                members.Add(user);
                            }
                        }
                    }
                    catch { }
                    reader.Position = startPos + 1;
                }
            }

            Debug.WriteLine(string.Format("[ProfileService] Участников распознано: {0}", members.Count));
            return members;
        }

        private ContactItemViewModel ReadUserSafe(TlBinaryReader reader, uint constructor)
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
                string phone = ((flags & 16) != 0) ? ReadStringSafe(reader) : "";

                if (id > 0 && id < 100000000000L)
                {
                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;
                    if (string.IsNullOrEmpty(fullName)) fullName = "Пользователь " + id;

                    return new ContactItemViewModel
                    {
                        UserId = id,
                        AccessHash = accessHash,
                        FullName = fullName,
                        Phone = phone,
                        Status = "участник",
                        Initials = GetInitials(fullName)
                    };
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

                if (id > 0 && id < 100000000000L)
                {
                    string fullName = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(fullName)) fullName = username;
                    if (string.IsNullOrEmpty(fullName)) fullName = "Пользователь " + id;

                    return new ContactItemViewModel
                    {
                        UserId = id,
                        AccessHash = accessHash,
                        FullName = fullName,
                        Phone = phone,
                        Status = "участник",
                        Initials = GetInitials(fullName)
                    };
                }
            }
            catch { }

            return null;
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
            return System.Text.Encoding.UTF8.GetString(data, 0, data.Length);
        }

        private string GetInitials(string title)
        {
            if (string.IsNullOrEmpty(title)) return "TG";
            string[] words = title.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpper();
            return (title.Length >= 2 ? title.Substring(0, 2) : title.Substring(0, 1)).ToUpper();
        }
    }
}