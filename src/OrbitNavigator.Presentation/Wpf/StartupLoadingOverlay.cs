#if ORBIT_WPF
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

using OrbitNavigator.Presentation.Shell;

using IOPath = System.IO.Path;

namespace OrbitNavigator.Presentation.Wpf;

/// <summary>
/// Native startup/loading overlay driven by a narrow state API. Foundation
/// supplies packaged artwork, calls <see cref="SetState"/> as phases advance,
/// and calls <see cref="Complete"/> when InitialHostReady is committed.
/// </summary>
public sealed class StartupLoadingOverlay : Grid
{
    public const string CoordinatorAssetRelativePath = "assets/loading/orbit-launch-field-v1.png";
    public const string CoordinatorSequenceRelativeDirectory = "assets/loading/sequence-v1";
    public const int TimelineFramesPerSecond = 30;

    public static readonly TimeSpan TimelineDuration = TimeSpan.FromSeconds(4);

    private const int TimelineFrameCount = TimelineFramesPerSecond * 4;

    private static readonly string[] coordinatorSequenceFileNames =
    [
        "frame-00-dormant.png",
        "frame-01-awaken.png",
        "frame-02-sweep.png",
        "frame-03-converge.png",
        "frame-04-apex.png",
        "frame-05-settle.png",
    ];

