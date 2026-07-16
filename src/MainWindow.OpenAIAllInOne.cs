using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UGTLive
{
    public partial class MainWindow
    {
        private readonly OpenAIAllInOneImageService _openAIAllInOneService = new();
        private CancellationTokenSource? _openAIAllInOneCancellation;
        private long _openAIAllInOneRequestId;
        private DispatcherTimer? _openAIAllInOneStatusTimer;
        private DateTime _openAIAllInOneStartTime;
        private OpenAIAllInOneStage _openAIAllInOneStage;
        private BitmapSource? _openAIAllInOneSourceImageSource;
        private BitmapSource? _openAIAllInOneImageSource;

        public void OnSnapshotProcessingModeChanged()
        {
            CancelOpenAIAllInOneSnapshot();
            ClearOpenAIAllInOneOverlay();
            if (_snapshotInProgress)
            {
                _snapshotInProgress = false;
                _isSnapshotOverlayDisplayed = false;
                UpdateSnapshotButtonState();
            }
            UpdateHotkeyTooltips();
        }

        private void StartOpenAIAllInOneSnapshot(Bitmap bitmap)
        {
            Bitmap bitmapClone = (Bitmap)bitmap.Clone();
            CancelOpenAIAllInOneSnapshot();
            _openAIAllInOneSourceImageSource = MonitorWindow.CreateFrozenBitmapSource(bitmap);
            _toolbarWindow?.HideOpenAIAllInOneResult();
            long requestId = Interlocked.Increment(ref _openAIAllInOneRequestId);
            var cancellation = new CancellationTokenSource();
            _openAIAllInOneCancellation = cancellation;
            CancellationToken cancellationToken = cancellation.Token;
            _openAIAllInOneStartTime = DateTime.Now;
            _openAIAllInOneStage = OpenAIAllInOneStage.Preparing;
            StartOpenAIAllInOneStatusTimer();

            var progress = new Progress<OpenAIAllInOneStage>(stage =>
            {
                if (requestId == _openAIAllInOneRequestId)
                {
                    _openAIAllInOneStage = stage;
                    UpdateOpenAIAllInOneStatus();
                }
            });

            _ = RunOpenAIAllInOneSnapshotAsync(bitmapClone, requestId, cancellation, cancellationToken, progress);
        }

        private async Task RunOpenAIAllInOneSnapshotAsync(
            Bitmap bitmap,
            long requestId,
            CancellationTokenSource cancellation,
            CancellationToken cancellationToken,
            IProgress<OpenAIAllInOneStage> progress)
        {
            try
            {
                string sourceLanguage = Logic.GetLanguageName(ConfigManager.Instance.GetSourceLanguage());
                string targetLanguage = Logic.GetLanguageName(ConfigManager.Instance.GetTargetLanguage());
                string apiKey = ConfigManager.Instance.GetOpenAIAllInOneApiKey();
                string model = ConfigManager.Instance.GetOpenAIAllInOneModel();
                OpenAIAllInOneQuality quality = ConfigManager.Instance.GetOpenAIAllInOneQuality();
                OpenAIAllInOneResult result = await Task.Run(
                    () => _openAIAllInOneService.TranslateImageAsync(
                        bitmap,
                        sourceLanguage,
                        targetLanguage,
                        apiKey,
                        model,
                        quality,
                        progress,
                        cancellationToken),
                    cancellationToken);

                if (requestId != _openAIAllInOneRequestId || cancellationToken.IsCancellationRequested)
                    return;

                BitmapSource image = CreateFrozenBitmapSource(result.ImageBytes);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (requestId != _openAIAllInOneRequestId)
                        return;

                    _openAIAllInOneImageSource = image;
                    MonitorWindow.Instance.SetOpenAIAllInOneTranslatedImage(image);
                    SetOpenAIAllInOneComparisonMode(OverlayMode.Translated);
                    UpdateOpenAIAllInOneOverlayVisibility();
                    StopOpenAIAllInOneStatusTimer();
                    double elapsedSeconds = result.Elapsed.TotalSeconds;
                    SetOpenAIAllInOneStatus($"Translated image ready - {elapsedSeconds:F1} seconds", showProgress: false);
                    _toolbarWindow?.ShowOpenAIAllInOneResult(elapsedSeconds, showingTranslated: true);
                    OnSnapshotComplete(true);
                });
            }
            catch (OperationCanceledException)
            {
                if (requestId == _openAIAllInOneRequestId)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StopOpenAIAllInOneStatusTimer();
                        ClearOpenAIAllInOneOverlay();
                        SetOpenAIAllInOneStatus("Snapshot canceled", showProgress: false);
                        OnSnapshotComplete(false);
                    });
                }
            }
            catch (Exception ex)
            {
                if (requestId == _openAIAllInOneRequestId)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StopOpenAIAllInOneStatusTimer();
                        ClearOpenAIAllInOneOverlay();
                        SetOpenAIAllInOneStatus("OpenAI All In One failed", showProgress: false);
                        OnSnapshotComplete(false);
                        ErrorPopupManager.ShowError(ex.Message, "OpenAI All In One");
                    });
                }
            }
            finally
            {
                bitmap.Dispose();
                if (ReferenceEquals(_openAIAllInOneCancellation, cancellation))
                    _openAIAllInOneCancellation = null;
                cancellation.Dispose();
            }
        }

        private void CancelOpenAIAllInOneSnapshot()
        {
            Interlocked.Increment(ref _openAIAllInOneRequestId);
            CancellationTokenSource? cancellation = _openAIAllInOneCancellation;
            _openAIAllInOneCancellation = null;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
            StopOpenAIAllInOneStatusTimer();
        }

        private void ClearOpenAIAllInOneOverlay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearOpenAIAllInOneOverlay);
                return;
            }

            _openAIAllInOneSourceImageSource = null;
            _openAIAllInOneImageSource = null;
            allInOneOverlayImage.Source = null;
            allInOneOverlayImage.Visibility = Visibility.Collapsed;
            textOverlayWebView.Visibility = Visibility.Visible;
            _toolbarWindow?.HideOpenAIAllInOneResult();
            MonitorWindow.Instance.ClearOpenAIAllInOneSnapshot();
        }

        private bool UpdateOpenAIAllInOneOverlayVisibility()
        {
            BitmapSource? selectedImage = null;
            if (_openAIAllInOneImageSource != null)
            {
                selectedImage = _currentOverlayMode switch
                {
                    OverlayMode.Translated => _openAIAllInOneImageSource,
                    OverlayMode.Source => _openAIAllInOneSourceImageSource,
                    _ => null
                };
            }

            bool showImage = selectedImage != null;
            allInOneOverlayImage.Source = selectedImage;
            allInOneOverlayImage.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
            textOverlayWebView.Visibility = showImage ? Visibility.Collapsed : Visibility.Visible;
            if (_openAIAllInOneImageSource != null)
                _toolbarWindow?.SyncOpenAIAllInOneComparison(_currentOverlayMode == OverlayMode.Translated);
            MonitorWindow.Instance.RefreshOpenAIAllInOneDisplay();
            return showImage;
        }

        public void HandleOpenAIAllInOneComparisonToggle()
        {
            if (_openAIAllInOneSourceImageSource == null || _openAIAllInOneImageSource == null)
                return;

            OverlayMode nextMode = _currentOverlayMode == OverlayMode.Translated
                ? OverlayMode.Source
                : OverlayMode.Translated;
            SetOpenAIAllInOneComparisonMode(nextMode);
            RefreshMainWindowOverlays();
        }

        private void SetOpenAIAllInOneComparisonMode(OverlayMode mode)
        {
            _currentOverlayMode = mode;
            string modeName = mode == OverlayMode.Source ? "Source" : "Translated";
            ConfigManager.Instance.SetMainWindowOverlayMode(modeName);
            _toolbarWindow?.SyncOverlayMode(modeName);
            _toolbarWindow?.SyncOpenAIAllInOneComparison(mode == OverlayMode.Translated);
            MonitorWindow.Instance.SetOpenAIAllInOneComparisonMode(mode);
        }

        private void StartOpenAIAllInOneStatusTimer()
        {
            _openAIAllInOneStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _openAIAllInOneStatusTimer.Tick -= OpenAIAllInOneStatusTimer_Tick;
            _openAIAllInOneStatusTimer.Tick += OpenAIAllInOneStatusTimer_Tick;
            _openAIAllInOneStatusTimer.Start();
            UpdateOpenAIAllInOneStatus();
        }

        private void OpenAIAllInOneStatusTimer_Tick(object? sender, EventArgs e)
        {
            UpdateOpenAIAllInOneStatus();
        }

        private void UpdateOpenAIAllInOneStatus()
        {
            TimeSpan elapsed = DateTime.Now - _openAIAllInOneStartTime;
            string stage = _openAIAllInOneStage == OpenAIAllInOneStage.Preparing
                ? "preparing image"
                : "translating and rendering";
            SetOpenAIAllInOneStatus($"OpenAI All In One: {stage}... {FormatOperationElapsed(elapsed)}", showProgress: true);
        }

        private void SetOpenAIAllInOneStatus(string status, bool showProgress)
        {
            TranslationStatus.SetStatus(status);
            if (translationStatusLabel != null)
                translationStatusLabel.Text = status;
            if (translationStatusBorder != null)
                translationStatusBorder.Visibility = Visibility.Visible;
            if (translationProgressBar != null)
            {
                translationProgressBar.IsIndeterminate = showProgress;
                translationProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void StopOpenAIAllInOneStatusTimer()
        {
            _openAIAllInOneStatusTimer?.Stop();
        }

        private static string FormatOperationElapsed(TimeSpan elapsed)
        {
            return $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        }

        private static BitmapSource CreateFrozenBitmapSource(byte[] imageBytes)
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
