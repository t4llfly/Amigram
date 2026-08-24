using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Xna.Framework.Media;
using MetroTelegram.TL;

namespace MetroTelegram.Views
{
    public partial class ImageViewerPage : PhoneApplicationPage
    {
        private MediaService _mediaService;
        private byte[] _currentPhotoBytes;

        private double _initialScale = 1.0;
        private double _minScale = 1.0;
        private double _maxScale = 4.0;

        public ImageViewerPage()
        {
            InitializeComponent();
            BuildApplicationBar();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (NavigationContext.QueryString.ContainsKey("photoId"))
            {
                long photoId = long.Parse(NavigationContext.QueryString["photoId"]);
                long accessHash = long.Parse(NavigationContext.QueryString["accessHash"]);
                string thumbSize = NavigationContext.QueryString.ContainsKey("thumb") ? NavigationContext.QueryString["thumb"] : "x";

                byte[] fileRef = null;
                if (NavigationContext.QueryString.ContainsKey("ref"))
                {
                    string hex = NavigationContext.QueryString["ref"];
                    fileRef = HexToBytes(hex);
                }

                await LoadFullImageAsync(photoId, accessHash, fileRef, thumbSize);
            }
        }

        private async Task LoadFullImageAsync(long photoId, long accessHash, byte[] fileRef, string thumbSize)
        {
            try
            {
                LoadingProgressBar.Visibility = Visibility.Visible;

                await App.EnsureTelegramConnectedAsync();
                _mediaService = new MediaService(App.RpcEngine);

                _currentPhotoBytes = await _mediaService.LoadPhotoBytesAsync(photoId, accessHash, fileRef, thumbSize);

                if (_currentPhotoBytes != null && _currentPhotoBytes.Length > 0)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(_currentPhotoBytes))
                        {
                            bmp.SetSource(ms);
                        }
                        TargetImage.Source = bmp;
                        LoadingProgressBar.Visibility = Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                LoadingProgressBar.Visibility = Visibility.Collapsed;
                MessageBox.Show("Ошибка загрузки фото: " + ex.Message, "Ошибка", MessageBoxButton.OK);
            }
        }

        #region Мультитач Gestures: Pinch-to-Zoom, Drag, DoubleTap

        private void GestureListener_PinchStarted(object sender, PinchStartedGestureEventArgs e)
        {
            _initialScale = ImageTransform.ScaleX;

            var center = e.GetPosition(TargetImage);
            ImageTransform.CenterX = center.X;
            ImageTransform.CenterY = center.Y;
        }

        private void GestureListener_PinchDelta(object sender, PinchGestureEventArgs e)
        {
            double newScale = _initialScale * e.DistanceRatio;
            if (newScale < _minScale) newScale = _minScale;
            if (newScale > _maxScale) newScale = _maxScale;

            ImageTransform.ScaleX = newScale;
            ImageTransform.ScaleY = newScale;
        }

        private void GestureListener_PinchCompleted(object sender, PinchGestureEventArgs e)
        {
            if (ImageTransform.ScaleX <= 1.05)
            {
                ResetTransform();
            }
        }

        private void GestureListener_DragDelta(object sender, DragDeltaGestureEventArgs e)
        {
            if (ImageTransform.ScaleX > 1.0)
            {
                ImageTransform.TranslateX += e.HorizontalChange;
                ImageTransform.TranslateY += e.VerticalChange;
            }
        }

        private void GestureListener_DoubleTap(object sender, Microsoft.Phone.Controls.GestureEventArgs e)
        {
            if (ImageTransform.ScaleX > 1.0)
            {
                ResetTransform();
            }
            else
            {
                var center = e.GetPosition(TargetImage);
                ImageTransform.CenterX = center.X;
                ImageTransform.CenterY = center.Y;
                ImageTransform.ScaleX = 2.5;
                ImageTransform.ScaleY = 2.5;
            }
        }

        private void ResetTransform()
        {
            ImageTransform.ScaleX = 1.0;
            ImageTransform.ScaleY = 1.0;
            ImageTransform.TranslateX = 0;
            ImageTransform.TranslateY = 0;
            ImageTransform.CenterX = 0;
            ImageTransform.CenterY = 0;
        }

        #endregion

        #region Сохранение в Галерею телефона (MediaLibrary)

        private void BuildApplicationBar()
        {
            ApplicationBar = new ApplicationBar();
            ApplicationBar.Mode = ApplicationBarMode.Minimized;
            ApplicationBar.Opacity = 0.8;
            ApplicationBar.BackgroundColor = System.Windows.Media.Color.FromArgb(255, 0, 0, 0);
            ApplicationBar.ForegroundColor = System.Windows.Media.Color.FromArgb(255, 255, 255, 255);

            ApplicationBarMenuItem saveMenuItem = new ApplicationBarMenuItem("сохранить в галерею");
            saveMenuItem.Click += (s, e) => SavePhotoToGallery();
            ApplicationBar.MenuItems.Add(saveMenuItem);
        }

        private void SavePhotoToGallery()
        {
            if (_currentPhotoBytes == null || _currentPhotoBytes.Length == 0)
            {
                MessageBox.Show("Изображение еще не загружено.", "Внимание", MessageBoxButton.OK);
                return;
            }

            try
            {
                using (var ml = new MediaLibrary())
                {
                    string filename = string.Format("Telegram_{0}.jpg", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    ml.SavePicture(filename, _currentPhotoBytes);
                }

                MessageBox.Show("Фотография успешно сохранена в альбом «Сохраненные фото»!", "Галерея", MessageBoxButton.OK);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка сохранения: " + ex.Message + "\nУбедитесь, что включена возможность ID_CAP_MEDIALIB_PHOTO в манифесте.", "Ошибка", MessageBoxButton.OK);
            }
        }

        #endregion

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            int len = hex.Length / 2;
            byte[] bytes = new byte[len];
            for (int i = 0; i < len; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}