using DraggerResizer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoCropperPage;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using WinUIShared.Controls;
using WinUIShared.Enums;
using WinUIShared.Helpers;
using Orientation = DraggerResizer.Orientation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VideoCropper
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class VideoCropperPage : Page
    {
        private DraggerResizer.DraggerResizer resizer;
        private readonly CropperModel viewModel;
        // Set by navigation before processing controls are used.
        private CropProcessor cropProcessor = null!;
        private RectangleGeometry mask => (RectangleGeometry)((GeometryGroup)OverlayAndMask.Data).Children[1];
        private const string AspectRatioPresetSetting = "Crop.AspectRatioPreset";
        private const string CenterPresetSetting = "Crop.CenterPreset";
        private const string LockAspectRatioSetting = "Crop.LockAspectRatio";
        private const string ZoomIntoFrameSetting = "Crop.ZoomIntoFrame";
        private const string MouseWheelZoomSetting = "Crop.MouseWheelZoom";
        private const string NormalizePixelAspectRatioSetting = "Crop.NormalizePixelAspectRatio";
        private const string XPercentSetting = "Crop.XPercent";
        private const string YPercentSetting = "Crop.YPercent";
        private const string PreviewWhileProcessingSetting = "Crop.PreviewWhileProcessing";
        private const string NotifyWhenDoneSetting = "Crop.NotifyWhenDone";
        private readonly ProcessingPreviewPosition processingPreview = new();
        private readonly DispatcherTimer processingPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private bool pageActive;
        private ContentDialog? quitDialog;
        private readonly ObservableCollection<AspectRatio> ratios = [];
        private const double IconMaxSize = 40;
        private string? outputFile;
        private string? navigateTo;
        // Navigation supplies the executable path; LoadVideo selects the input.
        private string ffmpegPath = null!;
        private string videoPath => videoPaths[currentVideoIndex];
        private readonly List<string> videoPaths = [];
        private int currentVideoIndex;
        private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private bool videoSizeChanged;
        private bool applyingResize;
        private bool cropFrameInitialized;
        private int quarterTurns;
        private bool restoringCrop;
        private double? savedWidthPercent;
        private double? savedHeightPercent;
        private bool centerPresetSelected;
        private bool zoomIntoFrame;
        private string? savedAspectRatioTitle;
        private double? savedXPercent;
        private double? savedYPercent;
        private AspectRatio? selectedAspectRatio;
        private Storyboard? transformStoryboard;
        private (string XText, string YText, string X2Text, string Y2Text) previousRect;

        public VideoCropperPage()
        {
            InitializeComponent();
            resizer = new DraggerResizer.DraggerResizer();
            viewModel = new CropperModel();
            PopulateAspectRatios();
            resizeTimer.Tick += (_, _) => ApplyPendingResize();
            VideoPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            processingPreviewTimer.Tick += UpdateProcessingPreviewPosition;
            VideoPlayer.MediaPlayer.MediaOpened += (player, _) =>
            {
                App.Diagnostics.Log($"Media opened: {player.PlaybackSession.NaturalVideoWidth}x{player.PlaybackSession.NaturalVideoHeight}; duration={player.PlaybackSession.NaturalDuration}");
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!pageActive || VideoPlayer.ActualWidth == 0 || VideoPlayer.ActualHeight == 0) return;
                    UpdateVideoRotation();
                    InitializeCropFrame(quarterTurns % 2 == 0
                        ? new Size(VideoPlayer.ActualWidth, VideoPlayer.ActualHeight)
                        : new Size(VideoPlayer.ActualHeight, VideoPlayer.ActualWidth));
                });
            };
            VideoPlayer.MediaPlayer.MediaFailed += (_, args) => App.Diagnostics.Report(
                $"Media failure: {args.Error}; {args.ErrorMessage}", args.ExtendedErrorCode);
            var settings = VideoCropper.App.Settings;
            NotifyWhenDoneCheckBox.IsEnabled = !AppStorage.NoProfile;
            if (AppStorage.NoProfile)
                ToolTipService.SetToolTip(NotifyWhenDoneCheckBox, "Windows notifications are disabled in -NoProfile mode.");
            if (settings.ContainsKey(NotifyWhenDoneSetting))
                NotifyWhenDoneCheckBox.IsChecked = settings.Get<bool>(NotifyWhenDoneSetting);
            NotifyWhenDoneCheckBox.Checked += (_, _) => App.Settings[NotifyWhenDoneSetting] = true;
            NotifyWhenDoneCheckBox.Unchecked += (_, _) => App.Settings[NotifyWhenDoneSetting] = false;
            if (settings.ContainsKey("Crop.QuarterTurns"))
                quarterTurns = settings.Get<int>("Crop.QuarterTurns");
            if (settings.ContainsKey("Crop.WidthPercent"))
            {
                savedWidthPercent = settings.Get<double>("Crop.WidthPercent");
                savedHeightPercent = settings.Get<double>("Crop.HeightPercent");
            }
            if (settings.ContainsKey(PreviewWhileProcessingSetting))
                PreviewWhileProcessingCheckBox.IsChecked = settings.Get<bool>(PreviewWhileProcessingSetting);
            PreviewWhileProcessingCheckBox.Checked += PreviewWhileProcessingChanged;
            PreviewWhileProcessingCheckBox.Unchecked += PreviewWhileProcessingChanged;
            if (settings.ContainsKey("Crop.AutoAdvance"))
                AutoAdvanceCheckBox.IsChecked = settings.Get<bool>("Crop.AutoAdvance");
            AutoAdvanceCheckBox.Checked += (_, _) => App.Settings["Crop.AutoAdvance"] = true;
            AutoAdvanceCheckBox.Unchecked += (_, _) => App.Settings["Crop.AutoAdvance"] = false;
            if (settings.ContainsKey("Crop.QuitWhenDone"))
                QuitWhenDoneCheckBox.IsChecked = settings.Get<bool>("Crop.QuitWhenDone");
            QuitWhenDoneCheckBox.Checked += (_, _) => App.Settings["Crop.QuitWhenDone"] = true;
            QuitWhenDoneCheckBox.Unchecked += (_, _) => App.Settings["Crop.QuitWhenDone"] = false;
            if (settings.ContainsKey(AspectRatioPresetSetting))
                savedAspectRatioTitle = settings.Get<string>(AspectRatioPresetSetting);
            if (settings.ContainsKey(CenterPresetSetting))
                centerPresetSelected = settings.Get<bool>(CenterPresetSetting);
            if (settings.ContainsKey(LockAspectRatioSetting))
                AspectRatioCheckBox.IsChecked = settings.Get<bool>(LockAspectRatioSetting);
            if (settings.ContainsKey(ZoomIntoFrameSetting))
                ZoomIntoFrameCheckBox.IsChecked = settings.Get<bool>(ZoomIntoFrameSetting);
            if (settings.ContainsKey(MouseWheelZoomSetting))
                MouseWheelZoomCheckBox.IsChecked = settings.Get<bool>(MouseWheelZoomSetting);
            if (settings.ContainsKey(NormalizePixelAspectRatioSetting))
                NormalizePixelAspectRatioCheckBox.IsChecked = settings.Get<bool>(NormalizePixelAspectRatioSetting);
            if (settings.ContainsKey(XPercentSetting))
                savedXPercent = settings.Get<double>(XPercentSetting);
            if (settings.ContainsKey(YPercentSetting))
                savedYPercent = settings.Get<double>(YPercentSetting);
            viewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(CropperModel.DuringOperation))
                {
                    UpdateVideoNavigation();
                    UpdateProcessingPreview();
                }
                if (args.PropertyName == nameof(CropperModel.BeforeOperation))
                {
                    if (zoomIntoFrame) FocusCropFrame();
                    else FitToView();
                }
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            pageActive = true;
            VideoPlayer.MediaPlayer.PlaybackSession.SeekCompleted += PreviewSeekCompleted;
            var props = (CropperProps)e.Parameter;
            ffmpegPath = props.FfmpegPath;
            cropProcessor = new CropProcessor(ffmpegPath);
            cropProcessor.ProcessingPositionChanged += processingPreview.Report;
            videoPaths.AddRange(props.VideoPaths);
            navigateTo = props.TypeToNavigateTo;
            base.OnNavigatedTo(e);
            LoadVideo();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            pageActive = false;
            resizeTimer.Stop();
            videoSizeChanged = false;
            quitDialog?.Hide();
            processingPreviewTimer.Stop();
            cropProcessor.ProcessingPositionChanged -= processingPreview.Report;
            VideoPlayer.MediaPlayer.PlaybackSession.SeekCompleted -= PreviewSeekCompleted;
            base.OnNavigatedFrom(e);
        }

        private void PreviewWhileProcessingChanged(object sender, RoutedEventArgs e)
        {
            App.Settings[PreviewWhileProcessingSetting] = PreviewWhileProcessingCheckBox.IsChecked == true;
            UpdateProcessingPreview();
        }

        private void UpdateProcessingPreview()
        {
            var enabled = pageActive && viewModel.DuringOperation && PreviewWhileProcessingCheckBox.IsChecked == true;
            VideoControls.IsEnabled = !enabled;
            if (enabled)
            {
                VideoPlayer.MediaPlayer.Pause();
                processingPreviewTimer.Start();
            }
            else processingPreviewTimer.Stop();
        }

        private void UpdateProcessingPreviewPosition(object? sender, object e)
        {
            if (!pageActive || !viewModel.DuringOperation || PreviewWhileProcessingCheckBox.IsChecked != true) return;
            var session = VideoPlayer.MediaPlayer.PlaybackSession;
            if (session.CanSeek && processingPreview.TryTake(session.Position, session.NaturalDuration, out var position))
                session.Position = position;
        }

        private void PreviewSeekCompleted(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        {
            processingPreview.CompleteSeek();
        }

        private void LoadVideo()
        {
            resizeTimer.Stop();
            videoSizeChanged = false;
            processingPreview.Reset();
            processingPreview.CompleteSeek();
            App.Diagnostics.Log($"Loading video {currentVideoIndex + 1}/{videoPaths.Count}: {videoPath}");
            VideoName.Text = Path.GetFileName(videoPath);
            cropFrameInitialized = false;
            UpdateVideoRotation();
            RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = ResetRotationButton.IsEnabled = false;
            VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(Processor.GetSafePath(videoPath)));
            UpdateVideoNavigation();
        }

        private void UpdateVideoNavigation()
        {
            RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = ResetRotationButton.IsEnabled = cropFrameInitialized && !viewModel.DuringOperation;
            VideoPosition.Text = $"{currentVideoIndex + 1}/{videoPaths.Count}";
            VideoNavigation.Visibility = videoPaths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            PreviousVideoButton.IsEnabled = !viewModel.DuringOperation && currentVideoIndex > 0;
            NextVideoButton.IsEnabled = !viewModel.DuringOperation && currentVideoIndex < videoPaths.Count - 1;
        }

        private void PreviousVideo(object sender, RoutedEventArgs e)
        {
            currentVideoIndex--;
            LoadVideo();
        }

        private void NextVideo(object sender, RoutedEventArgs e)
        {
            currentVideoIndex++;
            LoadVideo();
        }

        private void UpdateUiWithCoordinates(Rect newRect)
        {
            X.Text = (newRect.X / Canvas.Width * 100).ToString("F2");
            Y.Text = (newRect.Y / Canvas.Height * 100).ToString("F2");
            XDelta.Text = newRect.Width.ToString("F0");
            YDelta.Text = newRect.Height.ToString("F0");
            previousRect.XText = X.Text;
            previousRect.YText = Y.Text;
            previousRect.X2Text = XDelta.Text;
            previousRect.Y2Text = YDelta.Text;
        }

        private void PopulateAspectRatios()
        {
            ratios.Add(new AspectRatio { Title = "Square", Width = IconMaxSize, Height = IconMaxSize });
            ratios.Add(GetAspectRatio(16, 9));
            ratios.Add(GetAspectRatio(9, 16));
            ratios.Add(GetAspectRatio(5, 4));
            ratios.Add(GetAspectRatio(4, 5));
            ratios.Add(GetAspectRatio(4, 3));
            ratios.Add(GetAspectRatio(3, 4));
            ratios.Add(GetAspectRatio(3, 2));
            ratios.Add(GetAspectRatio(2, 3));
            ratios.Add(GetAspectRatio(2, 1));
            ratios.Add(GetAspectRatio(1, 2));
        }

        private static AspectRatio GetAspectRatio(double aspectWidth, double aspectHeight)
        {
            double width, height;
            if (aspectWidth > aspectHeight)
            {
                width = IconMaxSize;
                height = IconMaxSize * aspectHeight / aspectWidth;
            }
            else
            {
                height = IconMaxSize;
                width = IconMaxSize * aspectWidth / aspectHeight;
            }

            return new AspectRatio { Title = $"{aspectWidth}:{aspectHeight}", Width = width, Height = height };
        }

        private void CoordinatesChanged(Rect newRect)
        {
            if (restoringCrop) return;
            mask.Rect = newRect;
            UpdateUiWithCoordinates(newRect);
            savedXPercent = newRect.X / Canvas.Width * 100;
            savedYPercent = newRect.Y / Canvas.Height * 100;
            savedWidthPercent = newRect.Width / Canvas.Width * 100;
            savedHeightPercent = newRect.Height / Canvas.Height * 100;

            if (selectedAspectRatio != null &&
                Math.Abs(newRect.Width / newRect.Height - selectedAspectRatio.Width / selectedAspectRatio.Height) > 0.001)
            {
                selectedAspectRatio.IsSelected = false;
                selectedAspectRatio = null;
                savedAspectRatioTitle = null;
            }

            if (centerPresetSelected &&
                (Math.Abs(newRect.X - (Canvas.Width - newRect.Width) / 2) > 0.5 ||
                 Math.Abs(newRect.Y - (Canvas.Height - newRect.Height) / 2) > 0.5))
            {
                centerPresetSelected = false;
                CenterFrameToggleButton.IsChecked = false;
            }
            App.Settings.SetCropState(savedXPercent.Value, savedYPercent.Value,
                savedWidthPercent.Value, savedHeightPercent.Value, quarterTurns,
                savedAspectRatioTitle, centerPresetSelected);
        }

        private void CoordinatesChanged()
        {
            CoordinatesChanged(new Rect(resizer.GetElementLeft(CropFrame), resizer.GetElementTop(CropFrame), CropFrame.Width, CropFrame.Height));
        }

        private void AspectRatioCheckBox_OnChanged(object sender, RoutedEventArgs? e)
        {
            var checkbox = (CheckBox)sender;
            VideoCropper.App.Settings[LockAspectRatioSetting] = checkbox.IsChecked == true;
            if (!cropFrameInitialized) return;
            resizer.SetNewHandlingParameters(CropFrame, GetAspectRatioParam(checkbox.IsChecked == true));
        }

        private void SpecificRatio(object sender, RoutedEventArgs e)
        {
            var ratio = (AspectRatio)((ToggleButton)sender).DataContext;
            if (selectedAspectRatio != null) selectedAspectRatio.IsSelected = false;
            selectedAspectRatio = ratio;
            ratio.IsSelected = true;
            savedAspectRatioTitle = ratio.Title;
            VideoCropper.App.Settings[AspectRatioPresetSetting] = ratio.Title;
            resizer.SetAspectRatio(CropFrame, ratio.Width / ratio.Height);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void CenterFrame(object sender, RoutedEventArgs e)
        {
            centerPresetSelected = CenterFrameToggleButton.IsChecked == true;
            if (centerPresetSelected) resizer.PositionElementAtCenter(CropFrame);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void VideoPlayer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width == 0 || e.NewSize.Height == 0) return;
            if (cropFrameInitialized)
            {
                videoSizeChanged = true;
                ScheduleResize();
                return;
            }
            UpdateVideoRotation();
            InitializeCropFrame(quarterTurns % 2 == 0 ? e.NewSize : new Size(e.NewSize.Height, e.NewSize.Width));
        }

        private void RotateLeft(object sender, RoutedEventArgs e) => RotateVideo(-1);
        private void RotateRight(object sender, RoutedEventArgs e) => RotateVideo(1);

        private void ResetRotation(object sender, RoutedEventArgs e)
        {
            while (quarterTurns != 0)
                RotateVideo(quarterTurns == 3 ? 1 : -1);
        }

        private void UpdateVideoRotation()
        {
            var width = VideoPlayer.ActualWidth;
            var height = VideoPlayer.ActualHeight;
            var matrix = quarterTurns switch
            {
                1 => new Matrix(0, 1, -1, 0, height, 0),
                2 => new Matrix(-1, 0, 0, -1, width, height),
                3 => new Matrix(0, -1, 1, 0, 0, width),
                _ => new Matrix(1, 0, 0, 1, 0, 0)
            };
            VideoPlayer.RenderTransform = new MatrixTransform { Matrix = matrix };
        }

        private void RotateVideo(int direction)
        {
            ApplyPendingResize();
            var left = resizer.GetElementLeft(CropFrame);
            var top = resizer.GetElementTop(CropFrame);
            var rotated = direction > 0
                ? new Rect(Canvas.Height - top - CropFrame.Height, left, CropFrame.Height, CropFrame.Width)
                : new Rect(top, Canvas.Width - left - CropFrame.Width, CropFrame.Height, CropFrame.Width);
            var newSize = new Size(Canvas.Height, Canvas.Width);
            var preset = selectedAspectRatio;
            var rotatedPreset = preset == null ? null : preset.Title == "Original" ? "Original"
                : ratios.First(ratio => Math.Abs(ratio.Width / ratio.Height - preset.Height / preset.Width) < 0.001).Title;
            quarterTurns = (quarterTurns + direction + 4) % 4;
            savedAspectRatioTitle = rotatedPreset;
            UpdateVideoRotation();
            InitializeCropFrame(newSize, rotated);
            App.Diagnostics.Log($"Video rotation: {quarterTurns * 90} degrees clockwise");
        }

        private void InitializeCropFrame(Size videoSize, Rect? rotatedCrop = null)
        {
            restoringCrop = true;
            resizer.DeInitDraggerResizer(CropFrame);
            Canvas.Width = CropFrame.Width = OverlayAndMask.Width = videoSize.Width;
            Canvas.Height = CropFrame.Height = OverlayAndMask.Height = videoSize.Height;
            if (rotatedCrop is Rect crop)
            {
                CropFrame.Width = crop.Width;
                CropFrame.Height = crop.Height;
            }
            else if (savedWidthPercent != null)
            {
                CropFrame.Width = savedWidthPercent.Value / 100 * videoSize.Width;
                CropFrame.Height = savedHeightPercent!.Value / 100 * videoSize.Height;
            }
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CropFrame, 0);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CropFrame, 0);
            var geometryGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
            var mask = new RectangleGeometry
            {
                Rect = new Rect(0, 0, videoSize.Width, videoSize.Height)
            };
            geometryGroup.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(0, 0, videoSize.Width, videoSize.Height)
            });
            geometryGroup.Children.Add(mask);
            OverlayAndMask.Data = geometryGroup;
            var orientations = Enum.GetValues<Orientation>().Append(Orientation.Horizontal | Orientation.Vertical)
                .ToDictionary(o => o, o => new Appearance { HandleThickness = 30 });
            CropFrame.UpdateLayout();
            resizer.InitDraggerResizer(CropFrame, orientations, callbacks: new HandlingCallbacks
            {
                BeforeDragging = point => new Point(point.X / ZoomTransform.ScaleX, point.Y / ZoomTransform.ScaleY),
                AfterDragging = CoordinatesChanged,
                DragCompleted = FocusCropFrame,
                BeforeResizing = (point, _) => new Point(point.X / ZoomTransform.ScaleX, point.Y / ZoomTransform.ScaleY),
                AfterResizing = (newRect, _) => CoordinatesChanged(newRect),
                ResizeCompleted = _ => FocusCropFrame()
            });
            CropFrame.UpdateLayout();

            var originalAspectRatio = GetAspectRatio(videoSize.Width, videoSize.Height);
            originalAspectRatio.Title = "Original";
            if (ratios[0].Title == originalAspectRatio.Title) ratios.RemoveAt(0);
            ratios.Insert(0, originalAspectRatio);

            cropFrameInitialized = true;
            AspectRatioCheckBox_OnChanged(AspectRatioCheckBox, null);
            if (savedAspectRatioTitle != null)
            {
                if (selectedAspectRatio != null) selectedAspectRatio.IsSelected = false;
                selectedAspectRatio = ratios.First(ratio => ratio.Title == savedAspectRatioTitle);
                selectedAspectRatio.IsSelected = true;
                if (rotatedCrop == null)
                    resizer.SetAspectRatio(CropFrame, selectedAspectRatio.Width / selectedAspectRatio.Height);
            }
            if (rotatedCrop == null && savedXPercent != null)
                resizer.PositionElementLeft(CropFrame, savedXPercent.Value / 100 * OverlayAndMask.Width);
            if (rotatedCrop == null && savedYPercent != null)
                resizer.PositionElementTop(CropFrame, savedYPercent.Value / 100 * OverlayAndMask.Height);
            if (rotatedCrop is Rect rotated)
                resizer.PositionElement(CropFrame, rotated.X, rotated.Y);
            if (centerPresetSelected)
            {
                CenterFrameToggleButton.IsChecked = true;
                resizer.PositionElementAtCenter(CropFrame);
            }
            restoringCrop = false;
            CoordinatesChanged();

            RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = ResetRotationButton.IsEnabled = !viewModel.DuringOperation;
            if (zoomIntoFrame) FocusCropFrame();
            else FitToView();
        }

        private void FocusCropFrame()
        {
            if (!zoomIntoFrame) return;
            const int margin = 100;
            var left = resizer.GetElementLeft(CropFrame);
            var top = resizer.GetElementTop(CropFrame);
            double panX, panY, zoom;
            if (CanvasContainer.ActualWidth / CanvasContainer.ActualHeight < CropFrame.Width / CropFrame.Height)
            {
                zoom = CanvasContainer.ActualWidth / Math.Min(CropFrame.Width + margin * 2, Canvas.Width);
                panX = -(left * zoom - (CanvasContainer.ActualWidth - CropFrame.Width * zoom) / 2);
                panY = -(top * zoom - (CanvasContainer.ActualHeight - CropFrame.Height * zoom) / 2);
            }
            else
            {
                zoom = CanvasContainer.ActualHeight / Math.Min(CropFrame.Height + margin * 2, Canvas.Height);
                panY = -(top * zoom - (CanvasContainer.ActualHeight - CropFrame.Height * zoom) / 2);
                panX = -(left * zoom - (CanvasContainer.ActualWidth - CropFrame.Width * zoom) / 2);
            }
            AnimateTransform(panX, panY, zoom);
        }

        private void FitToView()
        {
            double panX, panY, zoom;
            if (CanvasContainer.ActualWidth / CanvasContainer.ActualHeight < Canvas.Width / Canvas.Height)
            {
                zoom = CanvasContainer.ActualWidth / Canvas.Width; // Fit to width
                var scaledHeight = zoom * Canvas.Height;
                panY = (CanvasContainer.ActualHeight - scaledHeight) / 2; // Center vertically
                panX = 0;
            }
            else
            {
                zoom = CanvasContainer.ActualHeight / Canvas.Height; // Fit to height
                var scaledWidth = zoom * Canvas.Width;
                panX = (CanvasContainer.ActualWidth - scaledWidth) / 2; // Center horizontally
                panY = 0;
            }
            AnimateTransform(panX, panY, zoom);
        }

        private static HandlingParameters GetAspectRatioParam(bool lockedAspectRatio) => new() { KeepAspectRatio = lockedAspectRatio };

        private void AnimateTransform(double panX, double panY, double zoom)
        {
            transformStoryboard?.Stop();
            if (applyingResize)
            {
                PanTransform.X = panX;
                PanTransform.Y = panY;
                ZoomTransform.ScaleX = ZoomTransform.ScaleY = zoom;
                return;
            }
            transformStoryboard = new Storyboard(); //Animates PanTransform.X/Y and ZoomTransform.ScaleX/Y
            const double animDuration = 500;

            var animPanX = new DoubleAnimation
            {
                To = panX,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animPanX, PanTransform);
            Storyboard.SetTargetProperty(animPanX, "X");
            transformStoryboard.Children.Add(animPanX);

            var animPanY = new DoubleAnimation
            {
                To = panY,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animPanY, PanTransform);
            Storyboard.SetTargetProperty(animPanY, "Y");
            transformStoryboard.Children.Add(animPanY);

            var animZoomX = new DoubleAnimation
            {
                To = zoom,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animZoomX, ZoomTransform);
            Storyboard.SetTargetProperty(animZoomX, "ScaleX");
            transformStoryboard.Children.Add(animZoomX);

            var animZoomY = new DoubleAnimation
            {
                To = zoom,
                Duration = new Duration(TimeSpan.FromMilliseconds(animDuration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animZoomY, ZoomTransform);
            Storyboard.SetTargetProperty(animZoomY, "ScaleY");
            transformStoryboard.Children.Add(animZoomY);

            transformStoryboard.Begin();
        }

        private void X_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.XText == X.Text) return;
            resizer.PositionElementLeft(CropFrame, double.Parse(X.Text) / 100 * OverlayAndMask.Width);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void XDelta_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.X2Text == XDelta.Text) return;
            resizer.ResizeElementWidth(CropFrame, double.Parse(XDelta.Text) / Canvas.Width * OverlayAndMask.Width,
                parameters: new HandlingParameters{ KeepAspectRatio = AspectRatioCheckBox.IsChecked == true });
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void Y_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.YText == Y.Text) return;
            resizer.PositionElementTop(CropFrame, double.Parse(Y.Text) / 100 * OverlayAndMask.Height);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void YDelta_OnTextChanged(object sender, RoutedEventArgs e)
        {
            if(previousRect.Y2Text == YDelta.Text) return;
            resizer.ResizeElementHeight(CropFrame, double.Parse(YDelta.Text) / Canvas.Height * OverlayAndMask.Height,
                parameters: new HandlingParameters { KeepAspectRatio = AspectRatioCheckBox.IsChecked == true });
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void CanvasContainer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            CanvasContainer.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, CanvasContainer.ActualWidth, CanvasContainer.ActualHeight)
            };
            if (!cropFrameInitialized) return;
            ScheduleResize();
        }

        private void ScheduleResize()
        {
            transformStoryboard?.Stop();
            resizeTimer.Stop();
            resizeTimer.Start();
        }

        private void ApplyPendingResize()
        {
            var pending = resizeTimer.IsEnabled;
            resizeTimer.Stop();
            if (!pending || !pageActive || !cropFrameInitialized) return;
            if (CanvasContainer.ActualWidth == 0 || CanvasContainer.ActualHeight == 0 ||
                VideoPlayer.ActualWidth == 0 || VideoPlayer.ActualHeight == 0) return;
            applyingResize = true;
            if (videoSizeChanged)
            {
                videoSizeChanged = false;
                UpdateVideoRotation();
                InitializeCropFrame(quarterTurns % 2 == 0
                    ? new Size(VideoPlayer.ActualWidth, VideoPlayer.ActualHeight)
                    : new Size(VideoPlayer.ActualHeight, VideoPlayer.ActualWidth));
            }
            else if (zoomIntoFrame) FocusCropFrame();
            else FitToView();
            applyingResize = false;
        }

        private void CanvasContainer_OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (MouseWheelZoomCheckBox.IsChecked != true) return;

            var pointerPoint = e.GetCurrentPoint(CanvasContainer);
            var zoomFactor = pointerPoint.Properties.MouseWheelDelta > 0 ? 1.1 : 1 / 1.1;
            var panX = pointerPoint.Position.X - (pointerPoint.Position.X - PanTransform.X) * zoomFactor;
            var panY = pointerPoint.Position.Y - (pointerPoint.Position.Y - PanTransform.Y) * zoomFactor;
            var zoom = ZoomTransform.ScaleX * zoomFactor;

            transformStoryboard?.Stop();
            PanTransform.X = panX;
            PanTransform.Y = panY;
            ZoomTransform.ScaleX = zoom;
            ZoomTransform.ScaleY = zoom;
            e.Handled = true;
        }

        private void ZoomFrameCheckBox_OnChanged(object sender, RoutedEventArgs e)
        {
            var checkbox = (CheckBox)sender;
            VideoCropper.App.Settings[ZoomIntoFrameSetting] = checkbox.IsChecked == true;
            zoomIntoFrame = checkbox.IsChecked == true;
            if (!cropFrameInitialized) return;
            if (checkbox.IsChecked == true)
            {
                FocusCropFrame();
            }
            else
            {
                FitToView();
            }
        }

        private void MouseWheelZoomCheckBox_OnChanged(object sender, RoutedEventArgs e)
        {
            VideoCropper.App.Settings[MouseWheelZoomSetting] = MouseWheelZoomCheckBox.IsChecked == true;
        }

        private void NormalizePixelAspectRatioCheckBox_OnChanged(object sender, RoutedEventArgs? e)
        {
            VideoCropper.App.Settings[NormalizePixelAspectRatioSetting] =
                NormalizePixelAspectRatioCheckBox.IsChecked == true;
        }

        private async void Crop(object sender, RoutedEventArgs e)
        {
            ApplyPendingResize();
            processingPreview.Reset();
            App.Diagnostics.Log($"Crop requested: input={videoPath}; X={X.Text}; Y={Y.Text}; width={XDelta.Text}; height={YDelta.Text}");
            outputFile = null;
            outputFile = await ProcessManager.StartProcess(CropVideo());
            if (!pageActive || !viewModel.AfterOperation || outputFile == null || cropProcessor.StopRequested) return;
            if (NotifyWhenDoneCheckBox.IsChecked == true && cropProcessor.Succeeded)
                App.NotifyCropCompleted(outputFile);
            if (AutoAdvanceCheckBox.IsChecked == true && currentVideoIndex < videoPaths.Count - 1)
            {
                currentVideoIndex++;
                LoadVideo();
            }
            else if (QuitWhenDoneCheckBox.IsChecked == true)
                await ConfirmQuitWhenDone();
        }

        private async Task ConfirmQuitWhenDone()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Video Cropper is quitting",
                Content = "Processing is complete. Quitting in 30 seconds.",
                PrimaryButtonText = "Quit now",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            quitDialog = dialog;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            var elapsed = new Stopwatch();
            var expired = false;
            timer.Tick += (_, _) =>
            {
                var seconds = Math.Max(0, 30 - (int)elapsed.Elapsed.TotalSeconds);
                dialog.Content = $"Processing is complete. Quitting in {seconds} seconds.";
                if (seconds == 0)
                {
                    expired = true;
                    timer.Stop();
                    dialog.Hide();
                }
            };
            dialog.Opened += (_, _) =>
            {
                elapsed.Start();
                timer.Start();
            };
            dialog.Closing += (_, _) => timer.Stop();
            var result = await dialog.ShowAsync();
            quitDialog = null;
            if (pageActive && (expired || result == ContentDialogResult.Primary))
            {
                App.Diagnostics.Log("Quitting after processing completed");
                MainWindow.Window.Close();
            }
        }

        private async Task CropVideo()
        {
            var playbackSession = VideoPlayer.MediaPlayer.PlaybackSession;
            var naturalWidth = quarterTurns % 2 == 0 ? playbackSession.NaturalVideoWidth : playbackSession.NaturalVideoHeight;
            var naturalHeight = quarterTurns % 2 == 0 ? playbackSession.NaturalVideoHeight : playbackSession.NaturalVideoWidth;
            var videoScale = Math.Min(Canvas.Width / naturalWidth, Canvas.Height / naturalHeight);
            var videoWidth = naturalWidth * videoScale;
            var videoHeight = naturalHeight * videoScale;
            var videoLeft = (Canvas.Width - videoWidth) / 2;
            var videoTop = (Canvas.Height - videoHeight) / 2;
            var x = $"iw*{((double.Parse(X.Text) / 100 * Canvas.Width - videoLeft) / videoWidth).ToString(CultureInfo.InvariantCulture)}";
            var y = $"ih*{((double.Parse(Y.Text) / 100 * Canvas.Height - videoTop) / videoHeight).ToString(CultureInfo.InvariantCulture)}";
            var width = $"iw*{(double.Parse(XDelta.Text) / videoWidth).ToString(CultureInfo.InvariantCulture)}";
            var height = $"ih*{(double.Parse(YDelta.Text) / videoHeight).ToString(CultureInfo.InvariantCulture)}";
            await cropProcessor.Crop(videoPath, ffmpegPath, x, y, width, height,
                NormalizePixelAspectRatioCheckBox.IsChecked == true, quarterTurns,
                VideoControls.InPosition, VideoControls.OutPosition);
        }

        private void GoBack(object sender, RoutedEventArgs e)
        {
            processingPreviewTimer.Stop();
            App.Diagnostics.Log("Leaving crop page");
            VideoPlayer.MediaPlayer.Pause();
            _ = cropProcessor.Cancel();
            if (navigateTo == null) Frame.GoBack();
            else Frame.NavigateToType(Type.GetType(navigateTo), outputFile, new FrameNavigationOptions { IsNavigationStackEnabled = false });
        }
    }

    class AspectRatio : INotifyPropertyChanged
    {
        private bool isSelected;
        public required string Title { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class CropperProps{
        public required string FfmpegPath { get; set; }
        public required List<string> VideoPaths { get; set; }
        public string? TypeToNavigateTo { get; set; }
    }
}
