using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using MetroTelegram.TL;
using MetroTelegram.ViewModels;
using System.Diagnostics;

namespace MetroTelegram
{
    public partial class MainPage : PhoneApplicationPage
    {
        private ProgressIndicator _progressIndicator;
        private DialogsService _dialogsService;
        private ContactsService _contactsService;
        private MediaService _mediaService;

        public MainPage()
        {
            InitializeComponent();
            DataContext = App.ViewModel;

            _progressIndicator = new ProgressIndicator();
            SystemTray.SetProgressIndicator(this, _progressIndicator);

            BuildLocalizedApplicationBar();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            App.Storage.Load();

            if (!App.Storage.IsAuthorized)
            {
                NavigationService.Navigate(new Uri("/Views/AuthPage.xaml", UriKind.Relative));
                return;
            }

            if (ChatsList != null) ChatsList.SelectedItem = null;
            if (ContactsList != null) ContactsList.SelectedItem = null;

            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            try
            {
                SetLoading(true, "Синхронизация...");

                await App.EnsureTelegramConnectedAsync(s => SetLoading(true, s));

                App.ViewModel.Settings.LoadFromStorage(App.Storage);

                _dialogsService = new DialogsService(App.RpcEngine);
                _contactsService = new ContactsService(App.RpcEngine);
                _mediaService = new MediaService(App.RpcEngine);

                try
                {
                    List<ChatItemViewModel> dialogs = await _dialogsService.GetDialogsAsync(30);

                    Dispatcher.BeginInvoke(() =>
                    {
                        App.ViewModel.Dialogs.Clear();
                        foreach (var d in dialogs)
                        {
                            App.ViewModel.Dialogs.Add(d);

                            if (d.PhotoId != 0)
                            {
                                Task.Run(async () =>
                                {
                                    byte[] avatarBytes = await _mediaService.LoadAvatarBytesAsync(
                                        d.Id, d.AccessHash, d.PeerType, d.PhotoId);

                                    if (avatarBytes != null && avatarBytes.Length > 0)
                                    {
                                        Dispatcher.BeginInvoke(() =>
                                        {
                                            try
                                            {
                                                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                                using (var ms = new System.IO.MemoryStream(avatarBytes))
                                                {
                                                    bmp.SetSource(ms);
                                                }
                                                d.AvatarImage = bmp;
                                            }
                                            catch { }
                                        });
                                    }
                                });
                            }
                        }
                    });
                }
                catch (Exception exDialogs)
                {
                    Debug.WriteLine("[MainPage] Ошибка диалогов: " + exDialogs.Message);
                }

                try
                {
                    List<ContactItemViewModel> contacts = await _contactsService.GetContactsAsync();
                    var groupedContacts = AlphaKeyGroup<ContactItemViewModel>.CreateGroups(contacts);

                    Dispatcher.BeginInvoke(() =>
                    {
                        App.ViewModel.GroupedContacts.Clear();
                        foreach (var g in groupedContacts)
                        {
                            App.ViewModel.GroupedContacts.Add(g);
                        }
                    });
                }
                catch (Exception exContacts)
                {
                    Debug.WriteLine("[MainPage] Ошибка контактов: " + exContacts.Message);
                }

                SetLoading(false);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    SetLoading(false);

                    if (ex.Message.Contains("401") || ex.Message.Contains("UNAUTHORIZED") || ex.Message.Contains("AUTH_KEY_UNREGISTERED"))
                    {
                        App.Storage.Clear();
                        App.Transport?.Disconnect();
                        NavigationService.Navigate(new Uri("/Views/AuthPage.xaml", UriKind.Relative));
                        return;
                    }
                });
            }
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            lock (App.UsersCache)
            {
                int count = App.UsersCache.Count;
                App.UsersCache.Clear();
                MessageBox.Show(string.Format("Кэш пользователей очищен ({0} записей освобождено).", count), "Память", MessageBoxButton.OK);
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Вы уверены, что хотите выйти из аккаунта Telegram?", "Выход", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            {
                try
                {
                    SetLoading(true, "Выход из аккаунта...");

                    if (App.RpcEngine != null && App.Transport != null && App.Transport.IsConnected)
                    {
                        byte[] logoutQuery;
                        using (var writer = new TlBinaryWriter())
                        {
                            writer.WriteUInt32(0x3e72ba15);
                            logoutQuery = writer.ToByteArray();
                        }
                        await App.RpcEngine.SendRpcQueryAsync(logoutQuery, wrapInitConnection: false, timeoutMs: 5000);
                    }
                }
                catch { }
                finally
                {
                    App.Storage.Clear();
                    App.Transport?.Disconnect();
                    SetLoading(false);

                    NavigationService.Navigate(new Uri("/Views/AuthPage.xaml", UriKind.Relative));
                }
            }
        }

        private void ChatsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ChatItemViewModel selectedChat = ChatsList.SelectedItem as ChatItemViewModel;
            if (selectedChat != null)
            {
                string uri = string.Format("/Views/ChatPage.xaml?id={0}&accessHash={1}&peerType={2}&title={3}",
                    selectedChat.Id,
                    selectedChat.AccessHash,
                    selectedChat.PeerType,
                    Uri.EscapeDataString(selectedChat.Title ?? "Диалог"));

                NavigationService.Navigate(new Uri(uri, UriKind.Relative));
            }
        }

        private void ContactsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ContactItemViewModel selectedContact = ContactsList.SelectedItem as ContactItemViewModel;
            if (selectedContact != null)
            {
                string uri = string.Format("/Views/ChatPage.xaml?id={0}&accessHash={1}&peerType={2}&title={3}",
                    selectedContact.UserId,
                    selectedContact.AccessHash,
                    1,
                    Uri.EscapeDataString(selectedContact.FullName ?? "Пользователь"));

                NavigationService.Navigate(new Uri(uri, UriKind.Relative));
            }
        }

        private void BuildLocalizedApplicationBar()
        {
            ApplicationBar = new ApplicationBar();
            ApplicationBar.Mode = ApplicationBarMode.Default;
            ApplicationBar.Opacity = 0.99;
            ApplicationBar.IsVisible = true;
            ApplicationBar.IsMenuEnabled = true;
            ApplicationBar.BackgroundColor = System.Windows.Media.Color.FromArgb(255, 0, 0, 0);
            ApplicationBar.ForegroundColor = System.Windows.Media.Color.FromArgb(255, 255, 255, 255);

            ApplicationBarIconButton refreshBtn = new ApplicationBarIconButton(new Uri("/Assets/appbar.sync.png", UriKind.Relative));
            refreshBtn.Text = "обновить";
            refreshBtn.Click += async (s, e) => { await LoadDataAsync(); };
            ApplicationBar.Buttons.Add(refreshBtn);

            ApplicationBarMenuItem logoutMenuItem = new ApplicationBarMenuItem("выйти из аккаунта");
            logoutMenuItem.Click += (s, e) =>
            {
                App.Storage.Clear();
                App.Transport?.Disconnect();
                NavigationService.Navigate(new Uri("/Views/AuthPage.xaml", UriKind.Relative));
            };
            ApplicationBar.MenuItems.Add(logoutMenuItem);
        }

        private void SetLoading(bool isLoading, string text = "")
        {
            _progressIndicator.IsVisible = isLoading;
            _progressIndicator.IsIndeterminate = isLoading;
            _progressIndicator.Text = text;
        }
    }
}