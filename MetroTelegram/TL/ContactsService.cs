using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MetroTelegram.ViewModels;

namespace MetroTelegram.TL
{
    public class ContactsService
    {
        private readonly TelegramRpcEngine _rpcEngine;

        public ContactsService(TelegramRpcEngine rpcEngine)
        {
            _rpcEngine = rpcEngine;
        }

        public async Task<List<ContactItemViewModel>> GetContactsAsync()
        {
            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0x5dd69e12);
                writer.WriteInt64(0L);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[ContactsService] Запрос contacts.getContacts ({0} байт)...", queryBytes.Length));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return ParseContactsResponse(response);
        }

        private List<ContactItemViewModel> ParseContactsResponse(byte[] response)
        {
            var result = new List<ContactItemViewModel>();

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
                        if (cons == 0x215c4438 || cons == 0x83314057 || cons == 0x93b272a7 ||
                            cons == 0x2e56d744 || cons == 0xd23c81a3 || cons == 0x3ff6ecb0 ||
                            cons == 0xb1b8cc83 || cons == 0xabb5f120)
                        {
                            var contact = ReadContactDetail(reader, cons);
                            if (contact != null && contact.UserId != 0)
                            {
                                if (!result.Exists(c => c.UserId == contact.UserId))
                                {
                                    result.Add(contact);
                                }
                            }
                        }
                    }
                    catch { }

                    reader.Position = startPos + 1;
                }
            }

            Debug.WriteLine(string.Format("[ContactsService] УСПЕХ! Загружено контактов: {0}", result.Count));
            return result;
        }

        private ContactItemViewModel ReadContactDetail(TlBinaryReader reader, uint constructor)
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
                        Status = "был(а) недавно",
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
                        Status = "был(а) недавно",
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