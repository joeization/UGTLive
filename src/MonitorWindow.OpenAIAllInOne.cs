using System;
using System.Drawing;
using System.Windows;
using System.Windows.Media.Imaging;

namespace UGTLive
{
    public sealed record OpenAIAllInOneSnapshotImages(BitmapSource Source, BitmapSource? Translated);

    public partial class MonitorWindow
    {
        private BitmapSource? _openAIAllInOneSourceImage;
        private BitmapSource? _openAIAllInOneTranslatedImage;
        private bool _hasOpenAIAllInOneSnapshot;

        public void BeginOpenAIAllInOneSnapshot(Bitmap source)
        {
            if (!Dispatcher.CheckAccess())
            {
                using Bitmap clone = (Bitmap)source.Clone();
                Dispatcher.Invoke(() => BeginOpenAIAllInOneSnapshot(clone));
                return;
            }

            _openAIAllInOneSourceImage = CreateFrozenBitmapSource(source);
            _openAIAllInOneTranslatedImage = null;
            _hasOpenAIAllInOneSnapshot = true;
            RefreshOpenAIAllInOneDisplay();
        }

        public void SetOpenAIAllInOneTranslatedImage(BitmapSource translatedImage)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOpenAIAllInOneTranslatedImage(translatedImage));
                return;
            }

            _openAIAllInOneTranslatedImage = translatedImage;
            _hasOpenAIAllInOneSnapshot = true;
            RefreshOpenAIAllInOneDisplay();
        }

        public void ClearOpenAIAllInOneSnapshot()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearOpenAIAllInOneSnapshot);
                return;
            }

            if (_openAIAllInOneSourceImage != null)
                captureImage.Source = _openAIAllInOneSourceImage;

            _hasOpenAIAllInOneSnapshot = false;
            _openAIAllInOneSourceImage = null;
            _openAIAllInOneTranslatedImage = null;
            textOverlayWebView.Visibility = Visibility.Visible;
        }

        public bool RefreshOpenAIAllInOneDisplay()
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(RefreshOpenAIAllInOneDisplay);
            if (!_hasOpenAIAllInOneSnapshot)
                return false;

            textOverlayWebView.Visibility = Visibility.Collapsed;
            captureImage.Source = _currentOverlayMode == OverlayMode.Translated && _openAIAllInOneTranslatedImage != null
                ? _openAIAllInOneTranslatedImage
                : _openAIAllInOneSourceImage;
            UpdateScrollViewerSettings();
            return true;
        }

        public OpenAIAllInOneSnapshotImages? GetOpenAIAllInOneSnapshotImages()
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(GetOpenAIAllInOneSnapshotImages);
            if (!_hasOpenAIAllInOneSnapshot || _openAIAllInOneSourceImage == null)
                return null;
            return new OpenAIAllInOneSnapshotImages(
                _openAIAllInOneSourceImage,
                _openAIAllInOneTranslatedImage);
        }

        public void SetOpenAIAllInOneComparisonMode(OverlayMode mode)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOpenAIAllInOneComparisonMode(mode));
                return;
            }
            if (!_hasOpenAIAllInOneSnapshot)
                return;

            _currentOverlayMode = mode == OverlayMode.Source ? OverlayMode.Source : OverlayMode.Translated;
            overlaySourceRadio.Checked -= OverlayRadioButton_Checked;
            overlayTranslatedRadio.Checked -= OverlayRadioButton_Checked;
            if (_currentOverlayMode == OverlayMode.Source)
                overlaySourceRadio.IsChecked = true;
            else
                overlayTranslatedRadio.IsChecked = true;
            overlaySourceRadio.Checked += OverlayRadioButton_Checked;
            overlayTranslatedRadio.Checked += OverlayRadioButton_Checked;

            string modeName = _currentOverlayMode == OverlayMode.Source ? "Source" : "Translated";
            ConfigManager.Instance.SetMonitorOverlayMode(modeName);
            RefreshOpenAIAllInOneDisplay();
        }

        internal static BitmapSource CreateFrozenBitmapSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
    }
}
