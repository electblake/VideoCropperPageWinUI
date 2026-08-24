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
        private CropProcessor cropProcessor;
        private RectangleGeometry mask;
        private const string AspectRatioPresetSetting = "Crop.AspectRatioPreset";
        private const string CenterPresetSetting = "Crop.CenterPreset";
        private const string LockAspectRatioSetting = "Crop.LockAspectRatio";
        private const string ZoomIntoFrameSetting = "Crop.ZoomIntoFrame";
        private const string MouseWheelZoomSetting = "Crop.MouseWheelZoom";
        private const string NormalizePixelAspectRatioSetting = "Crop.NormalizePixelAspectRatio";
        private bool progressChangedByCode;
        private readonly ObservableCollection<AspectRatio> ratios = [];
        private const double IconMaxSize = 40;
        private readonly double progressMax = 100;
        private string? outputFile;
        private string? navigateTo;
        private string ffmpegPath, videoPath;
        private readonly List<string> videoPaths = [];
        private int currentVideoIndex;
        private CancellationTokenSource resizeTokenSource;
        private bool cropFrameInitialized;
        private bool centerPresetSelected;
        private bool zoomIntoFrame;
        private string? savedAspectRatioTitle;
        private AspectRatio? selectedAspectRatio;
        private Storyboard? transformStoryboard;
        private (string XText, string YText, string X2Text, string Y2Text) previousRect;

        public VideoCropperPage()
        {
            InitializeComponent();
            resizer = new DraggerResizer.DraggerResizer();
            viewModel = new CropperModel();
            PopulateAspectRatios();
            var settings = Windows.Storage.ApplicationData.Current.RoamingSettings.Values;
            if (settings.ContainsKey(AspectRatioPresetSetting))
                savedAspectRatioTitle = (string)settings[AspectRatioPresetSetting];
            if (settings.ContainsKey(CenterPresetSetting))
                centerPresetSelected = (bool)settings[CenterPresetSetting];
            if (settings.ContainsKey(LockAspectRatioSetting))
                AspectRatioCheckBox.IsChecked = (bool)settings[LockAspectRatioSetting];
            if (settings.ContainsKey(ZoomIntoFrameSetting))
                ZoomIntoFrameCheckBox.IsChecked = (bool)settings[ZoomIntoFrameSetting];
            if (settings.ContainsKey(MouseWheelZoomSetting))
                MouseWheelZoomCheckBox.IsChecked = (bool)settings[MouseWheelZoomSetting];
            if (settings.ContainsKey(NormalizePixelAspectRatioSetting))
                NormalizePixelAspectRatioCheckBox.IsChecked = (bool)settings[NormalizePixelAspectRatioSetting];
            viewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(CropperModel.BeforeOperation))
                {
                    if (zoomIntoFrame) FocusCropFrame();
                    else FitToView();
                }
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var props = (CropperProps)e.Parameter;
            ffmpegPath = props.FfmpegPath;
            cropProcessor = new CropProcessor(ffmpegPath);
            videoPaths.AddRange(props.VideoPaths);
            navigateTo = props.TypeToNavigateTo;
            base.OnNavigatedTo(e);
            LoadVideo();
        }

        private void LoadVideo()
        {
            videoPath = videoPaths[currentVideoIndex];
            VideoName.Text = Path.GetFileName(videoPath);
            cropFrameInitialized = false;
            VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(Processor.GetSafePath(videoPath)));
            UpdateVideoNavigation();
        }

        private void UpdateVideoNavigation()
        {
            VideoNavigation.Visibility = videoPaths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            PreviousVideoButton.IsEnabled = currentVideoIndex > 0;
            NextVideoButton.IsEnabled = currentVideoIndex < videoPaths.Count - 1;
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
            X.Text = newRect.X.ToString("F0");
            Y.Text = newRect.Y.ToString("F0");
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
            mask.Rect = newRect;
            UpdateUiWithCoordinates(newRect);

            if (selectedAspectRatio != null &&
                Math.Abs(newRect.Width / newRect.Height - selectedAspectRatio.Width / selectedAspectRatio.Height) > 0.001)
            {
                selectedAspectRatio.IsSelected = false;
                selectedAspectRatio = null;
                savedAspectRatioTitle = null;
                Windows.Storage.ApplicationData.Current.RoamingSettings.Values.Remove(AspectRatioPresetSetting);
            }

            if (centerPresetSelected &&
                (Math.Abs(newRect.X - (Canvas.Width - newRect.Width) / 2) > 0.5 ||
                 Math.Abs(newRect.Y - (Canvas.Height - newRect.Height) / 2) > 0.5))
            {
                centerPresetSelected = false;
                CenterFrameToggleButton.IsChecked = false;
                Windows.Storage.ApplicationData.Current.RoamingSettings.Values[CenterPresetSetting] = false;
            }
        }

        private void CoordinatesChanged()
        {
            CoordinatesChanged(new Rect(resizer.GetElementLeft(CropFrame), resizer.GetElementTop(CropFrame), CropFrame.Width, CropFrame.Height));
        }

        private void AspectRatioCheckBox_OnChanged(object sender, RoutedEventArgs e)
        {
            var checkbox = (CheckBox)sender;
            Windows.Storage.ApplicationData.Current.RoamingSettings.Values[LockAspectRatioSetting] = checkbox.IsChecked == true;
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
            Windows.Storage.ApplicationData.Current.RoamingSettings.Values[AspectRatioPresetSetting] = ratio.Title;
            resizer.SetAspectRatio(CropFrame, ratio.Width / ratio.Height);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void CenterFrame(object sender, RoutedEventArgs e)
        {
            centerPresetSelected = true;
            CenterFrameToggleButton.IsChecked = true;
            Windows.Storage.ApplicationData.Current.RoamingSettings.Values[CenterPresetSetting] = true;
            resizer.PositionElementAtCenter(CropFrame);
            CoordinatesChanged();
            FocusCropFrame();
        }

        private void VideoPlayer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InitializeCropFrame(e.NewSize);
        }

        private void InitializeCropFrame(Size videoSize)
        {
            Canvas.Width = CropFrame.Width = OverlayAndMask.Width = videoSize.Width;
            Canvas.Height = CropFrame.Height = OverlayAndMask.Height = videoSize.Height;
            var geometryGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
            mask = new RectangleGeometry
            {
                Rect = new Rect(0, 0, videoSize.Width, videoSize.Height)
            };
            geometryGroup.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(0, 0, videoSize.Width, videoSize.Height)
            });
            geometryGroup.Children.Add(mask);
            OverlayAndMask.Data = geometryGroup;
            resizer.DeInitDraggerResizer(CropFrame);
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
                resizer.SetAspectRatio(CropFrame, selectedAspectRatio.Width / selectedAspectRatio.Height);
            }
            if (centerPresetSelected)
            {
                CenterFrameToggleButton.IsChecked = true;
                resizer.PositionElementAtCenter(CropFrame);
            }
            CoordinatesChanged();

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
            resizer.PositionElementLeft(CropFrame, double.Parse(X.Text) / Canvas.Width * OverlayAndMask.Width);
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
            resizer.PositionElementTop(CropFrame, double.Parse(Y.Text) / Canvas.Height * OverlayAndMask.Height);
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
            if(zoomIntoFrame) FocusCropFrame();
            else FitToView();
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
            Windows.Storage.ApplicationData.Current.RoamingSettings.Values[ZoomIntoFrameSetting] = checkbox.IsChecked == true;
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
            Windows.Storage.ApplicationData.Current.RoamingSettings.Values[MouseWheelZoomSetting] = MouseWheelZoomCheckBox.IsChecked == true;
        }

        private void NormalizePixelAspectRatioCheckBox_OnChanged(object sender, RoutedEventArgs e)
        {
            Windows.Storage.ApplicationData.Current.RoamingSettings.Values[NormalizePixelAspectRatioSetting] =
                NormalizePixelAspectRatioCheckBox.IsChecked == true;
        }

        private async void Crop(object sender, RoutedEventArgs e)
        {
            outputFile = null;
            outputFile = await ProcessManager.StartProcess(CropVideo());
        }

        private async void BatchCrop(object sender, RoutedEventArgs e)
        {
            outputFile = null;
            while (videoPaths.Count > 0)
            {
                outputFile = await ProcessManager.StartProcess(CropVideo());
                videoPaths.RemoveAt(currentVideoIndex);
                if (videoPaths.Count == 0)
                {
                    UpdateVideoNavigation();
                    return;
                }
                if (currentVideoIndex == videoPaths.Count) currentVideoIndex = 0;
                LoadVideo();
            }
        }

        private async Task CropVideo()
        {
            var playbackSession = VideoPlayer.MediaPlayer.PlaybackSession;
            var videoScale = Math.Min(Canvas.Width / playbackSession.NaturalVideoWidth,
                Canvas.Height / playbackSession.NaturalVideoHeight);
            var videoWidth = playbackSession.NaturalVideoWidth * videoScale;
            var videoHeight = playbackSession.NaturalVideoHeight * videoScale;
            var videoLeft = (Canvas.Width - videoWidth) / 2;
            var videoTop = (Canvas.Height - videoHeight) / 2;
            var x = $"iw*{((double.Parse(X.Text) - videoLeft) / videoWidth).ToString(CultureInfo.InvariantCulture)}";
            var y = $"ih*{((double.Parse(Y.Text) - videoTop) / videoHeight).ToString(CultureInfo.InvariantCulture)}";
            var width = $"iw*{(double.Parse(XDelta.Text) / videoWidth).ToString(CultureInfo.InvariantCulture)}";
            var height = $"ih*{(double.Parse(YDelta.Text) / videoHeight).ToString(CultureInfo.InvariantCulture)}";
            await cropProcessor.Crop(videoPath, ffmpegPath, x, y, width, height,
                NormalizePixelAspectRatioCheckBox.IsChecked == true);
        }

        private void GoBack(object sender, RoutedEventArgs e)
        {
            VideoPlayer.MediaPlayer.Pause();
            _ = cropProcessor.Cancel();
            if (navigateTo == null) Frame.GoBack();
            else Frame.NavigateToType(Type.GetType(navigateTo), outputFile, new FrameNavigationOptions { IsNavigationStackEnabled = false });
        }
    }

    class AspectRatio : INotifyPropertyChanged
    {
        private bool isSelected;
        public string Title { get; set; }
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
        public string FfmpegPath { get; set; }
        public List<string> VideoPaths { get; set; }
        public string? TypeToNavigateTo { get; set; }
    }
}
