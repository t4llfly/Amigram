using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using MetroTelegram.TL;
using MetroTelegram.ViewModels;

namespace MetroTelegram.Views
{
    public partial class ProfilePage : PhoneApplicationPage
    {
        private ProgressIndicator _progressIndicator;
        private ProfileService _profileService;

        public ProfileViewModel ViewModel { get; private set; }

        public ProfilePage()
        {
            InitializeComponent();

            ViewModel = new ProfileViewModel();
            DataContext = ViewModel;

            _progressIndicator = new ProgressIndicator();
            SystemTray.SetProgressIndicator(this, _progressIndicator);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (NavigationContext.QueryString.ContainsKey("id"))
            {
                ViewModel.Id = long.Parse(NavigationContext.QueryString["id"]);
                ViewModel.AccessHash = long.Parse(NavigationContext.QueryString["accessHash"]);
                ViewModel.PeerType = int.Parse(NavigationContext.QueryString["peerType"]);
                ViewModel.Title = Uri.UnescapeDataString(NavigationContext.QueryString["title"]);

                ViewModel.AvatarInitials = GetInitials(ViewModel.Title);

                await LoadProfileDataAsync();
            }
        }

        private async Task LoadProfileDataAsync()
        {
            try
            {
                SetLoading(true, "Загрузка профиля...");

                await App.EnsureTelegramConnectedAsync(s => SetLoading(true, s));
                _profileService = new ProfileService(App.RpcEngine);

                var profile = await _profileService.GetFullProfileAsync(
                    ViewModel.Id, ViewModel.AccessHash, ViewModel.PeerType, ViewModel.Title);

                if (ViewModel.PeerType == 3)
                {
                    SetLoading(true, "Загрузка участников...");
                    try
                    {
                        var channelMembers = await _profileService.GetChannelParticipantsAsync(
                            ViewModel.Id, ViewModel.AccessHash, 0, 200);
                        profile.Members.Clear();
                        profile.Members.AddRange(channelMembers);
                        profile.ParticipantsCount = channelMembers.Count;
                    }
                    catch (Exception exMembers)
                    {
                        Debug.WriteLine("[ProfilePage] Ошибка загрузки участников канала: " + exMembers.Message);
                    }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    ViewModel.Subtitle = profile.Status;
                    ViewModel.About = profile.About;
                    ViewModel.Phone = profile.Phone;
                    ViewModel.Username = profile.Username;

                    ViewModel.Members.Clear();
                    foreach (var m in profile.Members)
                    {
                        ViewModel.Members.Add(m);
                    }

                    SetLoading(false);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    SetLoading(false);
                    Debug.WriteLine("[ProfilePage] Ошибка загрузки профиля: " + ex.Message);
                });
            }
        }

        private void MembersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ContactItemViewModel selected = MembersList.SelectedItem as ContactItemViewModel;
            if (selected != null)
            {
                string uri = string.Format("/Views/ChatPage.xaml?id={0}&accessHash={1}&peerType={2}&title={3}",
                    selected.UserId,
                    selected.AccessHash,
                    1,
                    Uri.EscapeDataString(selected.FullName ?? "Пользователь"));

                NavigationService.Navigate(new Uri(uri, UriKind.Relative));
            }
        }

        private void SetLoading(bool isLoading, string text = "")
        {
            _progressIndicator.IsVisible = isLoading;
            _progressIndicator.IsIndeterminate = isLoading;
            _progressIndicator.Text = text;
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