using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Phone.Tasks;
using MetroTelegram.TL;
using MetroTelegram.ViewModels;
using System.Text;

namespace MetroTelegram.Views
{
    public partial class ChatPage : PhoneApplicationPage
    {
        private ProgressIndicator _progressIndicator;
        private MessagesService _messagesService;
        private MediaService _mediaService;
        private MediaUploadService _uploadService;
        private PhotoChooserTask _photoChooser;

        private DispatcherTimer _typingResetTimer;
        private string _originalSubtitle = "был(а) недавно";
        private DateTime _lastTypingSent = DateTime.MinValue;

        public ChatViewModel ViewModel { get; private set; }

        public ChatPage()
        {
            InitializeComponent();

            ViewModel = new ChatViewModel();
            DataContext = ViewModel;

            _progressIndicator = new ProgressIndicator();
            SystemTray.SetProgressIndicator(this, _progressIndicator);

            _photoChooser = new PhotoChooserTask();
            _photoChooser.ShowCamera = true;
            _photoChooser.Completed += PhotoChooser_Completed;

            _typingResetTimer = new DispatcherTimer();
            _typingResetTimer.Interval = TimeSpan.FromSeconds(5);
            _typingResetTimer.Tick += (s, e) =>
            {
                _typingResetTimer.Stop();
                ChatSubtitleTextBlock.Text = _originalSubtitle;
                ChatSubtitleTextBlock.Foreground = (Brush)Application.Current.Resources["PhoneSubtleForegroundBrush"];
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            App.LiveMessageReceived -= OnLiveMessageReceived;
            App.LiveMessageReceived += OnLiveMessageReceived;

            App.LiveOutboxRead -= OnLiveOutboxRead;
            App.LiveOutboxRead += OnLiveOutboxRead;

            App.LiveUserTyping -= OnLiveUserTyping;
            App.LiveUserTyping += OnLiveUserTyping;

            if (e.NavigationMode == NavigationMode.Back && ViewModel.Messages.Count > 0)
            {
                return;
            }

            if (NavigationContext.QueryString.ContainsKey("id"))
            {
                ViewModel.PeerId = long.Parse(NavigationContext.QueryString["id"]);
                ViewModel.AccessHash = long.Parse(NavigationContext.QueryString["accessHash"]);
                ViewModel.PeerType = int.Parse(NavigationContext.QueryString["peerType"]);
                ViewModel.Title = Uri.UnescapeDataString(NavigationContext.QueryString["title"]);

                if (NavigationContext.QueryString.ContainsKey("readOutbox"))
                {
                    ViewModel.ReadOutboxMaxId = int.Parse(NavigationContext.QueryString["readOutbox"]);
                }

                ChatTitleTextBlock.Text = ViewModel.Title;

                if (ViewModel.PeerType == 3)
                {
                    _originalSubtitle = "канал";
                }
                else if (ViewModel.PeerType == 2)
                {
                    _originalSubtitle = "групповой чат";
                }
                else
                {
                    _originalSubtitle = "был(а) недавно";
                }
                ChatSubtitleTextBlock.Text = _originalSubtitle;
                ChatSubtitleTextBlock.Foreground = (Brush)Application.Current.Resources["PhoneSubtleForegroundBrush"];

                var currentDialog = App.ViewModel.Dialogs.FirstOrDefault(d =>
                    d.Id == ViewModel.PeerId || Math.Abs(d.Id) == Math.Abs(ViewModel.PeerId));

                if (currentDialog != null)
                {
                    currentDialog.UnreadCount = 0;
                    int totalUnread = App.ViewModel.Dialogs.Sum(d => d.UnreadCount);
                    var topChat = App.ViewModel.Dialogs.FirstOrDefault();
                    string sName = topChat != null ? topChat.Title : "";
                    string lMsg = topChat != null ? topChat.LastMessage : "";
                    TileService.UpdatePrimaryTile(totalUnread, sName, lMsg);
                }

                await LoadHistoryAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (e.NavigationMode != NavigationMode.New)
            {
                App.LiveMessageReceived -= OnLiveMessageReceived;
                App.LiveOutboxRead -= OnLiveOutboxRead;
                App.LiveUserTyping -= OnLiveUserTyping;
                _typingResetTimer.Stop();
            }
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                SetLoading(true, "Загрузка переписки...");

                await App.EnsureTelegramConnectedAsync(s => SetLoading(true, s));

                _messagesService = new MessagesService(App.RpcEngine);
                _mediaService = new MediaService(App.RpcEngine);
                _uploadService = new MediaUploadService(App.RpcEngine);

                List<MessageItemViewModel> history = await _messagesService.GetHistoryAsync(
                    ViewModel.PeerId, ViewModel.AccessHash, ViewModel.PeerType, 30);

                Dispatcher.BeginInvoke(() =>
                {
                    ViewModel.Messages.Clear();
                    foreach (var msg in history)
                    {
                        ViewModel.Messages.Add(msg);

                        if (msg.HasPhoto)
                        {
                            Task.Run(async () =>
                            {
                                byte[] imageBytes = await _mediaService.LoadPhotoBytesAsync(
                                    msg.PhotoId, msg.PhotoAccessHash, msg.PhotoFileReference, msg.PhotoThumbSize);

                                if (imageBytes != null && imageBytes.Length > 0)
                                {
                                    Dispatcher.BeginInvoke(() =>
                                    {
                                        try
                                        {
                                            var bmp = new BitmapImage();
                                            using (var ms = new MemoryStream(imageBytes))
                                            {
                                                bmp.SetSource(ms);
                                            }
                                            msg.PhotoImage = bmp;
                                        }
                                        catch { }
                                    });
                                }
                            });
                        }
                    }

                    ScrollToBottom();
                    SetLoading(false);
                });

                if (history.Count > 0)
                {
                    var lastMsg = history.LastOrDefault();
                    if (lastMsg != null && App.Updates != null)
                    {
                        await App.Updates.ReadHistoryAsync(ViewModel.PeerId, ViewModel.AccessHash, ViewModel.PeerType, (int)lastMsg.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    SetLoading(false);
                    MessageBox.Show("Ошибка истории: " + ex.Message, "Ошибка", MessageBoxButton.OK);
                });
            }
        }

        private void OnLiveMessageReceived(object sender, IncomingMessageEventArgs e)
        {
            if (Math.Abs(e.PeerId) == Math.Abs(ViewModel.PeerId))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _typingResetTimer.Stop();
                    ChatSubtitleTextBlock.Text = _originalSubtitle;
                    ChatSubtitleTextBlock.Foreground = (Brush)Application.Current.Resources["PhoneSubtleForegroundBrush"];

                    if (e.MsgId != 0 && ViewModel.Messages.Any(m => m.Id == e.MsgId))
                        return;

                    string authorName = "";
                    if (!e.IsOut && (ViewModel.PeerType == 2 || ViewModel.PeerType == 3))
                    {
                        authorName = App.GetUserName(e.FromId);
                    }

                    ViewModel.Messages.Add(new MessageItemViewModel
                    {
                        Id = e.MsgId,
                        FromId = e.FromId,
                        AuthorName = authorName,
                        Text = e.Text,
                        Date = e.Date,
                        IsOutgoing = e.IsOut,
                        DeliveryStatus = 1,
                        IsService = false
                    });

                    ScrollToBottom();
                });

                if (!e.IsOut && App.Updates != null && e.MsgId > 0)
                {
                    Task.Run(async () =>
                    {
                        await App.Updates.ReadHistoryAsync(ViewModel.PeerId, ViewModel.AccessHash, ViewModel.PeerType, (int)e.MsgId);
                    });
                }
            }
        }

