using System;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;

namespace MetroTelegram.Crypto
{
    public class AuthKeyStorage
    {
        private readonly bool _isInMemoryOnly;

        private const string KeyAuthKey = "TG_AuthKey";
        private const string KeyAuthKeyId = "TG_AuthKeyId";
        private const string KeyServerSalt = "TG_ServerSalt";
        private const string KeyTimeOffset = "TG_TimeOffset";
        private const string KeyCurrentDcId = "TG_CurrentDcId";
        private const string KeySessionId = "TG_SessionId";
        private const string KeyUserId = "TG_UserId";
        private const string KeyUserPhone = "TG_UserPhone";
        private const string KeyUserName = "TG_UserName";
        private const string KeyIsAuthorized = "TG_IsAuthorized";

        public byte[] AuthKey { get; private set; }
        public long AuthKeyId { get; private set; }
        public ulong ServerSalt { get; set; }
        public int TimeOffset { get; set; }
        public int CurrentDcId { get; set; }
        public long SessionId { get; set; }

        public long CurrentUserId { get; set; }
        public string CurrentUserPhone { get; set; }
        public string CurrentUserName { get; set; }
        public bool IsAuthorized { get; set; }

        public bool HasAuthKey => AuthKey != null && AuthKey.Length == 256;

        public AuthKeyStorage(bool isInMemoryOnly = false)
        {
            _isInMemoryOnly = isInMemoryOnly;
        }

        public void Load()
        {
            if (_isInMemoryOnly) return;

            var settings = IsolatedStorageSettings.ApplicationSettings;

            CurrentDcId = settings.Contains(KeyCurrentDcId) ? (int)settings[KeyCurrentDcId] : 2;

            if (settings.Contains(KeyAuthKey))
            {
                AuthKey = (byte[])settings[KeyAuthKey];
                AuthKeyId = (long)settings[KeyAuthKeyId];
                ServerSalt = (ulong)settings[KeyServerSalt];
                TimeOffset = settings.Contains(KeyTimeOffset) ? (int)settings[KeyTimeOffset] : 0;
            }

            if (settings.Contains(KeySessionId))
            {
                SessionId = (long)settings[KeySessionId];
            }
            else
            {
                GenerateNewSessionId();
            }

            if (settings.Contains(KeyIsAuthorized))
            {
                IsAuthorized = (bool)settings[KeyIsAuthorized];
                CurrentUserId = settings.Contains(KeyUserId) ? (long)settings[KeyUserId] : 0;
                CurrentUserPhone = settings.Contains(KeyUserPhone) ? (string)settings[KeyUserPhone] : string.Empty;
                CurrentUserName = settings.Contains(KeyUserName) ? (string)settings[KeyUserName] : string.Empty;
            }
        }

        public void GenerateNewSessionId()
        {
            byte[] bytes = new byte[8];
            var rng = new RNGCryptoServiceProvider();
            rng.GetBytes(bytes);

            SessionId = BitConverter.ToInt64(bytes, 0);

            if (!_isInMemoryOnly)
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                settings[KeySessionId] = SessionId;
                settings.Save();
            }
        }

        public void ClearPending2FaState()
        {
        }

        public void Save(byte[] authKey, ulong serverSalt, int timeOffset, int dcId = 0)
        {
            AuthKey = authKey;
            ServerSalt = serverSalt;
            TimeOffset = timeOffset;
            if (dcId > 0) CurrentDcId = dcId;

            if (authKey != null && authKey.Length == 256)
            {
                using (SHA1Managed sha1 = new SHA1Managed())
                {
                    byte[] sha = sha1.ComputeHash(authKey);
                    AuthKeyId = BitConverter.ToInt64(sha, 12);
                }
            }
            else
            {
                AuthKeyId = 0;
            }

            if (!_isInMemoryOnly)
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                settings[KeyAuthKey] = AuthKey;
                settings[KeyAuthKeyId] = AuthKeyId;
                settings[KeyServerSalt] = ServerSalt;
                settings[KeyTimeOffset] = TimeOffset;
                settings[KeyCurrentDcId] = CurrentDcId;
                settings.Save();
            }
        }

        public void SaveUserProfile(long userId, string phone, string name)
        {
            CurrentUserId = userId;
            CurrentUserPhone = phone;
            CurrentUserName = name;
            IsAuthorized = true;

            if (!_isInMemoryOnly)
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                settings[KeyUserId] = CurrentUserId;
                settings[KeyUserPhone] = CurrentUserPhone;
                settings[KeyUserName] = CurrentUserName;
                settings[KeyIsAuthorized] = true;
                settings.Save();
            }
        }

        public void Clear()
        {
            AuthKey = null;
            AuthKeyId = 0;
            ServerSalt = 0;
            TimeOffset = 0;
            IsAuthorized = false;
            CurrentUserId = 0;
            CurrentUserPhone = null;
            CurrentUserName = null;
            GenerateNewSessionId();

            if (!_isInMemoryOnly)
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                settings.Remove(KeyAuthKey);
                settings.Remove(KeyAuthKeyId);
                settings.Remove(KeyServerSalt);
                settings.Remove(KeyTimeOffset);
                settings.Remove(KeyUserId);
                settings.Remove(KeyUserPhone);
                settings.Remove(KeyUserName);
                settings.Remove(KeyIsAuthorized);
                settings.Save();
            }
        }
    }
}