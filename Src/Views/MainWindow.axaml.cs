using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.Contracts;
using ImageViewer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ImageViewer.Views;

internal sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _timerPointerCursorHide;
    private readonly MainViewModel _mainViewModel;
    private CancellationTokenSource? _cts = new();
    private Avalonia.Point _mousePosition;
    private string _lastOpenedDirectory = string.Empty;
    private bool _isClosing;
    private bool _isAllowClose;

    private int _winRestoreWidth = 1024;
    private int _winRestoreHeight = 768;
    private int _winRestoreTop = 100;
    private int _winRestoreLeft = 100;

    private Avalonia.Platform.Screen? _currentScreen;
    private double _systemDpiScalingFactor = 1.0;

    private Process? _LinuxSleepInhibitorProcess;

    private readonly IConfigurationService _configurationService;

    // parameterless constructor for XAML Previewer
    public MainWindow() 
    {
        _mainViewModel = App.GetService<MainViewModel>();
        this.DataContext = _mainViewModel;
        _configurationService = App.GetService<IConfigurationService>();

        InitializeComponent();

        LoadSettings();

        UpdateThemeBackground(ActualThemeVariant);

        //this.Title = ImageViewer.Properties.Resources.AppTitle;

        // Moved to Window_Loaded.
        //this.ContentFrame.Content = App.GetService<MainView>();

        // There is some issue showing sysmenu in AvaloniaUI.
        //TryRegisterWindowsMenu();

        _timerPointerCursorHide = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timerPointerCursorHide.Tick += OnPointerCursorHideTimerTick;

        // TODO: more
        InitKeyBindigs();

        // Subscribe to PointerPressed with Tunneling strategy
        QueueListBox.AddHandler(InputElement.PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnOpened(EventArgs e)
    {
        this.PropertyChanged += this.OnWindow_PropertyChanged;
        this.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        this.PositionChanged += Window_PositionChanged;
        _mainViewModel.QueueHasBeenChanged += OnQueueHasBeenChanged;
        _mainViewModel.SlideshowStatusChanged += OnSlideshowStatusChanged;
        _mainViewModel.QueueLoaded += OnQueueLoaded;
        _mainViewModel.ToggleFullscreenState += OnToggleFullscreenState;
        _mainViewModel.HideMenuFlyout += OnHideMenuFlyout;
        _mainViewModel.SlideshowIntervalChanged += OnSlideshowIntervalChanged;
        _mainViewModel.WorkingStateChanged += OnWorkingStateChanged;

        base.OnOpened(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        this.PropertyChanged -= this.OnWindow_PropertyChanged;
        this.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        this.PositionChanged -= Window_PositionChanged;
        _mainViewModel.QueueHasBeenChanged -= OnQueueHasBeenChanged;
        _mainViewModel.SlideshowStatusChanged -= OnSlideshowStatusChanged;
        _mainViewModel.QueueLoaded -= OnQueueLoaded;
        _mainViewModel.ToggleFullscreenState -= OnToggleFullscreenState;
        _mainViewModel.HideMenuFlyout -= OnHideMenuFlyout;
        _mainViewModel.SlideshowIntervalChanged -= OnSlideshowIntervalChanged;
        _mainViewModel.WorkingStateChanged -= OnWorkingStateChanged;

        if (this.Screens is { } screens)
        {
            screens.Changed -= Screens_Changed;
        }

        base.OnClosed(e);

        // Since we no longer use IHost, we need to manually dispose of the MainViewModel to ensure proper cleanup of resources.
        _mainViewModel?.Dispose();

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            Debug.WriteLine("MainWindow CancellationTokenSource disposed successfully.");
        }
        catch (ObjectDisposedException)
        {
            // The CancellationTokenSource has already been disposed, so we can safely ignore this exception.
        }
        catch (OperationCanceledException)
        {
            // The CancellationTokenSource has already been canceled, so we can safely ignore this exception.
        }
        catch (AggregateException ae)
        {
            foreach (var ex in ae.InnerExceptions)
            {
                Debug.WriteLine($"Error disposing CancellationTokenSource: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing CancellationTokenSource: {ex.Message}");
        }
    }

    private void OnWorkingStateChanged(object? sender, bool e)
    {
        if (_mainViewModel.IsSlideshowOn)
        {
            //this.Cursor = new Cursor(StandardCursorType.None); Not good.
            return;
        }

        if (e)
        {
            this.Cursor = new Cursor(StandardCursorType.AppStarting);
        }
        else
        {
            this.Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    public async void SetStdin(string[] args)
    {
        if (args.Length > 0)
        {
            // Too early?
            this.WelcomeMessageGrid.IsVisible = false;

            //await ProcessFiles([.. args]);
            await Task.Run(() => ProcessFiles([.. args]));
        }
    }

    private void OnQueueLoaded(object? sender, EventArgs e)
    {
        this.WelcomeMessageGrid.IsVisible = false;
    }

    private void OnPointerCursorHideTimerTick(object? sender, EventArgs e)
    {
        // This code runs on the UI thread, so it's safe to update UI elements.

        var flyout = FlyoutBase.GetAttachedFlyout(this);
        if (flyout is not null)
        {
            if (flyout.IsOpen)
            {
                this.Cursor = Cursor.Default;
                return;
            }
        }

        if (_mainViewModel.IsSlideshowOn || (this.WindowState == WindowState.FullScreen))
        {
            // TODO: Is there any way to determine current cursor type?
            // Creating new cursor every time is weird.
            this.Cursor = new Cursor(StandardCursorType.None);
        }
        else
        {
            this.Cursor = Cursor.Default;
        }
    }

    private void OnQueueHasBeenChanged(object? sender, int ind)
    {
        UpdateQueueListBoxImages(ind);
    }

    private void OnSlideshowStatusChanged(object? sender, EventArgs e)
    {
        if (this.WindowState != WindowState.FullScreen)
        {
            if (!_mainViewModel.IsSlideshowOn)
            {
                this.Cursor = Cursor.Default;
            }
            return;
        }

        if (_mainViewModel.IsSlideshowOn)
        {
            //NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);
            StartSleepInhibitor();
        }
        else
        {
            //NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
            StopSleepInhibitor();
        }
    }

    private void OnWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Window)
        {
            return;
        }
        if (e.Property.Name != nameof(WindowState))
        {
            return;
        }

        if (this.DataContext is not MainViewModel vm)
        {
            return;
        }

        // For linux.
        if (e.NewValue != null && e.OldValue != null)
        {
            if (e.NewValue.Equals(e.OldValue))
            {
                return;
            }
        }
        else
        {
            return;
        }

        //Debug.WriteLine($"WindowState changed from {e.OldValue} to {e.NewValue}");

        if (e.NewValue is WindowState.FullScreen)
        {
            vm.IsFullscreen = true;

            SetWindowStateFullScreen();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // It's actually 10px on all sides, but the window border is 2px, so the total is 10px.
                // This covers the window border and makes it look little sophisticated.
                this.BackgroundLayerBorder.Margin = new Thickness(0, 0, 0, 0);
                this.ContentFrame.Margin = new Thickness(0, 0, 0, 0);
            }
        }
        else if (e.NewValue is WindowState.Maximized)
        {
            vm.IsFullscreen = false;

            SetWindowStateNormal();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                this.BackgroundLayerBorder.Margin = new Thickness(0, 0, 0, 0);
                this.ContentFrame.Margin = new Thickness(0, 0, 0, 0);
            }
        }
        else
        {
            vm.IsFullscreen = false;

            SetWindowStateNormal();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // It's actually 10px on all sides, but the window border is 2px, so the total is 10px.
                // This covers the window border and makes it look little sophisticated.
                this.BackgroundLayerBorder.Margin = new Thickness(8, 8, 8, 8);
                this.ContentFrame.Margin = new Thickness(9, 9, 9, 9);
            }
        }
    }

    private void UpdateThemeBackground(ThemeVariant theme)
    {
        //(App.Current as App)!.RequestedThemeVariant
        //ActualThemeVariant

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            this.BackgroundLayerBorder.IsVisible = true;
            this.BackgroundLayerBorder.Opacity = 0.5;
            this.ListBoxBackgroundLayerBorder.Opacity = 0;

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 1803))
            {
                // Get the window's platform handle
                var handle = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle != IntPtr.Zero)
                {
                    this.Background = Brushes.Transparent;
                    this.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

                    //this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                    EnableBlurBehind(handle);
                }
                else
                {
                    this.Background = Brushes.Transparent;
                    this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                    //this.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
                }
            }
            else
            {
                this.Background = Brushes.Transparent;
                this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                //this.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            }

            if (theme == ThemeVariant.Dark)
            {
                //this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#121212"));
            }
            else if (theme == ThemeVariant.Light)
            {
                //this.TransparencyLevelHint = [WindowTransparencyLevel.Blur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#EEEEEE"));
            }
            else if (theme == ThemeVariant.Default)
            {
                //this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#121212"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            this.BackgroundLayerBorder.IsVisible = true;
            this.BackgroundLayerBorder.Opacity = 1;
            this.ListBoxBackgroundLayerBorder.Opacity = 0;

            if ((this.WindowState == WindowState.FullScreen) || (this.WindowState == WindowState.Maximized))
            {
                this.BackgroundLayerBorder.Margin = new Thickness(0, 0, 0, 0);
                this.ContentFrame.Margin = new Thickness(0, 0, 0, 0);
            }
            else
            {
                // It's actually 10px on all sides, but the window border is 2px, so the total is 10px.
                // This covers the window border and makes it look little sophisticated.
                this.BackgroundLayerBorder.Margin = new Thickness(8, 8, 8, 8);
                // For the images, 
                this.ContentFrame.Margin = new Thickness(9, 9, 9, 9);
            }

            if (theme == ThemeVariant.Dark)
            {
                //this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#121212"));
            }
            else if (theme == ThemeVariant.Light)
            {
                //this.TransparencyLevelHint = [WindowTransparencyLevel.Blur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#EEEEEE"));
            }
            else if (theme == ThemeVariant.Default)
            {
                //this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#121212"));
            }
        }
        else
        {
            this.BackgroundLayerBorder.IsVisible = true;
            this.BackgroundLayerBorder.Opacity = 0.9;

            if (theme == ThemeVariant.Dark)
            {
                this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#222222"));
            }
            else if (theme == ThemeVariant.Light)
            {
                this.TransparencyLevelHint = [WindowTransparencyLevel.Blur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
            }
            else if (theme == ThemeVariant.Default)
            {
                this.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
                this.BackgroundLayerBorder.Background = new SolidColorBrush(Color.Parse("#121212"));
            }
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        UpdateThemeBackground(ActualThemeVariant);
    }

    public void OnToggleFullscreenState(object? sender, EventArgs e)
    {
        ToggleFullScreen();
    }

    public void OnSlideshowIntervalChanged(object? sender, long interval)
    {
        this.Interval1SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval2SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval3SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval4SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval5SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval6SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval7SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval8SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval9SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval10SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval15SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval20SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval30SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval60SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval120SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval180SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval300SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;
        this.Interval600SecIconData.Data = MainViewModel.DataUnCheckedCircleIcon;

        if (interval <= 1)
        {
            this.Interval1SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 2)
        {
            this.Interval2SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 3)
        {
            this.Interval3SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 4)
        {
            this.Interval4SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 5)
        {
            this.Interval5SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 6)
        {
            this.Interval6SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 7)
        {
            this.Interval7SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 8)
        {
            this.Interval8SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 9)
        {
            this.Interval9SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 10)
        {
            this.Interval10SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 15)
        {
            this.Interval15SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 20)
        {
            this.Interval20SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 30)
        {
            this.Interval30SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 60)
        {
            this.Interval60SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 120)
        {
            this.Interval120SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 180)
        {
            this.Interval180SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval == 300)
        {
            this.Interval300SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
        else if (interval >= 600)
        {
            this.Interval600SecIconData.Data = MainViewModel.DataCheckedCircleIcon;
        }
    }

    public void OnHideMenuFlyout(object? sender, EventArgs e)
    {
        var flyout = FlyoutBase.GetAttachedFlyout(this);

        flyout?.Hide();

        this.Activate();
        this.Focus();
    }

    private void LoadSettings()
    {
        var mw = _configurationService.CurrentConfiguration.MainWindow;

        // TODO: multi-monitor support. If the saved position is outside of the current monitor, move it to the monitor.
        if ((mw.Left >= 0) && (mw.Top >= 0))
        {
            this.Position = new PixelPoint(mw.Left, mw.Top);
        }
        else
        {
            this.Position = new PixelPoint(0, 0);
        }

        if (mw.Height >= 120)
        {
            this.Height = mw.Height;
        }

        if (mw.Width >= 480)
        {
            this.Width = mw.Width;
        }

        _winRestoreWidth = (int)mw.RestoreWidth;
        _winRestoreHeight = (int)mw.RestoreHeight;
        _winRestoreLeft = mw.RestoreLeft;
        _winRestoreTop = mw.RestoreTop;

        if (mw.WindowState == WindowState.Maximized)
        {
            this.WindowState = WindowState.Maximized;
        }
        else
        {
            this.WindowState = WindowState.Normal;
        }
    }

    private void InitKeyBindigs()
    {
        // TODO: more

        // 
        var ToggleSlideshowCommandKeyBinding = new KeyBinding
        {
            Gesture = new KeyGesture(Avalonia.Input.Key.Space, KeyModifiers.None),
            Command = _mainViewModel.ToggleSlideshowCommand
        };
        // set keybinding here instead of in Window_KeyDown.
        this.KeyBindings.Add(ToggleSlideshowCommandKeyBinding);

        // MenuItemStartSlideshow
        this.MenuItemStartSlideshow.InputGesture = new KeyGesture(Avalonia.Input.Key.Space, KeyModifiers.None);
        this.MenuItemStartSlideshow.KeyBindings.Add(ToggleSlideshowCommandKeyBinding);
        //
        var QuitCommandKeyBinding = new KeyBinding
        {
            Gesture = new KeyGesture(Avalonia.Input.Key.Q, KeyModifiers.Control),
            Command = _mainViewModel.QuitCommand
        };
        this.KeyBindings.Add(QuitCommandKeyBinding);

        // MenuItemQuit
        this.MenuItemQuit.InputGesture = new KeyGesture(Avalonia.Input.Key.Q, KeyModifiers.Control);
        this.MenuItemQuit.KeyBindings.Add(QuitCommandKeyBinding);
    }

    private void UpdateSystemDPIScalingFactor()
    {
        var platformHandle = TryGetPlatformHandle();
        if (platformHandle == null) return;

        var screen = this.Screens.ScreenFromWindow(this);

        _systemDpiScalingFactor = screen?.Scaling ?? 1.0;
        //_systemDpiScalingFactor = DpiHelper.GetWindowScalingFactor(platformHandle.Handle);

        var hardwareDisplayWidth = screen?.Bounds.Width ?? 3840;
        var hardwareDisplayHeight = screen?.Bounds.Height ?? 2160;
        _mainViewModel.UpdateScreenInfo(_systemDpiScalingFactor, hardwareDisplayWidth, hardwareDisplayHeight);

        Debug.WriteLine($"SystemDPIScalingFactor = {_systemDpiScalingFactor}, HardwareDisplayWidth = {hardwareDisplayWidth}");

        if (_systemDpiScalingFactor == 1.0)
        {
            this.MenuItemSystemDpiScalingFactor.IsVisible = false;
            this.MenuItemSystemDpiScalingFactorSeparator.IsVisible = false;
            this.MenuItemSystemDpiScalingFactor.IsEnabled = false;
            this.MenuItemSystemDpiScalingFactor.Header = ImageViewer.Properties.Resources.String_OverrideDPIScaling_Default;//$"Override DPI Scaling (100%)"; //Override System DPI Scaling Factor 
            //return;
        }
        else
        //if (_mainViewModel.SystemDpiScalingFactor != _systemDpiScalingFactor)
        {
            //_mainViewModel.SystemDpiScalingFactor = _systemDpiScalingFactor;

            this.MenuItemSystemDpiScalingFactor.IsVisible = true;
            this.MenuItemSystemDpiScalingFactorSeparator.IsVisible = true;
            this.MenuItemSystemDpiScalingFactor.IsEnabled = true;
            this.MenuItemSystemDpiScalingFactor.Header = string.Format(ImageViewer.Properties.Resources.String_OverrideDPIScaling, (_systemDpiScalingFactor * 100)); //$"Override DPI Scaling ({_systemDpiScalingFactor * 100}%)"; //Override System DPI Scaling Factor 
        }
    }

    private void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Too late to change window size etc. Move to constructor.
        //LoadSettings();

        this.ContentFrame.Content = App.GetService<MainView>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            this.BackgroundLayerBorder.IsVisible = true;
            this.BackgroundLayerBorder.StartFadeInAnimation(TimeSpan.FromSeconds(0.8), 0.9f);
        }

        // Update Slideshow Interval menu 
        OnSlideshowIntervalChanged(this, _mainViewModel.SlideshowTimerInterval);

        // SystemDpiScalingFactor - Set default.
        /*
        this.MenuItemSystemDpiScalingFactor.IsVisible = false;
        this.MenuItemSystemDpiScalingFactorSeparator.IsVisible = false;
        this.MenuItemSystemDpiScalingFactor.IsEnabled = false;
        this.MenuItemSystemDpiScalingFactor.Header = ImageViewer.Properties.Resources.String_OverrideDPIScaling_Default;//$"Override DPI Scaling (100%)"; //Override System DPI Scaling Factor 
        */

        // SystemDpiScalingFactor 
        // Initial screen detection upon loading
        _currentScreen = GetCurrentScreen();

        //Debug.WriteLine($"Initial Screen: {_currentScreen?.DisplayName}");

        // Optional: Subscribe to global screen configuration changes (e.g., resolution change, monitor added/removed)
        if (this.Screens is { } screens)
        {
            screens.Changed += Screens_Changed;
        }

        UpdateSystemDPIScalingFactor();
    }

    private void Window_PositionChanged(object? sender, PixelPointEventArgs e)
    {
        CheckAndNotifyDisplayChange();

        if (this.WindowState == WindowState.Normal)
        {
            _winRestoreHeight = (int)this.Height;
            _winRestoreWidth = (int)this.Width;
            _winRestoreTop = this.Position.Y;
            _winRestoreLeft = this.Position.X;
        }
    }

    private void Screens_Changed(object? sender, EventArgs e)
    {
        // Screen configurations changed, check if the window is still on the same "logical" screen
        CheckAndNotifyDisplayChange();
    }

    private void CheckAndNotifyDisplayChange()
    {
        var newScreen = GetCurrentScreen();

        if (newScreen != null && _currentScreen != null && newScreen != _currentScreen)
        {
            // Compare a unique identifier, like the screen bounds or name if available
            if (newScreen.Bounds != _currentScreen.Bounds)
            {
                //System.Diagnostics.Debug.WriteLine($"Window moved from screen {_currentScreen.Bounds} to {newScreen.Bounds}");
                // *** Raise your custom event or execute your logic here ***
                OnDisplayChanged(newScreen);
                _currentScreen = newScreen;
            }
        }
        else if (_currentScreen == null && newScreen != null)
        {
            // Handle case where window was on no screen and moved to one
            _currentScreen = newScreen;
            OnDisplayChanged(newScreen);
        }
    }

    private Avalonia.Platform.Screen? GetCurrentScreen()
    {
        if (this.Screens is not { } screens || screens.All.Count == 0)
        {
            return null;
        }

        // Get the screen that most contains the window
        // The window's position is its top-left corner in virtual screen coordinates
        var windowBounds = new PixelRect(Position, new PixelSize((int)Width, (int)Height));

        // Use ScreenFromBounds which is more robust than relying just on top-left pixel
        return screens.ScreenFromBounds(windowBounds);

        // Alternatively, use ScreenFromVisual (available on TopLevel)
        // return screens.ScreenFromVisual(this); 
    }

    private void OnDisplayChanged(Avalonia.Platform.Screen newScreen)
    {
        Debug.WriteLine($"Window has crossed to a new display: {newScreen.DisplayName}");

        UpdateSystemDPIScalingFactor();
    }

    private async void Window_Closing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        Debug.WriteLine("OnClosing");

        if (_isAllowClose) return;

        e.Cancel = true; // Cancel the default close behavior to allow for async operations

        if (_isClosing) return;
        _isClosing = true;

        this.Hide();

        try
        {
            await SaveSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in Window_Closing: {ex.Message}");
        }
        finally
        {
            StopSleepInhibitor();
        }

        _isAllowClose = true;

        this.Close();
    }

    private async Task SaveSettings()
    {
        _mainViewModel.UpdateSettings();

        var config = _configurationService.CurrentConfiguration;

        config.LastOpenedDirectory = _lastOpenedDirectory;

        // Window Position must be saved before the window is closed, otherwise the position will be lost.
        #region == Save Configuration ==

        config.MainWindow.Width = this.Width;
        config.MainWindow.Height = this.Height;
        config.MainWindow.Left = this.Position.X;
        config.MainWindow.Top = this.Position.Y;
        config.MainWindow.WindowState = this.WindowState;
        config.MainWindow.RestoreWidth = _winRestoreWidth;
        config.MainWindow.RestoreHeight = _winRestoreHeight;
        config.MainWindow.RestoreLeft = _winRestoreLeft;
        config.MainWindow.RestoreTop = _winRestoreTop;

        await _configurationService.SaveConfigurationAsync();

        #endregion

    }

    private void Window_Closed(object? sender, System.EventArgs e)
    {
        Debug.WriteLine("Window_Closed()");

        // Do not use this.Close() in the MainWindow. 
        // Use overrided OnClosed() instead.
    }

    private void Window_Resized(object? sender, Avalonia.Controls.WindowResizedEventArgs e)
    {
        UpdateQueueListBoxImages();

        if (this.WindowState == WindowState.Normal)
        {
            _winRestoreHeight = (int)this.Height;
            _winRestoreWidth = (int)this.Width;
            _winRestoreTop = this.Position.Y;
            _winRestoreLeft = this.Position.X;
        }

        _mainViewModel.ClientAreaSizeChanged(this.ClientAreaGrid.Bounds.Width, this.ClientAreaGrid.Bounds.Height);
    }

    private void Window_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        /*
        if (this.WindowState != WindowState.FullScreen)
        {
            e.Handled = true;
            return;
        }
        */
        /*
        if (!_mainViewModel.IsSlideshowOn)
        {
            e.Handled = true;
            return;
        }
        */

        var position = e.GetPosition(this);

        if ((position.X == _mousePosition.X) && (position.Y == _mousePosition.Y))
        {
            //Debug.WriteLine($"Same pos, returning: sender {sender}, X {position.X}, Y {position.Y}, Source {e.Source}");
            e.Handled = true;
            return;
        }

        _mousePosition = position;

        if (_timerPointerCursorHide.IsEnabled)
        {
            _timerPointerCursorHide.Stop();
        }

        if (_mainViewModel.IsWorking)
        {
            this.Cursor = new Cursor(StandardCursorType.AppStarting);
        }
        else
        {
            this.Cursor = Cursor.Default;
        }

        _timerPointerCursorHide.Start();

        e.Handled = true;
    }

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (this.WindowState == WindowState.Normal)
            {
                // Call BeginMoveDrag to enable window dragging
                BeginMoveDrag(e);
            }
            else if (this.WindowState == WindowState.FullScreen)
            {
                // do nothing.
            }
        }
        else if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // Right clicked on Window.
            if (sender is Window target)
            {
                if (_mainViewModel.IsWorking)
                {
                    this.Cursor = new Cursor(StandardCursorType.AppStarting);
                }
                else
                {
                    this.Cursor = Cursor.Default;
                }

                var visualPoint = e.GetPosition(target);

                if (visualPoint.Y < 36)
                {
                    if (GetTopLevel(this)?.TryGetPlatformHandle() is not { } handle) return;
                    
                    var screenPoint = this.PointToScreen(visualPoint);

                    NativeMethods.ShowNativeSystemMenu(handle.Handle, screenPoint, target.WindowState, true);
                }
                else
                {
                    var flyout = FlyoutBase.GetAttachedFlyout(target);
                    if (flyout is MenuFlyout menuFlyout)
                    {
                        menuFlyout.Placement = PlacementMode.Pointer;
                    }

                    // Show the flyout using the 'Placement="Pointer"' property.
                    flyout?.ShowAt(target);

                    this.Activate();
                    this.Focus();
                }

            }
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataTransfer is null)
        {
            return;
        }

        // Only allow copy effect for file drops
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        // Deprecated.
        /*
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        */
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        //Debug.WriteLine("Window_Drop()");

        if (_mainViewModel.IsWorking)
        {
            // Already processing. 
            Debug.WriteLine("Already processing.");
            return;
        }

        if (e.DataTransfer is null)
        {
            return;
        }

        // Check if the dropped data contains file paths
        // Only allow copy effect for file drops
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;

            return;
        }

        if (_cts == null || _cts.IsCancellationRequested)
        {
            return;
        }

        _mainViewModel.IsWorking = true;

        var fileNames = e.DataTransfer.GetItems(DataFormat.File)?.ToList();
        if (fileNames is null || fileNames.Count == 0)
        {
            _mainViewModel.IsWorking = false;
            return;
        }

        var droppedFiles = new List<string>();
        foreach (var file in fileNames)
        {
            if (_cts == null || _cts.IsCancellationRequested)
            {
                return;
            }

            var filePath = file.TryGetFile()?.TryGetLocalPath(); //file.TryGetLocalPath(); Deprecated.
            if (string.IsNullOrEmpty(filePath))
            {
                continue;
            }

            droppedFiles.Add(filePath);

            Debug.WriteLine(filePath);
        }

        if (_cts == null || _cts.IsCancellationRequested)
        {
            return;
        }

        if (droppedFiles.Count > 0)
        {
            // Remove dupe. Looks like a bug in Avalonia... when dropped from desktop for the first time.
            // https://github.com/AvaloniaUI/Avalonia/pull/19864
            // We can remove this line after Above PR is merged and released.
            //droppedFiles = [.. droppedFiles.Distinct()];

            await Task.Run(() => ProcessFiles(droppedFiles));
        }
        else
        {
            _mainViewModel.IsWorking = false;
        }
    }

    private void RecursivelyProcessFiles(List<string> fileNames, List<FileSystemInfo> allItems)
    {
        if (_cts == null || _cts.IsCancellationRequested)
        {
            return;
        }

        // On Linux.
        //Sort
        IComparer<string> naturalSortComparer = new ImageViewer.Helpers.NaturalSortComparer();
        fileNames = [.. fileNames.OrderBy(x => x, naturalSortComparer)];//StringComparer.Ordinal

        // File first
        List<FileSystemInfo> files = [];
        foreach (var path in fileNames)
        {
            if (System.IO.File.Exists(path))
            {
                files.Add(new FileInfo(path));
            }
        }

        allItems.AddRange(files);

        if (_cts == null || _cts.IsCancellationRequested)
        {
            return;
        }

        // Dir next.
        foreach (var path in fileNames)
        {
            if (_cts == null || _cts.IsCancellationRequested)
            {
                return;
            }

            if (!Directory.Exists(path)) continue;

            DirectoryInfo directory = new(path);
            var folderFiles = directory.GetFileSystemInfos("*", SearchOption.TopDirectoryOnly).ToList();

            if (folderFiles is null) continue;

            List<string> folderFileNames = [.. folderFiles.Select(x => x.FullName)];

            if (folderFileNames.Count > 0)
            {
                if (_cts == null || _cts.IsCancellationRequested)
                {
                    return;
                }
                RecursivelyProcessFiles(folderFileNames, allItems);
            }
        }
    }

    private async Task ProcessFiles(List<string> fileNames)
    {
        //Debug.WriteLine("ProcessFiles()");

        _mainViewModel.IsWorking = true;

        // Don't await. FIRE and FORGET! Otherwise GUI would freeze or be 100x slower (try sleeping HDD).
        _ = Task.Run(async () =>
        {
            if (_cts == null || _cts.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _mainViewModel.IsWorking = true;
            });

            try
            {
                if (fileNames.Count > 0)
                {
                    if (System.IO.File.Exists(fileNames[0]))//.Path.LocalPath
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var parentFolderPath = System.IO.Path.GetDirectoryName(fileNames[0]);
                            if (parentFolderPath is not null)
                            {
                                // Writes to Window title bar.
                                this.Title = System.IO.Path.GetFileName(parentFolderPath);
                            }
                        });
                    }
                    else if (Directory.Exists(fileNames[0]))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            // Writes to Window title bar.
                            this.Title = System.IO.Path.GetFileName(fileNames[0]);
                        });
                    }
                }

                // 
                var droppedImages = new List<ImageInfo>();
                var singleSelectedOriginalFile = string.Empty;

                // Linux for sort
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) //RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || 
                {
                    //Debug.WriteLine("Linux sort mode @ProcessFiles()");

                    List<string> IncludeSiblingsFileNames = [];

                    // Single file dropped, in that case, get all siblings.
                    if (fileNames.Count == 1)
                    {
                        if (System.IO.File.Exists(fileNames[0]))
                        {
                            singleSelectedOriginalFile = fileNames[0];

                            // Get parent dir.
                            string? parentFolderPath = System.IO.Path.GetDirectoryName(singleSelectedOriginalFile);
                            if (parentFolderPath is not null)
                            {
                                if (Directory.Exists(parentFolderPath))
                                {
                                    // NON-Recursively get all files from the folder
                                    var filesInFolder = Directory.GetFiles(parentFolderPath, "*", SearchOption.TopDirectoryOnly);

                                    // Sort
                                    //filesInFolder = [.. filesInFolder.OrderBy(f => f)];

                                    IncludeSiblingsFileNames.AddRange(filesInFolder);

                                    if (IncludeSiblingsFileNames.Count > 1)
                                    {
                                        IComparer<string> naturalSortComparer = new ImageViewer.Helpers.NaturalSortComparer();
                                        IncludeSiblingsFileNames = [.. IncludeSiblingsFileNames.OrderBy(x => x, naturalSortComparer)];
                                    }
                                }
                                else
                                {
                                    IncludeSiblingsFileNames.Add(singleSelectedOriginalFile);
                                }
                            }
                            else
                            {
                                IncludeSiblingsFileNames.Add(singleSelectedOriginalFile);
                            }
                        }
                        else if (System.IO.Directory.Exists(fileNames[0]))
                        {
                            IncludeSiblingsFileNames.AddRange(fileNames);
                        }
                    }
                    else
                    {
                        IncludeSiblingsFileNames.AddRange(fileNames);
                    }

                    if (_cts == null || _cts.IsCancellationRequested)
                    {
                        return;
                    }

                    List<FileSystemInfo> allItems = [];

                    RecursivelyProcessFiles(IncludeSiblingsFileNames, allItems);

                    foreach (var item in allItems)
                    {
                        if (_cts == null || _cts.IsCancellationRequested)
                        {
                            return;

                        }

                        //Debug.WriteLine(item.FullName);

                        if (item is DirectoryInfo)
                        {
                            //Debug.WriteLine($"[FOLDER] Full Path: {item.FullName}, Name: {item.Name}");
                        }
                        else if (item is FileInfo)
                        {
                            //Debug.WriteLine($"[FILE]   Full Path: {item.FullName}, Name: {item.Name}");

                            if (!MainViewModel.HasImageExtension(item.Name, MainViewModel.ValidExtensions))
                            {
                                continue;
                            }

                            // Avoid MacOS's garbage. Use char overload for faster comp if possible.
                            if ((item.Name.StartsWith('.')) || (item.Name.StartsWith("._")))
                            {
                                continue;
                            }

                            var img = new ImageInfo
                            {
                                ImageFilePath = item.FullName
                            };

                            droppedImages.Add(img);
                        }
                    }

                    // Sort originaly selected single file to first position.
                    //if ((droppedImages.Count > 0) && (!string.IsNullOrEmpty(singleSelectedOriginalFile)))
                    //{
                    // Sort to move the first instance of 'originalFile' to the front, followed by other files.
                    // Using `Distinct()` will remove the remaining duplicates.
                    //droppedImages = droppedImages.OrderBy(x => x.ImageFilePath == singleSelectedOriginalFile ? 0 : 1).Distinct().ToList();
                    //}
                }
                else // if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    //Debug.WriteLine("None-Linux sort mode @ProcessFiles()");

                    var droppedFiles = new List<string>();

                    // Get all files recursively.
                    foreach (var item in fileNames)
                    {
                        if (_cts == null || _cts.IsCancellationRequested)
                        {
                            return;
                        }

                        if (System.IO.File.Exists(item))//.Path.LocalPath
                        {
                            // Add single files
                            droppedFiles.Add(item);//.Path.LocalPath
                        }
                        else if (Directory.Exists(item))//.Path.LocalPath
                        {
                            // Recursively get all files from a dropped folder
                            var filesInFolder = Directory.GetFiles(item, "*", SearchOption.AllDirectories);//.Path.LocalPath

                            // Sort
                            //filesInFolder = [.. filesInFolder.OrderBy(f => f)];
                            //filesInFolder = [.. filesInFolder.OrderBy(f => f, StringComparison.CurrentCultureIgnoreCase.WithNaturalSort())];

                            droppedFiles.AddRange(filesInFolder);
                        }
                    }

                    if (_cts == null || _cts.IsCancellationRequested)
                    {
                        return;
                    }

                    // Single file dropped, in that case, get all siblings.
                    if (droppedFiles.Count == 1)
                    {
                        if (System.IO.File.Exists(droppedFiles[0]))
                        {
                            //var originalFile = droppedFiles[0];
                            singleSelectedOriginalFile = droppedFiles[0];

                            // Get parent dir.
                            string? parentFolderPath = System.IO.Path.GetDirectoryName(droppedFiles[0]);
                            if (parentFolderPath is not null)
                            {
                                if (Directory.Exists(parentFolderPath))
                                {
                                    // NON-Recursively get all files from the folder
                                    var filesInFolder = Directory.GetFiles(parentFolderPath, "*", SearchOption.TopDirectoryOnly);

                                    // Sort
                                    //filesInFolder = [.. filesInFolder.OrderBy(f => f)];
                                    //filesInFolder = [.. filesInFolder.OrderBy(f => f, StringComparison.CurrentCultureIgnoreCase.WithNaturalSort())];

                                    droppedFiles.AddRange(filesInFolder);

                                    if (droppedFiles.Count > 1)
                                    {
                                        // Sort to move the first instance of 'originalFile' to the front, followed by other files.
                                        // Using `Distinct()` will remove the remaining duplicates.
                                        //droppedFiles = droppedFiles.OrderBy(x => x == singleSelectedOriginalFile ? 0 : 1).Distinct().ToList();

                                        // removes now duplicated image file path.
                                        droppedFiles.RemoveAt(0);
                                    }
                                }
                            }
                        }
                    }

                    foreach (var fileFullPath in droppedFiles)
                    {
                        if (_cts == null || _cts.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!MainViewModel.HasImageExtension(fileFullPath, MainViewModel.ValidExtensions))
                        {
                            continue;
                        }

                        // Avoid MacOS's garbage. Use char overload for faster comp if possible.
                        string fileName = System.IO.Path.GetFileName(fileFullPath);

                        if (fileName.StartsWith('.') || fileName.StartsWith("._"))
                        {
                            continue;
                        }

                        var img = new ImageInfo
                        {
                            ImageFilePath = fileFullPath
                        };

                        droppedImages.Add(img);
                    }
                }

                if (_cts == null || _cts.IsCancellationRequested)
                {
                    return;
                }

                if (droppedImages.Count < 1)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _mainViewModel.IsWorking = false;
                    });
                    return;
                }

                if (_cts == null || _cts.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(async () =>
                //await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    //Debug.WriteLine("Calling DroppedFiles in ViewModel @ProcessFiles()");

                    await _mainViewModel.DroppedFilesAsync(droppedImages, singleSelectedOriginalFile);
                });
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("OperationCanceledException @ProcessFiles()");
            }
            catch (ObjectDisposedException)
            {
                Debug.WriteLine("ObjectDisposedException @ProcessFiles()");
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    _mainViewModel.IsWorking = false;
                });

                Debug.WriteLine(ex);
                App.AppendErrorLog("DispatcherUnhandledException", ex.ToString());
                App.SaveErrorLog();
            }
        },_cts?.Token ?? CancellationToken.None);
    }

    private void Window_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        this.WindowState = (this.WindowState == WindowState.FullScreen) ? WindowState.Normal : WindowState.FullScreen;
    }

    private void SetWindowStateFullScreen()
    {
        //Debug.WriteLine("SetWindowStateFullScreen()");
        _mainViewModel.IsQueueListBoxVisible = false;
        _mainViewModel.IsFilePathPopupVisible = false;

        //this.WindowState = WindowState.FullScreen;

        //this.Cursor = new Cursor(StandardCursorType.None);
        if (_timerPointerCursorHide.IsEnabled)
        {
            _timerPointerCursorHide.Stop();
        }
        _timerPointerCursorHide.Start();

        if (_mainViewModel.IsSlideshowOn)
        {
            StartSleepInhibitor();
        }
    }

    private void SetWindowStateNormal()
    {
        //Debug.WriteLine("SetWindowStateNormal()");

        this.Cursor = _mainViewModel.IsWorking ? new Cursor(StandardCursorType.AppStarting) : Cursor.Default;

        //this.WindowState = WindowState.Normal;

        if (_timerPointerCursorHide.IsEnabled)
        {
            _timerPointerCursorHide.Stop();
        }

        if (_mainViewModel.IsSlideshowOn)
        {
            StopSleepInhibitor();
        }

        // restore visibility
        if (_mainViewModel.IsViewImageListOn)
        {
            // queue count more than one, show image list.
            _mainViewModel.IsQueueListBoxVisible = _mainViewModel.Queue.Count != 1;
        }
        // restore visibility
        if (_mainViewModel.IsViewFilePathPopupOn)
        {
            if (_mainViewModel.Queue.Count > 0)
            {
                _mainViewModel.IsFilePathPopupVisible = true;
            }
            else
            {
                _mainViewModel.IsFilePathPopupVisible = false;
            }
        }
    }

    private void StartSleepInhibitor()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            //Debug.WriteLine("SetThreadExecutionState set @StartSleepInhibitor()");
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (_LinuxSleepInhibitorProcess is not null)
                {
                    return;
                }

                /* 
                // This "systemd-inhibit" is ignored by Gnome because it is too low level.
                var startInfo = new ProcessStartInfo
                {
                    FileName = "systemd-inhibit",
                    Arguments = "--what=idle:sleep --who=ImageViewerX --mode=block sleep infinity",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Debug.WriteLine("systemd-inhibit started @StartSleepInhibitor()");
                _LinuxSleepInhibitorProcess = Process.Start(startInfo);
                */

                // Gnome only gnome-session-inhibit.
                // TODO: Check the XDG_CURRENT_DESKTOP environment variable.
                var startInfo = new ProcessStartInfo
                {
                    FileName = "gnome-session-inhibit",
                    // --inhibit-only: Prevents the screen from blanking without needing to wrap another command
                    // --reason: Provides a human-readable explanation in the system inhibitor list
                    // --inhibit idle: Specifically targets the "screen blanking" behavior
                    Arguments = "--inhibit idle --reason \"Viewing Images\" --inhibit-only",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Debug.WriteLine("gnome-session-inhibit started @StartSleepInhibitor");
                _LinuxSleepInhibitorProcess = Process.Start(startInfo);

                // Or use DBus
                /*
                //Interface
                using Tmds.DBus;

                [DBusInterface("org.freedesktop.ScreenSaver")]
                public interface IScreenSaver : IDBusObject
                {
                    // Returns a cookie (uint) used to UnInhibit later
                    Task<uint> InhibitAsync(string appName, string reason);
                    Task UnInhibitAsync(uint cookie);
                }
                //Ussage
                using System;
                using System.Threading.Tasks;
                using Tmds.DBus;

                class Program
                {
                    static async Task Main(string[] args)
                    {
                        // 1. Create a connection to the Session Bus
                        using var connection = new Connection(Address.Session);
                        await connection.ConnectAsync();

                        // 2. Create a proxy for the ScreenSaver service
                        var screenSaver = connection.CreateProxy<IScreenSaver>(
                            "org.freedesktop.ScreenSaver", 
                            "/org/freedesktop/ScreenSaver"
                        );

                        try
                        {
                            // 3. Request Inhibition
                            Console.WriteLine("Inhibiting display sleep...");
                            uint cookie = await screenSaver.InhibitAsync("MyDotNetApp", "Running a critical task");

                            // Your application logic here
                            await Task.Delay(TimeSpan.FromMinutes(5)); 

                            // 4. Release Inhibition
                            Console.WriteLine("Releasing inhibition.");
                            await screenSaver.UnInhibitAsync(cookie);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                        }
                    }
                }
                */
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception: {ex.Message} @StartSleepInhibitor()");
            }
        }
    }

    private void StopSleepInhibitor()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // TODO: check if already ES_CONTINUOUS. If so, return immediately.

            //Debug.WriteLine("SetThreadExecutionState off @StopSleepInhibitor()");
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (_LinuxSleepInhibitorProcess is not null && !_LinuxSleepInhibitorProcess.HasExited)
            {
                Debug.WriteLine("_LinuxSleepInhibitorProcess stoped @StopSleepInhibitor()");
                _LinuxSleepInhibitorProcess.Kill();
                _LinuxSleepInhibitorProcess.Dispose();
                _LinuxSleepInhibitorProcess = null;
            }
            else
            {
                Debug.WriteLine("_LinuxSleepInhibitorProcess is already exited @StopSleepInhibitor");
            }
        }
    }

    private async void Window_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            if (WindowState == WindowState.FullScreen)
            {
                this.WindowState = WindowState.Normal;

                e.Handled = true;
            }
        }
        else if (e.Key == Avalonia.Input.Key.Space)
        {
            // handled at InitKeyBindigs().

            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                // Alt + Space pressed.
                Debug.WriteLine("Window_KeyDown: Alt + Space pressed. Ignoring for play/pause to allow system menu access.");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // not working. handled this at KeyDown to allow system menu access. Do nothing here.
                    //if (GetTopLevel(this)?.TryGetPlatformHandle() is not { } handle) return;
                    //NativeMethods.ShowSystemWindowMenu(handle.Handle);
                    e.Handled = true;
                    return;
                }
            }
        }
        else if (e.Key == Avalonia.Input.Key.Right)
        {
            await _mainViewModel.NextKeyPressedAsync();

            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Left)
        {
            await _mainViewModel.PrevKeyPressedAsync();

            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.F)
        {
            ToggleFullScreen();

            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Tab)
        {
            //e.Handled = true;
        }
        else if (e.PhysicalKey == PhysicalKey.ContextMenu)
        {
            var flyout = FlyoutBase.GetAttachedFlyout(this);

            if (flyout is MenuFlyout menuFlyout)
            {
                menuFlyout.Placement = PlacementMode.Center;
                flyout?.ShowAt(this);
            }

            e.Handled = true;
        }
    }

    private void Window_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            if (WindowState == WindowState.FullScreen)
            {
                e.Handled = true;
            }
        }
        else if (e.Key == Avalonia.Input.Key.Space)
        {
            if (this.DataContext is MainViewModel)
            {
                // handled at InitKeyBindigs().

                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    // Alt + Space pressed.
                    Debug.WriteLine("Window_KeyUp: Alt + Space pressed. Ignoring for play/pause to allow system menu access.");

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // KeyUp works, keyDown doesn't.
                        if (GetTopLevel(this)?.TryGetPlatformHandle() is not { } handle) return;
                        NativeMethods.ShowSystemWindowMenu(handle.Handle);
                        e.Handled = true;
                        return;
                    }
                }
            }
        }
        else if (e.Key == Key.Right)
        {
            if (this.DataContext is MainViewModel)
            {
                //vm.NextKeyPressed();

                e.Handled = true;
            }
        }
        else if (e.Key == Key.Left)
        {
            if (this.DataContext is MainViewModel)
            {
                //vm.PrevKeyPressed();

                e.Handled = true;
            }
        }
        else if (e.Key == Key.F)
        {
            if (this.DataContext is MainViewModel)
            {
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Tab)
        {
            //e.Handled = true;
        }
        else if (e.PhysicalKey == PhysicalKey.ContextMenu)
        {
            e.Handled = true;
        }
    }

    private async void Window_PointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0) // Scroll up
        {
            await _mainViewModel.PrevKeyPressedAsync();
        }
        else if (e.Delta.Y < 0) // Scroll down
        {
            //
            await _mainViewModel.NextKeyPressedAsync();
        }

        e.Handled = true;
    }

    private void ListBox_PointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void ListBox_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // Not always works. Use OnItemPointerPressed instead.
        /*
        Debug.WriteLine("ListBox_Tapped_1");
        e.Handled = true;

        if (sender is not ListBox lb)
        {
            Debug.WriteLine("ListBox_Tapped_2");
            return;
        }

        var element = e.Source as Visual;

        var listBoxItem = element?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem == null)
        {
            Debug.WriteLine("ListBox_Tapped_3");
            return;
        }
        var dataItem = listBoxItem.DataContext;
        if (dataItem == null)
        {
            Debug.WriteLine("ListBox_Tapped_4");
            return;
        }
        if (dataItem is not ImageInfo item)
        {
            Debug.WriteLine("ListBox_Tapped_5");
            return;
        }

        Debug.WriteLine("ListBox_Tapped_6");
        lb.SelectedItem = item;

        _ = _mainViewModel.ListBoxItemSelected(item);
        */
    }

    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        //Debug.WriteLine($"OnItemPointerPressed sender is {sender}, e.Source is {e.Source}");
        //OnItemPointerPressed sender is Avalonia.Controls.ListBox, e.Source is Avalonia.Controls.Image

        if (sender is not ListBox lb)
        {
            return;
        }

        if (e.Source is not Control)
        {
            return;
        }

        var element = e.Source as Control;//Visual;

        var listBoxItem = element?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem == null)
        {
            return;
        }
        var dataItem = listBoxItem.DataContext;
        if (dataItem == null)
        {
            return;
        }
        if (dataItem is not ImageInfo item)
        {
            return;
        }

        lb.SelectedItem = item;

        await _mainViewModel.ListBoxItemSelectedAsync(item);

    }

    private void ListBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            this.Focus();
            e.Handled = true;
        }
        /*
        else if (e.Key == Key.Right)
        {
            _mainViewModel.NextKeyPressed();

            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            _mainViewModel.PrevKeyPressed();

            e.Handled = true;
        }
        */
    }

    private void ListBox_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
        }
        /*
        else if (e.Key == Key.Right)
        {
            //vm.NextKeyPressed();

            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            //vm.PrevKeyPressed();

            e.Handled = true;
        }
        */
    }

    private void ListBox_GotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        this.ListBoxBackgroundLayerBorder.IsVisible = true;
    }

    private void ListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        e.Handled = true;
    }

    private void ListBox_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        this.ListBoxBackgroundLayerBorder.IsVisible = false;
    }

    private void ListBox_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }
        //var listBox = sender;

        if (listBox.Tag != null)
        {
            //Debug.WriteLine("(listBox.Tag != null) @ListBoxStackPanelBehaviors");
            return;
        }

        var scrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        var virtualPanel = listBox.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();

        if ((scrollViewer != null) && (virtualPanel != null))
        {
            listBox.Tag = "ListBox_Loaded_ListBoxStackPanelBehaviors";

            // Subscribe to the scroll event to update the visible items.
            scrollViewer.ScrollChanged += (s, args) => UpdateVisibleItems(scrollViewer, virtualPanel);
            // .. size changed event too.
            scrollViewer.SizeChanged += (s, args) => UpdateVisibleItems(scrollViewer, virtualPanel);

            scrollViewer.DetachedFromVisualTree += (s, e) =>
            {
                scrollViewer.ScrollChanged -= (s, args) => UpdateVisibleItems(scrollViewer, virtualPanel);
                scrollViewer.SizeChanged -= (s, args) => UpdateVisibleItems(scrollViewer, virtualPanel);
            };

            // Call it once initially to set the property.
            UpdateVisibleItems(scrollViewer, virtualPanel);
        }
    }

    private void UpdateVisibleItems(ScrollViewer scrollViewer, VirtualizingStackPanel virtualPanel)
    {
        if (this.DataContext is not MainViewModel vm)
        {
            return;
        }

        //var _scrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer == null) return;

        //var _virtualPanel = listBox.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
        if (virtualPanel == null) return;

        var viewportRect = new Rect(new Point(scrollViewer.Offset.X, scrollViewer.Offset.Y), scrollViewer.Viewport);

        var visibleObjects = virtualPanel.Children.Where(child => child.Bounds.Intersects(viewportRect)).ToList();

        var visibleItems = new List<object>();

        foreach (var itemContainer in visibleObjects)
        {
            var dataItem = itemContainer.DataContext;
            if (dataItem != null)
            {
                visibleItems.Add(dataItem);
            }
        }

        if (visibleItems.Count <= 0)
        {
            return;
        }

        // Set the new value of the attached property.
        //listBox.SetValue(VisibleItemsProperty, visibleItems);
        vm.VisibleItemsImageInfo = visibleItems;
    }

    private async void UpdateQueueListBoxImages(int selectedIndex)
    {
        if (this.QueueListBox.IsVisible == false)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if ((selectedIndex > (vm.Queue.Count - 1)) && (selectedIndex < 0))
        {
            Debug.WriteLine("if ((selectedIndex > c) && (selectedIndex < 0))");
            return;
        }

        await Task.Yield();
        //await Task.Delay(300); // Need to wait for UI to update
        Dispatcher.UIThread.Post(() =>
        {
            if (this.QueueListBox is not ListBox lb)
            {
                return;
            }

            if (lb.ItemCount == 0)
            {
                return;
            }

            if (lb.ItemCount < (selectedIndex + 1))
            {
                Debug.WriteLine($"if ({lb.ItemCount} < ({selectedIndex + 1}))");
                return;
            }

            //lb.ScrollIntoView(selectedIndex);

            if (DataContext is not MainViewModel)
            {
                return;
            }

            var scrollViewer = lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer is null)
            {
                return;
            }
            var virtualPanel = lb.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
            if (virtualPanel is null)
            {
                return;
            }

            var test = vm?.Queue[selectedIndex];
            if (test != null)
            {
                lb.SelectedItem = test;

                // 
                lb.ScrollIntoView(selectedIndex);
            }

            //

            // Center selected.
            if (lb.SelectedItem is not null)
            {
                if (lb.ContainerFromItem(lb.SelectedItem) is ListBoxItem item)
                {
                    BringIntoViewCenter(item, scrollViewer);
                }
            }

            UpdateVisibleItems(scrollViewer, virtualPanel);

        }, DispatcherPriority.Loaded);//.Background//.Loaded//.Default
    }

    private async void UpdateQueueListBoxImages()
    {
        await Task.Yield();
        //await Task.Delay(800); // Need to wait for UI to update

        Dispatcher.UIThread.Post(() =>
        {
            if (this.QueueListBox is not ListBox lb)
            {
                return;
            }

            if (DataContext is not MainViewModel vm)
            {
                return;
            }
            var item = lb.SelectedItem;
            if (item is not ImageInfo img)
            {
                return;
            }

            var ind = vm.Queue.IndexOf(img);
            if (ind < 0)
            {
                return;
            }

            lb.ScrollIntoView(ind);

            var scrollViewer = lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer is null)
            {
                return;
            }

            var virtualPanel = lb.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
            if (virtualPanel is null)
            {
                return;
            }

            // Center selected.
            if (lb.SelectedItem is not null)
            {
                if (lb.ContainerFromItem(lb.SelectedItem) is ListBoxItem listitem)
                {
                    BringIntoViewCenter(listitem, scrollViewer);
                }
            }

            UpdateVisibleItems(scrollViewer, virtualPanel);

        }, DispatcherPriority.Loaded);////.Default//.Background
    }

    private async void Button_FilePick_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenFilePickerAsync();
        this.Activate();
        this.Focus();
    }

    public async Task OpenFilePickerAsync()
    {
        if (_mainViewModel.IsWorking)
        {
            // Already processing. 
            return;
        }

        // TODO: Make/Move this to DialogService.

        // Get the IStorageProvider for the current window
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storageProvider == null) return; // Storage provider not available on this platform

        var options = new FilePickerOpenOptions
        {
            Title = "Select Image Files",
            AllowMultiple = true, // Set to true to allow multiple selections
            /*
            FileTypeFilter = new[]
            {
                FilePickerFileTypes.ImageAll // All image types
            }
            */
            FileTypeFilter =
            [
                new FilePickerFileType("All Supported Images")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.gif", "*.png", "*.webp", "*.bmp"],
                    AppleUniformTypeIdentifiers = ["public.jpg", "public.jpeg", "public.gif", "public.png", "public.webp", "public.bmp"],
                    MimeTypes = ["image/jpg", "image/jpeg", "image/gif", "image/png", "image/webp", "image/bmp"]
                },
                FilePickerFileTypes.ImagePng,
                FilePickerFileTypes.ImageJpg,
                FilePickerFileTypes.ImageWebp,
                new FilePickerFileType("Other Images")
                {
                    Patterns = ["*.gif", "*.bmp"],
                    AppleUniformTypeIdentifiers = ["public.gif", "public.bmp"],
                    MimeTypes = ["image/gif", "image/bmp"]
                }
                /*
                new FilePickerFileType("All Supported Images")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.gif", "*.png", "*.webp", "*.bmp", "*.ico", "*.avif", "*.jxl"],
                    AppleUniformTypeIdentifiers = ["public.jpg", "public.jpeg", "public.gif", "public.png", "public.webp", "public.bmp", "public.ico", "public.avif", "public.jxl"],
                    MimeTypes = ["image/jpg", "image/jpeg", "image/gif", "image/png", "image/webp", "image/bmp", "image/ico", "image/avif", "image/jxl"]
                },
                FilePickerFileTypes.ImagePng,
                FilePickerFileTypes.ImageJpg,
                FilePickerFileTypes.ImageWebp,
                new FilePickerFileType("Other Images")
                {
                    Patterns = ["*.gif", "*.bmp", "image/ico", "image/avif", "image/jxl"],
                    AppleUniformTypeIdentifiers = ["public.gif", "public.bmp", "public.ico", "public.avif", "public.jxl"],
                    MimeTypes = ["image/gif", "image/bmp", "image/ico", "image/avif", "image/jxl"]
                }
                */
            ]
        };

        if (!string.IsNullOrEmpty(_lastOpenedDirectory))
        {
            var folderUri = new Uri(_lastOpenedDirectory);

            var suggestedFolder = await storageProvider.TryGetFolderFromPathAsync(folderUri);

            options.SuggestedStartLocation = suggestedFolder;
        }

        var files = await storageProvider.OpenFilePickerAsync(options);

        if (files != null && files.Any())
        {
            var droppedFiles = new List<string>();
            foreach (var file in files)
            {
                var filePath = file.TryGetLocalPath();
                if (filePath != null)
                {
                    droppedFiles.Add(filePath);
                }
            }

            if (droppedFiles.Count > 0)
            {
                string? parentFolderPath = System.IO.Path.GetDirectoryName(droppedFiles[0]);
                if (parentFolderPath is not null)
                {
                    _lastOpenedDirectory = parentFolderPath;
                }

                //await ProcessFiles(droppedFiles);
                await Task.Run(() => ProcessFiles(droppedFiles));
            }
        }
    }

    private async void Button_FolderPick_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SelectFolderAsync();
        this.Activate();
        this.Focus();
    }

    public async Task SelectFolderAsync()
    {
        if (_mainViewModel.IsWorking)
        {
            // Already processing. 
            return;
        }

        // Get the IStorageProvider for the current window
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storageProvider == null) return; // Storage provider not available on this platform

        // Configure the options for the folder picker
        var options = new FolderPickerOpenOptions
        {
            Title = "Select Folders",
            AllowMultiple = true // Set to true to allow multiple folders
        };

        if (!string.IsNullOrEmpty(_lastOpenedDirectory))
        {
            var folderUri = new Uri(_lastOpenedDirectory);

            var suggestedFolder = await storageProvider.TryGetFolderFromPathAsync(folderUri);

            options.SuggestedStartLocation = suggestedFolder;
        }

        // Display the folder selection dialog
        var folders = await storageProvider.OpenFolderPickerAsync(options);

        // Process the result
        if (folders.Any())
        {
            var droppedFiles = new List<string>();

            foreach (var item in folders)
            {
                var filePath = item.TryGetLocalPath();
                if (filePath is not null)
                {
                    droppedFiles.Add(filePath);
                }
            }

            if (droppedFiles.Count > 0)
            {
                _lastOpenedDirectory = droppedFiles[0];

                //await ProcessFiles(droppedFiles);
                await Task.Run(() => ProcessFiles(droppedFiles));
            }
        }
    }

    private void Button1_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void Button2_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private static void BringIntoViewCenter(Control control, ScrollViewer scrollViewer)
    {
        /*
        var scrollViewer = control.GetVisualAncestors()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        */

        if (scrollViewer == null)
        {
            Debug.WriteLine("if (scrollViewer == null)");
            return;
        }

        var bounds = control.Bounds;
        var scrollViewerBounds = scrollViewer.Bounds;

        // Calculate the center of the control and the ScrollViewer's viewport
        var controlCenterY = bounds.Y + bounds.Height / 2;
        var viewportCenterY = scrollViewer.Offset.Y + scrollViewerBounds.Height / 2;

        // Calculate the difference between the centers
        var diff = controlCenterY - viewportCenterY;

        // Set the new scroll offset
        var newOffset = new Vector(scrollViewer.Offset.X, scrollViewer.Offset.Y + diff);
        scrollViewer.Offset = newOffset;
    }

    private void Window_Activated(object? sender, System.EventArgs e)
    {
        // TODO: dim caption button color only when deactivated.
        //AppTitleBarIcon.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.4 : 0.8;

        //this.Opacity = 1;

        // Restore Popup.
        if (_mainViewModel.IsViewFilePathPopupOn)
        {
            if (_mainViewModel.CanToggleViewFilePath())
            {
                if ((_mainViewModel.Queue.Count > 0) && (!_mainViewModel.IsFullscreen))
                {
                    this.PopupFilePath.IsOpen = true;
                }
            }
        }

        // Save the current window size and position when activated.
        if (this.WindowState == WindowState.Normal)
        {
            _winRestoreHeight = (int)this.Height;
            _winRestoreWidth = (int)this.Width;
            _winRestoreTop = this.Position.Y;
            _winRestoreLeft = this.Position.X;
        }
    }

    private void Window_Deactivated(object? sender, System.EventArgs e)
    {
        // TODO: dim caption button color only when deactivated.
        //this.Opacity = 0.8;

        // Popup stays optop, so hide it.
        this.PopupFilePath.IsOpen = false;
    }

    private void ClientAreaGrid_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _mainViewModel.ClientAreaSizeChanged(this.ClientAreaGrid.Bounds.Width, this.ClientAreaGrid.Bounds.Height);
    }

    // EnableBlurBehind: Use NativeMemory.Alloc, instead of Marshal.AllocHGlobal
    public static unsafe void EnableBlurBehind(IntPtr handle)
    {
        // The pointer for the unmanaged memory.
        void* accentPtr = null;

        try
        {
            uint darkGrayTint = 0;

            var accent = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0,
                GradientColor = darkGrayTint,
                AnimationId = 0
            };

            // Get the size of the structure.
            nuint sizeOfAccent = (nuint)sizeof(NativeMethods.AccentPolicy);

            // Allocate and zero the unmanaged memory.
            accentPtr = NativeMemory.AllocZeroed(sizeOfAccent);

            // Copy the managed struct to the unmanaged memory.
            Unsafe.Copy(accentPtr, ref accent);

            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = (int)sizeOfAccent,
                // Convert the void* pointer to an IntPtr for the interop call.
                Data = (IntPtr)accentPtr
            };

            NativeMethods.SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            // Free the unmanaged memory if it was successfully allocated.
            if (accentPtr != null)
            {
                NativeMemory.Free(accentPtr);
            }
        }
    }

    private void MenuItemQuit_Click(object? sender, RoutedEventArgs e)
    {
        var flyout = FlyoutBase.GetAttachedFlyout(this);
        // If the flyout is open, app freezes in linux when trying to close. So close the flyout first.
        flyout?.IsOpen = false;

        Close();
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (this.WindowState == WindowState.Normal)
        {
            _winRestoreHeight = (int)this.Height;
            _winRestoreWidth = (int)this.Width;
            _winRestoreTop = this.Position.Y;
            _winRestoreLeft = this.Position.X;
        }
    }


    // EnableBlurBehind: Marshal.AllocHGlobal way.
    /*
    private static void EnableBlurBehind(IntPtr handle)
    {
        // Hexadecimal BGR color value
        uint darkGrayTint = 0;//0 default //0xAA222222; 67% transparent, dark gray // 0x99FF99CC; 60% transparent, light blue

        var accent = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, //ACCENT_ENABLE_BLURBEHIND,
            AccentFlags = 0,
            GradientColor = darkGrayTint,//0,
            AnimationId = 0
        };

        var accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new NativeMethods.WindowCompositionAttributeData
        {
            Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
            SizeOfData = Marshal.SizeOf(accent),
            Data = accentPtr
        };

        NativeMethods.SetWindowCompositionAttribute(handle, ref data);

        Marshal.FreeHGlobal(accentPtr);
    }
    */
}