        private void OnLiveUserTyping(object sender, UserTypingEventArgs e)
        {
            if (Math.Abs(e.PeerId) == Math.Abs(ViewModel.PeerId))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    string typingText = "печатает...";
                    if (ViewModel.PeerType == 2 || ViewModel.PeerType == 3)
                    {
                        string name = App.GetUserName(e.UserId);
                        typingText = string.Format("{0} печатает...", name);
                    }

                    ChatSubtitleTextBlock.Text = typingText;
                    ChatSubtitleTextBlock.Foreground = (Brush)Application.Current.Resources["PhoneAccentBrush"];

                    _typingResetTimer.Stop();
                    _typingResetTimer.Start();
                });
            }
        }

        private void OnLiveOutboxRead(object sender, OutboxReadEventArgs e)
        {
            if (Math.Abs(e.PeerId) == Math.Abs(ViewModel.PeerId) || e.PeerId == 0)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ViewModel.ReadOutboxMaxId = Math.Max(ViewModel.ReadOutboxMaxId, e.MaxId);
                    foreach (var msg in ViewModel.Messages)
                    {
                        if (msg.IsOutgoing && (msg.Id <= e.MaxId || e.MaxId == 0))
                        {
                            msg.DeliveryStatus = 2;
                        }
                    }
                });
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string text = MessageInputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            MessageInputBox.Text = string.Empty;

            var localMsg = new MessageItemViewModel
            {
                Id = 0,
                Text = text,
                Date = DateTime.Now,
                IsOutgoing = true,
                DeliveryStatus = 0,
                IsService = false
            };

            ViewModel.Messages.Add(localMsg);
            ScrollToBottom();

            try
            {
                await App.EnsureTelegramConnectedAsync();
                await _messagesService.SendMessageAsync(ViewModel.PeerId, ViewModel.AccessHash, ViewModel.PeerType, text);

                localMsg.DeliveryStatus = 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось отправить: " + ex.Message, "Ошибка отправки", MessageBoxButton.OK);
            }
        }

        private void MessageInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendButton_Click(this, null);
            }
            else
            {
                SendTypingStatusIfNeeded();
            }
        }

        private void SendTypingStatusIfNeeded()
        {
            if ((DateTime.Now - _lastTypingSent).TotalSeconds > 4.5)
            {
                _lastTypingSent = DateTime.Now;
                Task.Run(async () =>
                {
                    if (_messagesService != null)
                    {
                        await _messagesService.SetTypingAsync(ViewModel.PeerId, ViewModel.AccessHash, ViewModel.PeerType);
                    }
                });
            }
        }

        private async void ForwardMessage_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as Microsoft.Phone.Controls.MenuItem;
            if (menuItem != null)
            {
                var msg = menuItem.DataContext as MessageItemViewModel;
                if (msg != null && msg.Id > 0)
                {
                    var targetChat = App.ViewModel.Dialogs.FirstOrDefault(d => d.Id != ViewModel.PeerId);
                    if (targetChat != null)
                    {
                        if (MessageBox.Show(string.Format("Переслать сообщение в чат «{0}»?", targetChat.Title), "Пересылка", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                        {
                            try
                            {
                                SetLoading(true, "Пересылка...");
                                await _messagesService.ForwardMessagesAsync(
                                    ViewModel.PeerId, ViewModel.AccessHash, ViewModel.PeerType,
                                    targetChat.Id, targetChat.AccessHash, targetChat.PeerType,
                                    (int)msg.Id);

                                SetLoading(false);
                                MessageBox.Show(string.Format("Сообщение успешно переслано в «{0}»!", targetChat.Title), "Успех", MessageBoxButton.OK);
                            }
                            catch (Exception ex)
                            {
                                SetLoading(false);
                                MessageBox.Show("Ошибка пересылки: " + ex.Message, "Ошибка", MessageBoxButton.OK);
                            }
                        }
                    }
                }
            }
        }

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as Microsoft.Phone.Controls.MenuItem;
            if (menuItem != null)
            {
                var msg = menuItem.DataContext as MessageItemViewModel;
                if (msg != null && !string.IsNullOrEmpty(msg.Text))
                {
                    Clipboard.SetText(msg.Text);
                }
            }
        }

        private async void DeleteForMe_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as Microsoft.Phone.Controls.MenuItem;
            if (menuItem != null)
            {
                var msg = menuItem.DataContext as MessageItemViewModel;
                if (msg != null)
                {
                    await DeleteMessageInternalAsync(msg, revoke: false);
                }
            }
        }

        private async void DeleteForEveryone_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as Microsoft.Phone.Controls.MenuItem;
            if (menuItem != null)
            {
                var msg = menuItem.DataContext as MessageItemViewModel;
                if (msg != null)
                {
                    await DeleteMessageInternalAsync(msg, revoke: true);
                }
            }
        }

        private async Task DeleteMessageInternalAsync(MessageItemViewModel msg, bool revoke)
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ViewModel.Messages.Remove(msg);
                });

                if (msg.Id > 0)
                {
                    await App.EnsureTelegramConnectedAsync();
                    if (_messagesService == null) _messagesService = new MessagesService(App.RpcEngine);

                    await _messagesService.DeleteMessagesAsync(
                        ViewModel.PeerId,
                        ViewModel.AccessHash,
                        ViewModel.PeerType,
                        (int)msg.Id,
                        revoke);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось удалить: " + ex.Message, "Ошибка", MessageBoxButton.OK);
            }
        }

        private void AttachPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            try { _photoChooser.Show(); } catch { }
        }

        private async void PhotoChooser_Completed(object sender, PhotoResult e)
        {
            if (e.TaskResult == TaskResult.OK && e.ChosenPhoto != null)
            {
                try
                {
                    BitmapImage previewBmp;
                    byte[] photoBytes = OptimizeAndCompressPhoto(e.ChosenPhoto, out previewBmp);

                    string caption = MessageInputBox.Text.Trim();
                    MessageInputBox.Text = string.Empty;

                    var localMsg = new MessageItemViewModel
                    {
                        Id = 0,
                        Text = caption,
                        Date = DateTime.Now,
                        IsOutgoing = true,
                        DeliveryStatus = 0,
                        IsService = false,
                        PhotoId = 1,
                        PhotoImage = previewBmp
                    };

                    Dispatcher.BeginInvoke(() =>
                    {
                        ViewModel.Messages.Add(localMsg);
                        ScrollToBottom();
                    });

                    SetLoading(true, "Отправка фото...");
                    await App.EnsureTelegramConnectedAsync();
                    if (_uploadService == null) _uploadService = new MediaUploadService(App.RpcEngine);

                    int chunkSize = 32768;
                    int totalParts = (photoBytes.Length + chunkSize - 1) / chunkSize;

                    long fileId = await _uploadService.UploadPhotoBytesAsync(photoBytes, (part, total) =>
                    {
                        SetLoading(true, string.Format("Отправка фото ({0}/{1})...", part, total));
                    });

                    await _uploadService.SendPhotoMessageAsync(
                        ViewModel.PeerId,
                        ViewModel.AccessHash,
                        ViewModel.PeerType,
                        fileId,
                        totalParts,
                        photoBytes,
                        caption);

                    localMsg.DeliveryStatus = 1;
                    SetLoading(false);
                }
                catch (Exception ex)
                {
                    SetLoading(false);
                    MessageBox.Show("Ошибка отправки фото: " + ex.Message, "Ошибка", MessageBoxButton.OK);
                }
            }
        }

        private byte[] OptimizeAndCompressPhoto(Stream srcStream, out BitmapImage previewBmp)
        {
            var rawBmp = new BitmapImage();
            rawBmp.SetSource(srcStream);

            var wb = new WriteableBitmap(rawBmp);
            int origW = wb.PixelWidth;
            int origH = wb.PixelHeight;

            int maxDim = 1280;
            int targetW = origW;
            int targetH = origH;

            if (origW > maxDim || origH > maxDim)
            {
                double ratio = Math.Min((double)maxDim / origW, (double)maxDim / origH);
                targetW = Math.Max(1, (int)(origW * ratio));
                targetH = Math.Max(1, (int)(origH * ratio));
            }

            using (var ms = new MemoryStream())
            {
                wb.SaveJpeg(ms, targetW, targetH, 0, 82);
                byte[] bytes = ms.ToArray();

                var preview = new BitmapImage();
                using (var previewMs = new MemoryStream(bytes))
                {
                    preview.SetSource(previewMs);
                }
                previewBmp = preview;
                return bytes;
            }
        }

        private void PhotoImage_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element != null)
            {
                var msg = element.DataContext as MessageItemViewModel;
                if (msg != null && msg.HasPhoto && msg.PhotoId > 1)
                {
                    string fileRefHex = msg.PhotoFileReference != null ? BytesToHex(msg.PhotoFileReference) : "";
                    string targetSize = !string.IsNullOrEmpty(msg.PhotoFullThumbSize) ? msg.PhotoFullThumbSize : "y";

                    string uri = string.Format("/Views/ImageViewerPage.xaml?photoId={0}&accessHash={1}&thumb={2}&ref={3}",
                        msg.PhotoId,
                        msg.PhotoAccessHash,
                        targetSize,
                        fileRefHex);

                    NavigationService.Navigate(new Uri(uri, UriKind.Relative));
                }
            }
        }

        private void ChatHeader_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            string uri = string.Format("/Views/ProfilePage.xaml?id={0}&accessHash={1}&peerType={2}&title={3}",
                ViewModel.PeerId,
                ViewModel.AccessHash,
                ViewModel.PeerType,
                Uri.EscapeDataString(ViewModel.Title ?? "Профиль"));

            NavigationService.Navigate(new Uri(uri, UriKind.Relative));
        }

        private void ScrollToBottom()
        {
            if (ViewModel.Messages.Count > 0)
            {
                MessagesList.ScrollTo(ViewModel.Messages[ViewModel.Messages.Count - 1]);
            }
        }

        private void SetLoading(bool isLoading, string text = "")
        {
            _progressIndicator.IsVisible = isLoading;
            _progressIndicator.IsIndeterminate = isLoading;
            _progressIndicator.Text = text;
        }

        private static string BytesToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("X2"));
            return sb.ToString();
        }
    }
}