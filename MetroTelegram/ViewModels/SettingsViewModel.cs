using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MetroTelegram.Crypto;

namespace MetroTelegram.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private string _fullName;
        public string FullName
        {
            get { return _fullName; }
            set { _fullName = value; OnPropertyChanged(); }
        }

        private string _phoneNumber;
        public string PhoneNumber
        {
            get { return _phoneNumber; }
            set { _phoneNumber = value; OnPropertyChanged(); }
        }

        private string _userIdString;
        public string UserIdString
        {
            get { return _userIdString; }
            set { _userIdString = value; OnPropertyChanged(); }
        }

        private string _avatarInitials;
        public string AvatarInitials
        {
            get { return _avatarInitials; }
            set { _avatarInitials = value; OnPropertyChanged(); }
        }

        private string _dcInfo;
        public string DcInfo
        {
            get { return _dcInfo; }
            set { _dcInfo = value; OnPropertyChanged(); }
        }

        private string _authKeyInfo;
        public string AuthKeyInfo
        {
            get { return _authKeyInfo; }
            set { _authKeyInfo = value; OnPropertyChanged(); }
        }

        public void LoadFromStorage(AuthKeyStorage storage)
        {
            storage.Load();

            FullName = !string.IsNullOrEmpty(storage.CurrentUserName) ? storage.CurrentUserName : "Пользователь Telegram";
            PhoneNumber = !string.IsNullOrEmpty(storage.CurrentUserPhone) ? "+" + storage.CurrentUserPhone : "—";
            UserIdString = storage.CurrentUserId != 0 ? "ID: " + storage.CurrentUserId : "—";

            if (!string.IsNullOrEmpty(FullName))
            {
                string[] words = FullName.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2)
                    AvatarInitials = (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpper();
                else
                    AvatarInitials = (FullName.Length >= 2 ? FullName.Substring(0, 2) : FullName.Substring(0, 1)).ToUpper();
            }
            else
            {
                AvatarInitials = "TG";
            }

            var dc = Transport.DataCenter.GetDc(storage.CurrentDcId);
            DcInfo = string.Format("DC{0} ({1}:{2})", dc.Id, dc.Host, dc.Port);
            AuthKeyInfo = string.Format("0x{0:X16}", storage.AuthKeyId);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}