public static partial class NativeMethods
{
    #region == Blur Background (SetWindowCompositionAttribute) ==

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    public enum WindowCompositionAttribute
    {
        // ... other attributes ...
        WCA_ACCENT_POLICY = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    public enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4, // Windows 10 v1803+
        ACCENT_INVALID_STATE = 5
    }

    #endregion

    #region == System Window Menu (ShowSystemWindowMenu) ==

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;

    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_ENABLED = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;

    private const uint SC_RESTORE = 0xF120;
    private const uint SC_MOVE = 0xF010;
    private const uint SC_SIZE = 0xF000;
    private const uint SC_MINIMIZE = 0xF020;
    private const uint SC_MAXIMIZE = 0xF030;
    private const uint SC_CLOSE = 0xF060;

    //[DllImport("user32.dll", SetLastError = true)]
    //public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    private static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Helper method to show the system window menu (aka the Control Menu) for a given window handle
    public static void ShowSystemWindowMenu(IntPtr hWnd)
    {
        //DefWindowProc(hWnd, WM_SYSCOMMAND, (IntPtr)SC_KEYMENU, (IntPtr)32);

        IntPtr sysMenu = GetSystemMenu(hWnd, false);
        if (sysMenu != IntPtr.Zero)
        {
            // Close is always active
            EnableMenuItem(sysMenu, SC_CLOSE, MF_BYCOMMAND | MF_ENABLED);

            // FORCE WINDOWS TO RE-EVALUATE/ENABLE SYSTEM MENU ITEMS
            // wParam is the menu handle; lParam contains flags (0 is standard)
            //SendMessage(hWnd, WM_INITMENUPOPUP, sysMenu, IntPtr.Zero);

            DefWindowProc(hWnd, WM_SYSCOMMAND, (IntPtr)SC_KEYMENU, (IntPtr)32);
        }
    }

