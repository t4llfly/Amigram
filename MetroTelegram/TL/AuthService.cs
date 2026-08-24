using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MetroTelegram.Crypto;
using MetroTelegram.Transport;

namespace MetroTelegram.TL
{
    public class SentCodeResult
    {
        public string PhoneCodeHash { get; set; }
        public string DeliveryTypeDescription { get; set; }
        public int CodeLength { get; set; }
        public int Timeout { get; set; }
    }

    public class AuthUserResult
    {
        public long UserId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Username { get; set; }
        public string Phone { get; set; }
        public bool Requires2Fa { get; set; }
    }

    public class AuthService
    {
        private TelegramRpcEngine _rpcEngine;
        private readonly ITcpTransport _transport;
        private readonly AuthKeyStorage _storage;

        public AuthService(TelegramRpcEngine rpcEngine, ITcpTransport transport, AuthKeyStorage storage)
        {
            _rpcEngine = rpcEngine;
            _transport = transport;
            _storage = storage;
        }

        public static string SanitizePhoneNumber(string phone)
        {
            if (string.IsNullOrEmpty(phone)) return string.Empty;
            return phone.Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Trim();
        }

        public async Task<SentCodeResult> SendCodeAsync(string phoneNumber)
        {
            string cleanPhone = SanitizePhoneNumber(phoneNumber);

            while (true)
            {
                byte[] queryBytes;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xa677244f);
                    writer.WriteString(cleanPhone);
                    writer.WriteInt32(AppConfig.ApiId);
                    writer.WriteString(AppConfig.ApiHash);

                    writer.WriteUInt32(0xad253d78);
                    writer.WriteInt32(0);

                    queryBytes = writer.ToByteArray();
                }

                try
                {
                    Debug.WriteLine(string.Format("[AuthService] Отправка auth.sendCode на номер {0} ({1} байт)...", cleanPhone, queryBytes.Length));
                    byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
                    return ParseSentCodeResponse(response);
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.Contains("PHONE_MIGRATE_") || ex.Message.Contains("NETWORK_MIGRATE_"))
                    {
                        int targetDc = ExtractTargetDc(ex.Message);
                        Debug.WriteLine(string.Format("[AuthService] Авто-миграция на домашний DC{0}...", targetDc));

                        await MigrateToDcAsync(targetDc);
                        continue;
                    }