    private readonly Image backgroundImage = CreateLaunchImage(0.9);
    private readonly Grid sequenceLayer = new() { IsHitTestVisible = false, Focusable = false };
    private readonly Image frameA = CreateLaunchImage(0.94);
    private readonly Image frameB = CreateLaunchImage(0);
    private readonly List<ImageSource> sequenceFrames = [];
    private readonly List<Ellipse> waypointLights = [];
    private readonly Viewbox orbitalField = new()
    {
        Stretch = Stretch.Uniform,
        Opacity = 0.42,
        IsHitTestVisible = false,
        Focusable = false,
    };
    private readonly Border card = OrbitVisualTheme.CreateSurface(16);
    private readonly OrbitIcon mark = new()
    {
        Kind = OrbitIconKind.OrbitMark,
        Width = 72,
        Height = 72,
        Stroke = OrbitVisualTheme.SeaGlass,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly TextBlock phaseText = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Text = "STARTING",
    };
    private readonly TextBlock statusText = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 15,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly ProgressBar progress = new()
    {
        Height = 6,
        Minimum = 0,
        Maximum = 100,
        Margin = new Thickness(0, 20, 0, 0),
    };
    private readonly Button retryButton = new()
    {
        Content = "Try again",
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 18, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly RotateTransform outerOrbitRotation = new();
    private readonly RotateTransform innerOrbitRotation = new();
    private readonly RotateTransform markRotation = new();
    private readonly DispatcherTimer sequenceTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1d / TimelineFramesPerSecond),
    };

    private string? backgroundAssetPath;
    private bool reducedMotion;
    private bool systemParameterEventsAttached;
    private int currentSequenceFrameIndex = -1;
    private int timelineFrame;
    private StartupLoadingState state = StartupLoadingState.Initial();

    public StartupLoadingOverlay()
    {
        ClipToBounds = true;
        Background = OrbitVisualTheme.Canvas;
        AutomationProperties.SetName(this, "Orbit Navigator startup");
        sequenceTimer.Tick += OnSequenceTick;
        BuildLayout();
        SetState(state);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static IReadOnlyList<string> CoordinatorSequenceFileNames { get; } =
        Array.AsReadOnly(coordinatorSequenceFileNames);

    public event EventHandler? Cleared;

    public event EventHandler? RetryRequested;

    public StartupLoadingState State => state;

    public int LoadedSequenceFrameCount => sequenceFrames.Count;

    public int CurrentSequenceFrameIndex => currentSequenceFrameIndex;

    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            if (reducedMotion == value)
            {
                return;
            }

            reducedMotion = value;
            RefreshVisualAssets();
            UpdateAnimations();
        }
    }

    /// <summary>
    /// Loads the four coordinator-owned sequence frames by their frozen names.
    /// Returns false without disturbing a previously loaded sequence if any
    /// frame is unavailable or invalid.
    /// </summary>
    public bool SetSequenceAssetDirectory(string absoluteDirectory)
    {
        if (string.IsNullOrWhiteSpace(absoluteDirectory) || !IOPath.IsPathFullyQualified(absoluteDirectory))
        {
            return false;
        }

        var loadedFrames = new List<ImageSource>(coordinatorSequenceFileNames.Length);
        foreach (var fileName in coordinatorSequenceFileNames)
        {
            var source = TryLoadBitmap(IOPath.Combine(absoluteDirectory, fileName));
            if (source is null)
            {
                return false;
            }

            loadedFrames.Add(source);
        }

        sequenceTimer.Stop();
        sequenceFrames.Clear();
        sequenceFrames.AddRange(loadedFrames);
        currentSequenceFrameIndex = -1;
        timelineFrame = 0;
        RefreshVisualAssets();
        UpdateAnimations();
        return true;
    }

    /// <summary>
    /// Sets the single-image fallback used only when the generated sequence is
    /// not available. Foundation should prefer <see cref="SetSequenceAssetDirectory"/>.
    /// </summary>
    public void SetBackgroundAsset(string? absolutePath)
    {
        backgroundAssetPath = absolutePath;
        RefreshVisualAssets();
    }

    public void SetState(StartupLoadingState nextState)
    {
        state = nextState ?? throw new ArgumentNullException(nameof(nextState));
        BeginAnimation(OpacityProperty, null);
        Visibility = Visibility.Visible;
        Opacity = 1;
        phaseText.Text = PhaseLabel(nextState.Phase);
        statusText.Text = nextState.Status;
        AutomationProperties.SetLiveSetting(
            statusText,
            nextState.Phase == StartupLoadingPhase.Failed
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);

        var hasProgress = nextState.Progress is not null;
        progress.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        if (nextState.Progress is { } value)
        {
            progress.Value = value * 100;
            AutomationProperties.SetHelpText(progress, $"{Math.Round(progress.Value):0} percent complete");
        }

        retryButton.Visibility = nextState.Phase == StartupLoadingPhase.Failed && nextState.CanRetry
            ? Visibility.Visible
            : Visibility.Collapsed;
        statusText.Foreground = nextState.Phase == StartupLoadingPhase.Failed && !SystemParameters.HighContrast
            ? OrbitVisualTheme.Danger
            : SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : OrbitVisualTheme.Ink;

        if (nextState.Phase == StartupLoadingPhase.Ready)
        {
            Complete();
            return;
        }

        UpdateAnimations();
    }

    public void Complete()
    {
        state = StartupLoadingState.ReadyState();
        phaseText.Text = PhaseLabel(StartupLoadingPhase.Ready);
        statusText.Text = state.Status;
        StopAllMotion();
        if (ReducedMotion || SystemParameters.HighContrast)
        {
            Visibility = Visibility.Collapsed;
            Cleared?.Invoke(this, EventArgs.Empty);
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Cleared?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void ShowFailure(string safeStatus, bool canRetry = true) =>
        SetState(StartupLoadingState.Failure(safeStatus, canRetry));

    private void BuildLayout()
    {
        Children.Add(backgroundImage);
        sequenceLayer.Children.Add(frameA);
        sequenceLayer.Children.Add(frameB);
        Children.Add(sequenceLayer);
        Children.Add(new Border
        {
            Background = new LinearGradientBrush(
                Color.FromArgb(22, 11, 17, 23),
                Color.FromArgb(172, 11, 17, 23),
                new Point(0.16, 0),
                new Point(0.84, 1)),
            IsHitTestVisible = false,
        });

        orbitalField.Child = CreateOrbitalField();
        Children.Add(orbitalField);

        card.Width = 520;
        card.MaxWidth = 620;
        card.Padding = new Thickness(36, 32, 36, 32);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Background = new SolidColorBrush(Color.FromArgb(226, 17, 26, 34));
        card.Effect = CreateCardShadow();

        mark.RenderTransform = markRotation;
        mark.RenderTransformOrigin = new Point(0.5, 0.5);
        var route = new Border
        {
            Width = 42,
            Height = 3,
            Background = OrbitVisualTheme.WaypointGold,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 18),
        };
        var title = new TextBlock
        {
            Text = "Orbit Navigator",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            Foreground = OrbitVisualTheme.Ink,
        };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        phaseText.Foreground = OrbitVisualTheme.SeaGlass;
        statusText.Foreground = OrbitVisualTheme.Ink;
        OrbitVisualTheme.ApplyProgressBar(progress);
        AutomationProperties.SetName(progress, "Startup progress");
        OrbitVisualTheme.ApplyButton(retryButton, OrbitButtonRole.Primary);
        AutomationProperties.SetName(retryButton, "Try startup again");
        retryButton.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);

        var content = new StackPanel();
        content.Children.Add(mark);
        content.Children.Add(route);
        content.Children.Add(title);
        content.Children.Add(phaseText);
        content.Children.Add(statusText);
        content.Children.Add(progress);
        content.Children.Add(retryButton);
        card.Child = content;
        Children.Add(card);
    }

    private Canvas CreateOrbitalField()
    {
        var canvas = new Canvas { Width = 760, Height = 760 };
        var outer = CreateOrbitLayer(600, 280, -24, outerOrbitRotation, 72, 238);
        var inner = CreateOrbitLayer(420, 420, 18, innerOrbitRotation, 594, 330);
        Canvas.SetLeft(outer, 80);
        Canvas.SetTop(outer, 240);
        Canvas.SetLeft(inner, 170);
        Canvas.SetTop(inner, 170);
        canvas.Children.Add(outer);
        canvas.Children.Add(inner);
        return canvas;
    }

    private Grid CreateOrbitLayer(
        double width,
        double height,
        double angle,
        RotateTransform rotation,
        double waypointX,
        double waypointY)
    {
        var layer = new Grid
        {
            Width = width,
            Height = height,
            RenderTransform = rotation,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var ring = new Ellipse
        {
            Stroke = OrbitVisualTheme.SeaGlass,
            StrokeThickness = 1.2,
            Opacity = 0.55,
            RenderTransform = new RotateTransform(angle),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var waypoint = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = OrbitVisualTheme.WaypointGold,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.9,
                Color = Color.FromRgb(226, 166, 83),
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(waypointX, waypointY, 0, 0),
        };
        waypointLights.Add(waypoint);
        layer.Children.Add(ring);
        layer.Children.Add(waypoint);
        return layer;
    }

    private static Image CreateLaunchImage(double opacity)
    {
        var scale = new ScaleTransform(1.02, 1.02);
        var translate = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        return new Image
        {
            Stretch = Stretch.UniformToFill,
            Opacity = opacity,
            IsHitTestVisible = false,
            Focusable = false,
            RenderTransform = transforms,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
    }

    private void RefreshVisualAssets()
    {
        if (SystemParameters.HighContrast)
        {
            backgroundImage.Source = null;
            backgroundImage.Visibility = Visibility.Collapsed;
            sequenceLayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (sequenceFrames.Count == coordinatorSequenceFileNames.Length)
        {
            backgroundImage.Visibility = Visibility.Collapsed;
            sequenceLayer.Visibility = Visibility.Visible;
            var target = ReducedMotion ? sequenceFrames.Count - 1 : Math.Max(currentSequenceFrameIndex, 0);
            ShowFrameImmediately(target);
            return;
        }

        sequenceLayer.Visibility = Visibility.Collapsed;
        var fallback = TryLoadBitmap(backgroundAssetPath);
        backgroundImage.Source = fallback;
        backgroundImage.Visibility = fallback is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static ImageSource? TryLoadBitmap(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) ||
            !IOPath.IsPathFullyQualified(absolutePath) ||
            !File.Exists(absolutePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(absolutePath!, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
            UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private void ShowFrameImmediately(int index)
    {
        if (index < 0 || index >= sequenceFrames.Count)
        {
            return;
        }

        StopFrameAnimations(frameA);
        StopFrameAnimations(frameB);
        frameA.Source = sequenceFrames[index];
        frameA.Opacity = 0.94;
        frameB.Source = null;
        frameB.Opacity = 0;
        Panel.SetZIndex(frameA, 1);
        Panel.SetZIndex(frameB, 0);
        ApplyStaticDepth(frameA, index);
        currentSequenceFrameIndex = index;
    }

    private void RenderTimelineFrame(int frameNumber)
    {
        var normalized = (frameNumber % TimelineFrameCount) / (double)TimelineFrameCount;
        if (sequenceFrames.Count == coordinatorSequenceFileNames.Length)
        {
            var keyframePosition = normalized * sequenceFrames.Count;
            var currentIndex = (int)Math.Floor(keyframePosition) % sequenceFrames.Count;
            var nextIndex = (currentIndex + 1) % sequenceFrames.Count;
            var localProgress = keyframePosition - Math.Floor(keyframePosition);
            var blend = SmoothStep(localProgress);

            if (!ReferenceEquals(frameA.Source, sequenceFrames[currentIndex]))
            {
                frameA.Source = sequenceFrames[currentIndex];
            }

            if (!ReferenceEquals(frameB.Source, sequenceFrames[nextIndex]))
            {
                frameB.Source = sequenceFrames[nextIndex];
            }

            frameA.Opacity = 0.94 * (1 - blend);
            frameB.Opacity = 0.94 * blend;
            Panel.SetZIndex(frameA, 1);
            Panel.SetZIndex(frameB, 2);
            ApplyTimelineDepth(frameA, normalized, currentIndex, false);
            ApplyTimelineDepth(frameB, normalized, nextIndex, true);
            currentSequenceFrameIndex = currentIndex;
        }

        outerOrbitRotation.Angle = normalized * 360;
        innerOrbitRotation.Angle = 18 - (normalized * 360);
        markRotation.Angle = normalized * 360;
        for (var index = 0; index < waypointLights.Count; index++)
        {
            var pulse = 0.5 + (0.5 * Math.Sin((normalized * Math.PI * 4) + (index * Math.PI)));
            waypointLights[index].Opacity = 0.42 + (0.58 * pulse);
        }
    }

    private static void ApplyStaticDepth(Image image, int frameIndex)
    {
        var (scale, translate) = GetDepthTransforms(image);
        scale.ScaleX = 1.018;
        scale.ScaleY = 1.018;
        translate.X = frameIndex % 2 == 0 ? -3 : 3;
        translate.Y = frameIndex < 2 ? 2 : -2;
    }

    private static void ApplyTimelineDepth(
        Image image,
        double normalized,
        int frameIndex,
        bool incoming)
    {
        var (scale, translate) = GetDepthTransforms(image);
        var phase = (normalized * Math.PI * 2) + (frameIndex * 0.7) + (incoming ? 0.35 : 0);
        var depth = 1.021 + (0.009 * Math.Sin(phase));
        scale.ScaleX = depth;
        scale.ScaleY = depth;
        translate.X = 7 * Math.Sin(phase * 0.74);
        translate.Y = 4 * Math.Cos(phase * 0.62);
    }

    private static double SmoothStep(double value) => value * value * (3 - (2 * value));

    private static (ScaleTransform Scale, TranslateTransform Translate) GetDepthTransforms(Image image)
    {
        var group = (TransformGroup)image.RenderTransform;
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    private static void StopFrameAnimations(Image image)
    {
        image.BeginAnimation(OpacityProperty, null);
        var (scale, translate) = GetDepthTransforms(image);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void UpdateAnimations()
    {
        var animate = IsLoaded && !ReducedMotion && !SystemParameters.HighContrast &&
            state.Phase is not (StartupLoadingPhase.Ready or StartupLoadingPhase.Failed);
        orbitalField.Visibility = SystemParameters.HighContrast ? Visibility.Collapsed : Visibility.Visible;
        card.Effect = SystemParameters.HighContrast ? null : CreateCardShadow();
        if (!animate)
        {
            sequenceTimer.Stop();
            outerOrbitRotation.Angle = -8;
            innerOrbitRotation.Angle = 12;
            markRotation.Angle = 0;
            foreach (var waypoint in waypointLights)
            {
                waypoint.Opacity = 0.8;
            }

            return;
        }

        RenderTimelineFrame(timelineFrame);
        sequenceTimer.Start();
    }

    private void StopAllMotion()
    {
        sequenceTimer.Stop();
        StopFrameAnimations(frameA);
        StopFrameAnimations(frameB);
    }

    private static DropShadowEffect CreateCardShadow() => new()
    {
        BlurRadius = 30,
        ShadowDepth = 8,
        Opacity = 0.36,
        Color = Colors.Black,
    };

    private static string PhaseLabel(StartupLoadingPhase phase) => phase switch
    {
        StartupLoadingPhase.Starting => "STARTING",
        StartupLoadingPhase.PreparingProfile => "PREPARING PRIVATE PROFILE",
        StartupLoadingPhase.StartingBrowserEngine => "STARTING WEB ENGINE",
        StartupLoadingPhase.RestoringSession => "RESTORING LOCAL SESSION",
        StartupLoadingPhase.Ready => "READY",
        StartupLoadingPhase.Failed => "STARTUP NEEDS ATTENTION",
        _ => "STARTING",
    };

    private void ApplyAccessibilityPalette()
    {
        if (SystemParameters.HighContrast)
        {
            Background = SystemColors.WindowBrush;
            card.Background = SystemColors.WindowBrush;
            card.BorderBrush = SystemColors.WindowTextBrush;
            mark.Stroke = SystemColors.WindowTextBrush;
            phaseText.Foreground = SystemColors.HighlightBrush;
            statusText.Foreground = SystemColors.WindowTextBrush;
        }
        else
        {
            Background = OrbitVisualTheme.Canvas;
            card.Background = new SolidColorBrush(Color.FromArgb(226, 17, 26, 34));
            card.BorderBrush = OrbitVisualTheme.Divider;
            mark.Stroke = OrbitVisualTheme.SeaGlass;
            phaseText.Foreground = OrbitVisualTheme.SeaGlass;
            statusText.Foreground = state.Phase == StartupLoadingPhase.Failed
                ? OrbitVisualTheme.Danger
                : OrbitVisualTheme.Ink;
        }

        OrbitVisualTheme.ApplyProgressBar(progress);
        OrbitVisualTheme.ApplyButton(retryButton, OrbitButtonRole.Primary);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            systemParameterEventsAttached = true;
        }

        ApplyAccessibilityPalette();
        RefreshVisualAssets();
        UpdateAnimations();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (systemParameterEventsAttached)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            systemParameterEventsAttached = false;
        }

        StopAllMotion();
    }

    private void OnSequenceTick(object? sender, EventArgs args)
    {
        timelineFrame = (timelineFrame + 1) % TimelineFrameCount;
        RenderTimelineFrame(timelineFrame);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.HighContrast) or null)
        {
            ApplyAccessibilityPalette();
            RefreshVisualAssets();
            UpdateAnimations();
        }
    }
}
#endif