    public static void ShowNativeSystemMenu(IntPtr hWnd, PixelPoint screenPoint, WindowState windowState, bool canResize)
    {
        // Fetch the window's live System Menu handle
        IntPtr sysMenu = GetSystemMenu(hWnd, false);
        if (sysMenu == IntPtr.Zero)
        {
            return;
        }

        // Explicitly force the Close menu option to become enabled
        // Close is always active
        EnableMenuItem(sysMenu, SC_CLOSE, MF_BYCOMMAND | MF_ENABLED);

        // Adjust actions based on WindowState
        if (windowState == WindowState.Maximized)
        {
            EnableMenuItem(sysMenu, SC_RESTORE, MF_BYCOMMAND | MF_ENABLED);
            EnableMenuItem(sysMenu, SC_MAXIMIZE, MF_BYCOMMAND | MF_GRAYED);
            EnableMenuItem(sysMenu, SC_MOVE, MF_BYCOMMAND | MF_GRAYED);
            EnableMenuItem(sysMenu, SC_SIZE, MF_BYCOMMAND | MF_GRAYED);
        }
        else if (windowState == WindowState.Minimized)
        {
            EnableMenuItem(sysMenu, SC_RESTORE, MF_BYCOMMAND | MF_ENABLED);
            EnableMenuItem(sysMenu, SC_MINIMIZE, MF_BYCOMMAND | MF_GRAYED);
        }
        else // Normal / Restored
        {
            EnableMenuItem(sysMenu, SC_RESTORE, MF_BYCOMMAND | MF_GRAYED);
            EnableMenuItem(sysMenu, SC_MAXIMIZE, canResize ? (MF_BYCOMMAND | MF_ENABLED) : (MF_BYCOMMAND | MF_GRAYED));
            EnableMenuItem(sysMenu, SC_MINIMIZE, MF_BYCOMMAND | MF_ENABLED);
            EnableMenuItem(sysMenu, SC_MOVE, MF_BYCOMMAND | MF_ENABLED);
            EnableMenuItem(sysMenu, SC_SIZE, canResize ? (MF_BYCOMMAND | MF_ENABLED) : (MF_BYCOMMAND | MF_GRAYED));
        }

        // Display the native menu at the cursor context.
        // TPM_RETURNCMD forces TrackPopupMenu to return the ID of the clicked menu item.
        int command = TrackPopupMenu(sysMenu, TPM_RETURNCMD, screenPoint.X, screenPoint.Y, 0, hWnd, IntPtr.Zero);

        if (command > 0)
        {
            // Post the selected command back to the window loop so Windows handles it (Minimize, Maximize, Close, Move, etc.)
            PostMessage(hWnd, WM_SYSCOMMAND, (IntPtr)command, IntPtr.Zero);
        }

        //e.Handled = true;
    }

    #endregion

    #region == Prevent from sleep (SetThreadExecutionState) ==

    // Define the execution state flags
    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SetThreadExecutionState(uint esFlags);

    #endregion

}

// Not used anymore.
public partial class DpiHelper
{
    // Define the P/Invoke signature for GetDpiForWindow
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetDpiForWindow(IntPtr hWnd);

    // Baseline DPI value (100% scale)
    private const uint DpiBase = 96;

    /// <summary>
    /// Calculates the scaling factor for a given window handle.
    /// </summary>
    /// <param name="hWnd">The handle to the window.</param>
    /// <returns>The scaling factor (e.g., 1.0 for 100%, 1.25 for 125%).</returns>
    public static double GetWindowScalingFactor(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            // Handle cases where there is no window (e.g., pure console app)
            // Fallback to system metrics if necessary, or return a default.
            // Note: This fallback is less accurate for per-monitor scaling.
            return 1;
        }

        uint dpi = GetDpiForWindow(hWnd);

        if (dpi == 0)
        {
            // Fallback if the API call fails
            return 1;
        }

        return (double)dpi / DpiBase;
    }


}
