using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MetroTelegram.ViewModels
{
    public class ChatItemViewModel : INotifyPropertyChanged
    {
        private long _id;
        public long Id
        {
            get { return _id; }
            set { _id = value; OnPropertyChanged(); }
        }

        public long AccessHash { get; set; }
        public long PhotoId { get; set; }

        private BitmapImage _avatarImage;
        public BitmapImage AvatarImage
        {
            get { return _avatarImage; }
            set
            {
                _avatarImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAvatarImage));
                OnPropertyChanged(nameof(AvatarImageVisibility));
                OnPropertyChanged(nameof(AvatarInitialsVisibility));
            }
        }

        public bool HasAvatarImage => AvatarImage != null;
        public Visibility AvatarImageVisibility => HasAvatarImage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AvatarInitialsVisibility => !HasAvatarImage ? Visibility.Visible : Visibility.Collapsed;

        private int _peerType;
        public int PeerType
        {
            get { return _peerType; }
            set { _peerType = value; OnPropertyChanged(); }
        }

        public bool IsChannel { get; set; }

        private bool _isPinned;
        public bool IsPinned
        {
            get { return _isPinned; }
            set
            {
                _isPinned = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinnedVisibility));
            }
        }

        public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

        private string _title;
        public string Title
        {
            get { return _title; }
            set { _title = value; OnPropertyChanged(); }
        }

        private string _lastMessage;
        public string LastMessage
        {
            get { return _lastMessage; }
            set
            {
                _lastMessage = value != null ? value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim() : string.Empty;
                OnPropertyChanged();
            }
        }

        private DateTime _date;
        public DateTime Date
        {
            get { return _date; }
            set
            {
                _date = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedDate));
            }
        }

        public string FormattedDate
        {
            get
            {
                if (Date <= new DateTime(2015, 1, 1))
                    return string.Empty;

                if (Date.Date == DateTime.Today)
                    return Date.ToString("HH:mm");

                if (Date.Year == DateTime.Today.Year)
                    return Date.ToString("dd.MM");

                return Date.ToString("dd.MM.yy");
            }
        }

        private int _unreadCount;
        public int UnreadCount
        {
            get { return _unreadCount; }
            set
            {
                _unreadCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UnreadVisibility));
            }
        }

        public Visibility UnreadVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        private string _avatarInitials;
        public string AvatarInitials
        {
            get { return _avatarInitials; }
            set { _avatarInitials = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int ReadOutboxMaxId { get; set; }
    }

    public class ContactItemViewModel
    {
        public long UserId { get; set; }
        public long AccessHash { get; set; }
        public string FullName { get; set; }
        public string Phone { get; set; }
        public string Status { get; set; }
        public string Initials { get; set; }
    }

    public class AlphaKeyGroup<T> : System.Collections.Generic.List<T>
    {
        public string Key { get; private set; }
        public bool HasItems => Count > 0;

        public AlphaKeyGroup(string key)
        {
            Key = key;
        }

        public static System.Collections.Generic.List<AlphaKeyGroup<ContactItemViewModel>> CreateGroups(System.Collections.Generic.IEnumerable<ContactItemViewModel> items)
        {
            string alphabet = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЭЮЯABCDEFGHIJKLMNOPQRSTUVWXYZ#";
            var groups = new System.Collections.Generic.Dictionary<string, AlphaKeyGroup<ContactItemViewModel>>();

            foreach (char c in alphabet)
            {
                groups[c.ToString()] = new AlphaKeyGroup<ContactItemViewModel>(c.ToString());
            }

            foreach (var item in System.Linq.Enumerable.OrderBy(items, i => i.FullName))
            {
                string firstLetter = "#";
                if (!string.IsNullOrEmpty(item.FullName))
                {
                    firstLetter = item.FullName.Substring(0, 1).ToUpper();
                    if (!groups.ContainsKey(firstLetter))
                    {
                        firstLetter = "#";
                    }
                }
                groups[firstLetter].Add(item);
            }

            return System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(groups.Values, g => g.HasItems));
        }
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ChatItemViewModel> Dialogs { get; set; }
        public ObservableCollection<AlphaKeyGroup<ContactItemViewModel>> GroupedContacts { get; set; }
        public SettingsViewModel Settings { get; set; }

        private bool _isConnecting;
        public bool IsConnecting
        {
            get { return _isConnecting; }
            set
            {
                _isConnecting = value;
                OnPropertyChanged();
            }
        }

        public MainViewModel()
        {
            Dialogs = new ObservableCollection<ChatItemViewModel>();
            GroupedContacts = new ObservableCollection<AlphaKeyGroup<ContactItemViewModel>>();
            Settings = new SettingsViewModel();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}