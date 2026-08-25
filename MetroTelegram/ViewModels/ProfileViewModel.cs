using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MetroTelegram.ViewModels
{
    public class ProfileViewModel : INotifyPropertyChanged
    {
        private long _id;
        public long Id
        {
            get { return _id; }
            set { _id = value; OnPropertyChanged(); }
        }

        public long AccessHash { get; set; }
        public int PeerType { get; set; }

        private string _title;
        public string Title
        {
            get { return _title; }
            set { _title = value; OnPropertyChanged(); }
        }

        private string _subtitle;
        public string Subtitle
        {
            get { return _subtitle; }
            set { _subtitle = value; OnPropertyChanged(); }
        }

        private string _phone;
        public string Phone
        {
            get { return _phone; }
            set
            {
                _phone = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PhoneVisibility));
            }
        }

        public Visibility PhoneVisibility => !string.IsNullOrEmpty(Phone) ? Visibility.Visible : Visibility.Collapsed;

        private string _username;
        public string Username
        {
            get { return _username; }
            set
            {
                _username = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UsernameVisibility));
            }
        }

        public Visibility UsernameVisibility => !string.IsNullOrEmpty(Username) ? Visibility.Visible : Visibility.Collapsed;

        private string _about;
        public string About
        {
            get { return _about; }
            set
            {
                _about = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AboutVisibility));
            }
        }

        public Visibility AboutVisibility => !string.IsNullOrEmpty(About) ? Visibility.Visible : Visibility.Collapsed;

        private string _avatarInitials;
        public string AvatarInitials
        {
            get { return _avatarInitials; }
            set { _avatarInitials = value; OnPropertyChanged(); }
        }

        public bool IsGroup => PeerType == 2 || PeerType == 3;
        public Visibility MembersTabVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

        public ObservableCollection<ContactItemViewModel> Members { get; set; }

        public ProfileViewModel()
        {
            Members = new ObservableCollection<ContactItemViewModel>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}