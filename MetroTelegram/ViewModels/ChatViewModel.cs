using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroTelegram.ViewModels
{
    public class MessageItemViewModel : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public long FromId { get; set; }
        public bool IsService { get; set; }

        public int ReplyToMsgId { get; set; }
        public string ReplyQuoteText { get; set; }

        private string _replyAuthor;
        public string ReplyAuthor
        {
            get { return _replyAuthor; }
            set { _replyAuthor = value; OnPropertyChanged(); }
        }

        private string _replyText;
        public string ReplyText
        {
            get { return _replyText; }
            set
            {
                _replyText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasReply));
                OnPropertyChanged(nameof(ReplyVisibility));
            }
        }

        public bool HasReply => ReplyToMsgId > 0 || !string.IsNullOrEmpty(ReplyText);
        public Visibility ReplyVisibility => HasReply ? Visibility.Visible : Visibility.Collapsed;

        public long PhotoId { get; set; }
        public long PhotoAccessHash { get; set; }
        public byte[] PhotoFileReference { get; set; }
        public string PhotoThumbSize { get; set; }
        public string PhotoFullThumbSize { get; set; }

        public bool HasPhoto => PhotoId != 0 || PhotoImage != null;
        public Visibility PhotoVisibility => HasPhoto ? Visibility.Visible : Visibility.Collapsed;

        private BitmapImage _photoImage;
        public BitmapImage PhotoImage
        {
            get { return _photoImage; }
            set
            {
                _photoImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPhoto));
                OnPropertyChanged(nameof(PhotoVisibility));
            }
        }

        private int _deliveryStatus = 1;
        public int DeliveryStatus
        {
            get { return _deliveryStatus; }
            set
            {
                _deliveryStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusIcon));
            }
        }

        public string StatusIcon
        {
            get
            {
                if (!IsOutgoing) return string.Empty;
                if (DeliveryStatus == 0) return "🕒";
                if (DeliveryStatus == 2) return "✔✔";
                return "✔";
            }
        }

        public Visibility StatusVisibility => (IsOutgoing && !IsService) ? Visibility.Visible : Visibility.Collapsed;

        private string _authorName;
        public string AuthorName
        {
            get { return _authorName; }
            set
            {
                _authorName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AuthorVisibility));
            }
        }

        public Visibility AuthorVisibility => (!IsOutgoing && !string.IsNullOrEmpty(AuthorName) && !IsService) ? Visibility.Visible : Visibility.Collapsed;

        private string _text;
        public string Text
        {
            get { return _text; }
            set { _text = value; OnPropertyChanged(); }
        }

        private DateTime _date;
        public DateTime Date
        {
            get { return _date; }
            set
            {
                _date = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedTime));
            }
        }

        public string FormattedTime => Date > new DateTime(2015, 1, 1) ? Date.ToString("HH:mm") : string.Empty;

        private bool _isOutgoing;
        public bool IsOutgoing
        {
            get { return _isOutgoing; }
            set
            {
                _isOutgoing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BubbleAlignment));
                OnPropertyChanged(nameof(BubbleMargin));
                OnPropertyChanged(nameof(BubbleBackground));
                OnPropertyChanged(nameof(AuthorVisibility));
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }

        public HorizontalAlignment BubbleAlignment => IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Thickness BubbleMargin => IsOutgoing ? new Thickness(48, 0, 0, 10) : new Thickness(0, 0, 48, 10);

        public Brush BubbleBackground
        {
            get
            {
                if (IsOutgoing)
                    return (Brush)Application.Current.Resources["PhoneAccentBrush"];

                return new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ChatViewModel : INotifyPropertyChanged
    {
        public long PeerId { get; set; }
        public long AccessHash { get; set; }
        public int PeerType { get; set; }
        public string Title { get; set; }
        public int ReadOutboxMaxId { get; set; }

        public ObservableCollection<MessageItemViewModel> Messages { get; set; }

        public ChatViewModel()
        {
            Messages = new ObservableCollection<MessageItemViewModel>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}