                    throw;
                }
            }
        }

        public async Task<AuthUserResult> SignInAsync(string phoneNumber, string phoneCodeHash, string code)
        {
            string cleanPhone = SanitizePhoneNumber(phoneNumber);
            string cleanCode = code.Trim().Replace(" ", "").Replace("-", "");

            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0x8d52a951);
                writer.WriteInt32(1);
                writer.WriteString(cleanPhone);
                writer.WriteString(phoneCodeHash.Trim());
                writer.WriteString(cleanCode);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[AuthService] Отправка auth.signIn для {0} (код: {1}, размер: {2} байт)...", cleanPhone, cleanCode, queryBytes.Length));

            try
            {
                byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
                return ParseAuthResponse(response, cleanPhone);
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("SESSION_PASSWORD_NEEDED"))
                {
                    Debug.WriteLine("[AuthService] Сервер вернул SESSION_PASSWORD_NEEDED! Переключаем на 2FA...");
                    return new AuthUserResult { Requires2Fa = true, Phone = cleanPhone };
                }
                throw;
            }
        }

        public async Task<PasswordKdfParams> GetPasswordSettingsAsync()
        {
            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0x548a30f5);
                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine("[AuthService] Запрос параметров 2FA (account.getPassword)...");
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);

            using (var reader = new TlBinaryReader(response))
            {
                uint constructor = reader.ReadUInt32();
                Debug.WriteLine(string.Format("[AuthService] account.getPassword Constructor: 0x{0:X8}", constructor));

                if (constructor == 0x2144ca10 || (constructor & 0xFFFFFF00) == 0x2144CA00)
                {
                    int errCode = reader.ReadInt32();
                    string errMsg = reader.ReadString();
                    throw new InvalidOperationException(string.Format("Telegram Error {0}: {1}", errCode, errMsg));
                }

                byte[] salt1 = null;
                byte[] salt2 = null;
                int g = 3;
                byte[] p = null;
                byte[] srpB = null;
                long srpId = 0;
                string hint = "";

                if (constructor == 0x957b50fb || constructor == 0x9570e57c)
                {
                    int flags = reader.ReadInt32();

                    if ((flags & 4) != 0)
                    {
                        uint algoCons = reader.ReadUInt32();
                        salt1 = reader.ReadBytes();
                        salt2 = reader.ReadBytes();
                        g = reader.ReadInt32();
                        p = reader.ReadBytes();

                        srpB = reader.ReadBytes();
                        srpId = reader.ReadInt64();
                    }

                    if ((flags & 8) != 0)
                    {
                        hint = reader.ReadString();
                    }
                }
                else if (constructor == 0x185b184f)
                {
                    uint algoCons = reader.ReadUInt32();
                    salt1 = reader.ReadBytes();
                    salt2 = reader.ReadBytes();
                    g = reader.ReadInt32();
                    p = reader.ReadBytes();

                    srpB = reader.ReadBytes();
                    srpId = reader.ReadInt64();
                    hint = reader.ReadString();
                }
                else
                {
                    while (reader.Remaining >= 24)
                    {
                        if (reader.ReadUInt32() == 0x3a912d4a)
                        {
                            salt1 = reader.ReadBytes();
                            salt2 = reader.ReadBytes();
                            g = reader.ReadInt32();
                            p = reader.ReadBytes();
                            srpB = reader.ReadBytes();
                            srpId = reader.ReadInt64();
                            break;
                        }
                    }
                }

                Debug.WriteLine(string.Format("[AuthService] ПОДЛИННЫЙ SRP ID: 0x{0:X16} ({0}), p={1}b, B={2}b, g={3}, hint='{4}'",
                    srpId, p != null ? p.Length : 0, srpB != null ? srpB.Length : 0, g, hint));

                return new PasswordKdfParams
                {
                    Salt1 = salt1,
                    Salt2 = salt2,
                    G = g,
                    P = p,
                    SrpB = srpB,
                    SrpId = srpId,
                    Hint = hint
                };
            }
        }

        public async Task<AuthUserResult> CheckPasswordAsync(string password, PasswordKdfParams kdf, string phone)
        {
            byte[] A_bytes, M1_bytes;
            Telegram2FaSrp.ComputeSrpProof(password, kdf, out A_bytes, out M1_bytes);

            byte[] queryBytes;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0xd18b4d16);

                writer.WriteUInt32(0xd27ff082);
                writer.WriteInt64(kdf.SrpId);
                writer.WriteBytes(A_bytes);
                writer.WriteBytes(M1_bytes);

                queryBytes = writer.ToByteArray();
            }

            Debug.WriteLine(string.Format("[AuthService] Отправка auth.checkPassword (SrpId: 0x{0:X16}, Payload: {1} байт)...", kdf.SrpId, queryBytes.Length));
            byte[] response = await _rpcEngine.SendRpcQueryAsync(queryBytes, wrapInitConnection: false);
            return ParseAuthResponse(response, phone);
        }

        private async Task MigrateToDcAsync(int dcId)
        {
            DataCenter targetDc = DataCenter.GetDc(dcId);

            _transport.Disconnect();
            _storage.Clear();
            _storage.CurrentDcId = dcId;

            await _transport.ConnectAsync(targetDc);

            var handshake = new AuthKeyHandshake(_transport);
            await handshake.ExecuteAsync(_storage);

            _storage.Save(_storage.AuthKey, _storage.ServerSalt, _storage.TimeOffset, dcId);

            _rpcEngine = new TelegramRpcEngine(_transport, _storage);

            byte[] configQuery;
            using (var writer = new TlBinaryWriter())
            {
                writer.WriteUInt32(0xc4f9186b);
                configQuery = writer.ToByteArray();
            }
            await _rpcEngine.SendRpcQueryAsync(configQuery, wrapInitConnection: true);
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

        private SentCodeResult ParseSentCodeResponse(byte[] response)
        {
            using (var reader = new TlBinaryReader(response))
            {
                uint constructor = reader.ReadUInt32();
                int flags = reader.ReadInt32();

                uint typeConstructor = reader.ReadUInt32();
                int codeLen = 5;
                string deliveryDesc = "";

                if (typeConstructor == 0x3dbb5986)
                {
                    codeLen = reader.ReadInt32();
                    deliveryDesc = "Код отправлен в официальное приложение Telegram на другом устройстве.";
                }
                else if (typeConstructor == 0xc0000810)
                {
                    codeLen = reader.ReadInt32();
                    deliveryDesc = "Код отправлен по СМС на ваш номер телефона.";
                }
                else
                {
                    deliveryDesc = "Код отправлен. Проверьте чат Telegram или СМС.";
                }

                string phoneCodeHash = reader.ReadString();

                Debug.WriteLine($"[AuthService] auth.sentCode OK! Hash: {phoneCodeHash}, Код: {codeLen} знаков");

                return new SentCodeResult
                {
                    PhoneCodeHash = phoneCodeHash,
                    DeliveryTypeDescription = deliveryDesc,
                    CodeLength = codeLen,
                    Timeout = 60
                };
            }
        }

        private AuthUserResult ParseAuthResponse(byte[] response, string phone)
        {
            using (var reader = new TlBinaryReader(response))
            {
                uint constructor = reader.ReadUInt32();

                if ((constructor & 0xFFFFFF00) == 0x2144CA00 || constructor == 0x2144ca10)
                {
                    int errorCode = reader.ReadInt32();
                    string errorMessage = reader.ReadString();

                    if (errorMessage.Contains("SESSION_PASSWORD_NEEDED"))
                        return new AuthUserResult { Requires2Fa = true, Phone = phone };

                    if (errorMessage.Contains("PASSWORD_HASH_INVALID"))
                        throw new InvalidOperationException("Введен неверный облачный пароль (PASSWORD_HASH_INVALID).");

                    throw new InvalidOperationException(string.Format("Ошибка авторизации ({0}): {1}", errorCode, errorMessage));
                }

                int flags = reader.ReadInt32();
                if ((flags & 2) != 0) reader.ReadInt32();
                if ((flags & 1) != 0) reader.ReadInt32();
                if ((flags & 4) != 0) reader.ReadBytes();

                uint userConstructor = reader.ReadUInt32();
                int userFlags = reader.ReadInt32();
                int userFlags2 = 0;
                if (reader.Remaining > 12) userFlags2 = reader.ReadInt32();

                long userId = reader.ReadInt64();
                long accessHash = ((userFlags & 1) != 0) ? reader.ReadInt64() : 0;
                string firstName = ((userFlags & 2) != 0) ? reader.ReadString() : string.Empty;
                string lastName = ((userFlags & 4) != 0) ? reader.ReadString() : string.Empty;
                string username = ((userFlags & 8) != 0) ? reader.ReadString() : string.Empty;

                return new AuthUserResult
                {
                    UserId = userId,
                    FirstName = firstName,
                    LastName = lastName,
                    Username = username,
                    Phone = phone,
                    Requires2Fa = false
                };
            }
        }
    }
}