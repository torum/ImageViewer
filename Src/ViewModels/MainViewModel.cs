using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services.Contracts;
using ImageViewer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable IDE0028,IDE0001

namespace ImageViewer.ViewModels;

internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    #region == Private ==

    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timerSlideshow;
    private int _queueIndex;
    private string _currentFile = string.Empty;
    private List<ImageInfo> _originalQueue = [];

    private Vector _displayDpi = new(96.0, 96.0);
    private int _hardwareDisplayWidth = 3840;
    private int _hardwareDisplayHeight = 2160;

    // Font info used for culculating displaying text width.
    private FontFamily TextFontFamily { get; } = FontFamily.Default;
    private double TextFontSize { get; } = 14;
    private FontWeight TextFontWeight { get; } = FontWeight.Regular;
    private FontStyle TextFontStyle { get; } = FontStyle.Normal;

    private readonly IConfigurationService _configurationService;
    #endregion

    #region == Public ==

    // TODO: Make user editable.
    // Other exts that Skia supports. Ico,Wbmp,Pkm,Ktx,Astc,Dng,Heif, ".avif"
    //private readonly string[] _validExtensions = [".jpg", ".jpeg", ".gif", ".png", ".webp", ".bmp", ".avif", ".jxl"]; //
    public static readonly HashSet<string> ValidExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".gif", ".png", ".webp", ".bmp" };//, ".avif"

    #endregion

    #region == Events ==

    public event EventHandler<int>? QueueHasBeenChanged;
    public event EventHandler? TransitionsHasBeenChanged;
    public event EventHandler? SlideshowStatusChanged;
    public event EventHandler? QueueLoaded;
    public event EventHandler? ToggleFullscreenState;
    public event EventHandler? HideMenuFlyout;
    public event EventHandler<long>? SlideshowIntervalChanged;
    public event EventHandler<bool>? WorkingStateChanged;

    #endregion

    public MainViewModel(IConfigurationService configurationService)
    {
        _configurationService = configurationService;

        var config = _configurationService.CurrentConfiguration;

        IsShuffleOn = config.IsShuffleOn;
        IsRepeatOn = config.IsRepeatOn;
        IsOverrideSystemDpiScalingFactorOn = config.IsOverrideSystemDpiScalingFactorOn;
        IsViewImageListOn = config.IsViewImageListOn;
        IsViewFilePathPopupOn = config.IsViewFilePathPopupOn;
        _slideshowTimerInterval = config.SlideshowTimerInterval; // _timerSlideshow isn't initialized yet, so set the backing field directly.
        IsStretchInOn = config.IsStretchInOn;
        IsStretchOutOn = config.IsStretchOutOn;
        IsEffectFadeInAndOutOn = config.IsEffectFadeInAndOutOn;
        IsEffectPageSlideOn = config.IsEffectPageSlideOn;
        IsNoEffectsOn = config.IsNoEffectsOn;
        IsEffectCrossfadeOn = config.IsEffectCrossfadeOn;

        // Init Timer.
        _timerSlideshow = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(config.SlideshowTimerInterval)
        };
        _timerSlideshow.Tick += OnSlideshowTimerTick;
        SlideshowTimerInterval = config.SlideshowTimerInterval; // Fire OnSlideshowIntervalChanged.

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var screen = desktop.MainWindow?.Screens.ScreenFromWindow(desktop.MainWindow);
            SystemDpiScalingFactor = screen?.Scaling ?? 1.0;
            _displayDpi = new Vector(96.0 * SystemDpiScalingFactor, 96.0 * SystemDpiScalingFactor);
            _hardwareDisplayWidth = screen?.Bounds.Width ?? 3840;
            _hardwareDisplayHeight = screen?.Bounds.Height ?? 2160;
        }

#if DEBUG
        IsSaveLog = true;

#else
        IsSaveLog = false;
#endif
    }

    #region == Public Properties ==

    public double ClientAreaWidth
    {
        get;
        set
        {
            field = value;

            if ((DisplayImage is null) || string.IsNullOrEmpty(DisplayImage.ImageFilePath))
            {
                return;
            }

            // Window width is changed, update FilePath textblock width with MinimizeName.
            OnPropertyChanged(nameof(FileNameFullPath));
        }
    } = 550;

    public double ClientAreaHeight { get; set; } = 550;

    [ObservableProperty]
    public partial ObservableCollection<ImageInfo> Queue { get; set; } = [];

    [ObservableProperty]
    public partial ImageInfo? SelectedQueueImage { get; set; }

    [ObservableProperty]
    public partial bool IsTransitionReversed { get; set; } = false;

    public IEnumerable<object>? VisibleItemsImageInfo
    {
        get;
        set
        {
            field = value;

            if (field is null)
            {
                return;
            }

            var i = field.Count();
            if (i <= 0)
            {
                return;
            }

            // Don't await here. Fire and forget. No _ = either.
            //_ = Task.Run(() => GetPicturesAsync(field), _cts?.Token ?? CancellationToken.None);
            _ = GetPicturesAsync(field);

            //Debug.WriteLine($"@VisibleItemsImageInfo: {i} items, IsWorking: {IsWorking}, Queue.Count: {Queue.Count}");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNameFullPath))]
    public partial ImageInfo? DisplayImage { get; set; }

    public string FileNameFullPath
    {
        get
        {
            if (DisplayImage is not null)
            {
                if (string.IsNullOrEmpty(DisplayImage.ImageFilePath))
                {
                    return string.Empty;
                }

                return PathHelper.MinimizeName(DisplayImage.ImageFilePath, TextFontFamily, TextFontSize, TextFontStyle, TextFontWeight, ClientAreaWidth, 450);
            }
            else
            {
                return string.Empty;
            }
        }
    }

    [ObservableProperty]
    public partial double SystemDpiScalingFactor { get; set; } = 1.0;

    // Reflects actual state.
    private bool _isFullscreen;
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (_isFullscreen == value)
                return;

            _isFullscreen = value;
            OnPropertyChanged();

            HideMenuFlyout?.Invoke(this, EventArgs.Empty);

            ToggleViewFilePathCommand.NotifyCanExecuteChanged();
            ToggleViewImageListCommand.NotifyCanExecuteChanged();

            // Update opts control
            IsFullscreenOn = _isFullscreen;
        }
    }

    // When busy, it's busy processing file in/out the background thread.
    //[GenerateTypedChangePropertyAction(UseDispatcher = true)]
    public bool IsWorking
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;

            if (!_isFullscreen)
            {
                // Only non fullscreen mode since IsWorking is binding to busy cursor.
                OnPropertyChanged();

                Dispatcher.UIThread.Post(() =>
                {
                    WorkingStateChanged?.Invoke(this, value);
                }, DispatcherPriority.Input);
            }
        }
    }

    // internal state.
    public bool IsQueueListBoxVisible
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;

            OnPropertyChanged();

            if (field)
            {
                QueueHasBeenChanged?.Invoke(this, _queueIndex - 1);
            }
        }
    } = true;

    // internal state.
    [ObservableProperty]
    public partial bool IsFilePathPopupVisible { get; set; }

    // Internal state only
    [ObservableProperty]
    public partial bool IsStretchNone { get; set; }

    public bool IsSaveLog
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            // TODO: do we need this?
            OnPropertyChanged();
        }
    }

    #region == User Opts ==

    // Both user opt and internal state.
    private long _slideshowTimerInterval = 4;
    public long SlideshowTimerInterval
    {
        get => _slideshowTimerInterval;
        set
        {
            if (_slideshowTimerInterval == value)
                return;

            _slideshowTimerInterval = value;
            OnPropertyChanged();

            if (_timerSlideshow.Interval is { })
            {
                _timerSlideshow.Interval = TimeSpan.FromSeconds(_slideshowTimerInterval);
                SlideshowIntervalChanged?.Invoke(this, _slideshowTimerInterval);
            }
        }
    }

    // Both user opt and internal state.
    public bool IsNoEffectsOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataNoEffectsIcon));

            TransitionsHasBeenChanged?.Invoke(this, EventArgs.Empty);

            ToggleCrossfadeCommand.NotifyCanExecuteChanged();
        }
    } = false;

    // Both user opt and internal state.
    public bool IsEffectCrossfadeOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataCrossfadeIcon));

            TransitionsHasBeenChanged?.Invoke(this, EventArgs.Empty);
        }
    } = true;

    // Both user opt and internal state.
    public bool IsEffectFadeInAndOutOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataFadeInFadeOutIcon));

            TransitionsHasBeenChanged?.Invoke(this, EventArgs.Empty);
        }
    } = true;

    // Both user opt and internal state.
    public bool IsEffectPageSlideOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataPageSlideIcon));

            TransitionsHasBeenChanged?.Invoke(this, EventArgs.Empty);
        }
    } = false;

    // Both user opt and internal state.
    private bool _isShuffleOn;
    public bool IsShuffleOn
    {
        get => _isShuffleOn;
        set
        {
            if (_isShuffleOn == value)
                return;

            _isShuffleOn = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(DataShuffleIcon));

            HideMenuFlyout?.Invoke(this, EventArgs.Empty);

            if (Queue.Count <= 0) return;
            IsWorking = true;

            // Slideshow Timer temp stop.
            if (_timerSlideshow.IsEnabled)
            {
                _timerSlideshow.Stop();
            }

            _queueIndex = 0;
            SelectedQueueImage = null;

            if (_isShuffleOn)
            {
                //Queue = new ObservableCollection<ImageInfo>(_originalQueue);
                Queue.Shuffle();
            }
            else
            {
                Queue = new ObservableCollection<ImageInfo>(_originalQueue);
            }

            if (!string.IsNullOrEmpty(_currentFile))
            {
                //Debug.WriteLine(_currentFile);
                var cur = Queue.FirstOrDefault(x => x.ImageFilePath == _currentFile);

                // clear here.
                //_currentFile = string.Empty;

                if (cur is not null)
                {
                    _queueIndex = Queue.IndexOf(cur);

                    //Debug.WriteLine(Queue[_queueIndex].ImageFilePath);
                }
            }

            //_currentFile = string.Empty;

            OnPropertyChanged(nameof(Queue));

            QueueHasBeenChanged?.Invoke(this, _queueIndex);

            _queueIndex++;

            IsWorking = false;

            // Slideshow timer restart.
            if (IsSlideshowOn)
            {
                _timerSlideshow.Start();
            }
        }
    }

    // Both user opt and internal state.
    public bool IsRepeatOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataRepeatIcon));

            HideMenuFlyout?.Invoke(this, EventArgs.Empty);
        }
    } = false;

    // Both user opt and internal state.
    public bool IsStayOnTop
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataStayOnTopIcon));

            HideMenuFlyout?.Invoke(this, EventArgs.Empty);
        }
    } = false;

    // Both user opt and internal state.
    public bool IsSlideshowOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataPlayPauseIcon));
            OnPropertyChanged(nameof(SlideshowStartStopString));

            SlideshowStatusChanged?.Invoke(this, EventArgs.Empty);
            HideMenuFlyout?.Invoke(this, EventArgs.Empty);
        }
    } = false;

    // Only user opt.
    public bool IsFullscreenOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataFullscreenOnIcon));

            //IsFullscreen = _isFullscreenOn;
        }
    } = false;

    // Only user opt.
    private bool _isViewFilePathPopupOn;
    public bool IsViewFilePathPopupOn
    {
        get => _isViewFilePathPopupOn;
        set
        {
            if (_isViewFilePathPopupOn == value)
                return;

            _isViewFilePathPopupOn = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(DataViewFilePathPopupOnIcon));

            //if (Queue.Count <= 1) return;
            if (_isFullscreen) return;

            IsFilePathPopupVisible = _isViewFilePathPopupOn;
        }
    }

    // Only user opt.
    private bool _isViewImageListOn = true;
    public bool IsViewImageListOn
    {
        get => _isViewImageListOn;
        set
        {
            if (_isViewImageListOn == value)
                return;

            _isViewImageListOn = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(DataViewImageListOnIcon));

            if (Queue.Count <= 1) return;
            if (_isFullscreen) return;

            IsQueueListBoxVisible = _isViewImageListOn;
        }
    }

    // Both user opt and internal state.
    public bool IsOverrideSystemDpiScalingFactorOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataSystemDpiScalingFactorOnIcon));

            if (Queue.Count > 0)
            {
                foreach (var item in Queue)
                {
                    /*
                    if (item.ImageSource is not null)
                    {
                        item.ImageSource = null;
                        item.ImageWidth = 0;
                        item.ImageHeight = 0;
                        item.IsAcquired = false;
                        item.IsLoading = false;
                    }
                    */
                    ReleaseImage(item);
                }

                // Reload image.
                _currentFile = string.Empty;

                //OnPropertyChanged(nameof(Queue));
                if ((_queueIndex - 1) >= 0)
                {
                    //QueueHasBeenChanged?.Invoke(this, _queueIndex -1);
                    _queueIndex--;
                }
                else
                {
                    //QueueHasBeenChanged?.Invoke(this, 0);
                    _queueIndex = 0;
                }

                _ = ShowAsync();
            }
        }
    } = true;

    //Stretch in 
    public bool IsStretchInOn
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataStretchInOnIcon));

            if ((IsStretchInOn == false) && (IsStretchOutOn == false))
            {
                IsStretchNone = true;
            }
            else
            {
                IsStretchNone = false;
            }

            UpdateDisplayImageMaxSizeAndStretchProperty();
        }
    } = true;

    //Stretch out
    public bool IsStretchOutOn
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DataStretchOutOnIcon));

            if ((IsStretchInOn == false) && (IsStretchOutOn == false))
            {
                IsStretchNone = true;
            }
            else
            {
                IsStretchNone = false;
            }

            UpdateDisplayImageMaxSizeAndStretchProperty();
        }
    } = false;

    #region == User opts menu icons == 

    // TODO: make this static resource or something.

    private const string Play = "M5.74514 3.06445C5.41183 2.87696 5 3.11781 5 3.50023V12.5005C5 12.8829 5.41182 13.1238 5.74512 12.9363L13.7454 8.43631C14.0852 8.24517 14.0852 7.75589 13.7454 7.56474L5.74514 3.06445ZM4 3.50023C4 2.35298 5.2355 1.63041 6.23541 2.19288L14.2357 6.69317C15.2551 7.26664 15.2551 8.73446 14.2356 9.3079L6.23537 13.8079C5.23546 14.3703 4 13.6477 4 12.5005V3.50023Z";
    private const string Pause = "M3.75 2C2.7835 2 2 2.7835 2 3.75V12.25C2 13.2165 2.7835 14 3.75 14H5.25C6.2165 14 7 13.2165 7 12.25V3.75C7 2.7835 6.2165 2 5.25 2H3.75ZM3 3.75C3 3.33579 3.33579 3 3.75 3H5.25C5.66421 3 6 3.33579 6 3.75V12.25C6 12.6642 5.66421 13 5.25 13H3.75C3.33579 13 3 12.6642 3 12.25V3.75ZM10.75 2C9.7835 2 9 2.7835 9 3.75V12.25C9 13.2165 9.7835 14 10.75 14H12.25C13.2165 14 14 13.2165 14 12.25V3.75C14 2.7835 13.2165 2 12.25 2H10.75ZM10 3.75C10 3.33579 10.3358 3 10.75 3H12.25C12.6642 3 13 3.33579 13 3.75V12.25C13 12.6642 12.6642 13 12.25 13H10.75C10.3358 13 10 12.6642 10 12.25V3.75Z";
    public string DataPlayPauseIcon => IsSlideshowOn ? Pause : Play;

    private const string ShuffleOn = "M12.3536 3.64645C12.1583 3.45118 11.8417 3.45118 11.6464 3.64645C11.4512 3.84171 11.4512 4.15829 11.6464 4.35355L12.2961 5.00321C9.97191 5.0767 8.51587 6.40057 7.20029 7.59672L7.16366 7.63003C5.77688 8.89074 4.53421 10 2.5 10C2.22386 10 2 10.2239 2 10.5C2 10.7761 2.22386 11 2.5 11C4.94373 11 6.44607 9.63403 7.79971 8.40327L7.83634 8.36997C9.17448 7.15347 10.3784 6.078 12.2888 6.00405L11.6464 6.64645C11.4512 6.84171 11.4512 7.15829 11.6464 7.35355C11.8417 7.54882 12.1583 7.54882 12.3536 7.35355L13.8536 5.85355C14.0488 5.65829 14.0488 5.34171 13.8536 5.14645L12.3536 3.64645ZM2.5 5C4.32898 5 5.63063 5.76516 6.73729 6.66612L6.49099 6.89009C6.31609 7.04909 6.14783 7.20111 5.98421 7.34542C4.99915 6.56432 3.95092 6 2.5 6C2.22386 6 2 5.77614 2 5.5C2 5.22386 2.22386 5 2.5 5ZM12.2961 10.9968C10.5735 10.9423 9.3278 10.201 8.26272 9.33388L8.50901 9.10991C8.68391 8.95091 8.85217 8.79889 9.01579 8.65458C9.95241 9.39727 10.9461 9.94398 12.2888 9.99595L11.6464 9.35355C11.4512 9.15829 11.4512 8.84171 11.6464 8.64645C11.7437 8.5492 11.871 8.50038 11.9985 8.5C12.127 8.49962 12.2555 8.54843 12.3536 8.64645L13.8536 10.1464C14.0488 10.3417 14.0488 10.6583 13.8536 10.8536L12.3536 12.3536C12.1583 12.5488 11.8417 12.5488 11.6464 12.3536C11.4512 12.1583 11.4512 11.8417 11.6464 11.6464L12.2961 10.9968Z";
    private const string ShuffleOff = "M11.647 12.3541L14.1464 14.8536C14.3417 15.0488 14.6583 15.0488 14.8536 14.8536C15.0488 14.6583 15.0488 14.3417 14.8536 14.1464L1.85355 1.14645C1.65829 0.951184 1.34171 0.951184 1.14645 1.14645C0.951184 1.34171 0.951184 1.65829 1.14645 1.85355L4.7145 5.42161C4.06578 5.16073 3.33827 5 2.5 5C2.22386 5 2 5.22386 2 5.5C2 5.77614 2.22386 6 2.5 6C3.95092 6 4.99915 6.56432 5.98421 7.34542C6.09688 7.24605 6.21175 7.14302 6.32947 7.03658L7.03751 7.74461C5.70056 8.95719 4.47234 10 2.5 10C2.22386 10 2 10.2239 2 10.5C2 10.7761 2.22386 11 2.5 11C4.91114 11 6.40583 9.67022 7.74547 8.45258L8.45339 9.16049L8.26272 9.33388C8.64016 9.64117 9.04028 9.93265 9.47627 10.1834L11.6459 12.353C11.6461 12.3532 11.6468 12.3539 11.647 12.3541C11.6468 12.3539 11.6472 12.3543 11.647 12.3541ZM12.1071 9.98576L13.4142 11.2929L13.8536 10.8536C14.0488 10.6583 14.0488 10.3417 13.8536 10.1464L12.3536 8.64645C12.2555 8.54843 12.127 8.49962 11.9985 8.5C11.871 8.50038 11.7437 8.5492 11.6464 8.64645C11.4512 8.84171 11.4512 9.15829 11.6464 9.35355L12.2888 9.99595C12.2275 9.99358 12.167 9.99017 12.1071 9.98576ZM8.55597 6.43464L9.27081 7.14947C10.1345 6.50814 11.0672 6.05134 12.2888 6.00405L11.6464 6.64645C11.4512 6.84171 11.4512 7.15829 11.6464 7.35355C11.8417 7.54881 12.1583 7.54881 12.3536 7.35355L13.8536 5.85355C14.0488 5.65829 14.0488 5.34171 13.8536 5.14645L12.3536 3.64645C12.1583 3.45118 11.8417 3.45118 11.6464 3.64645C11.4512 3.84171 11.4512 4.15829 11.6464 4.35355L12.2961 5.00321C10.7291 5.05276 9.55673 5.67068 8.55597 6.43464Z";
    public string DataShuffleIcon => IsShuffleOn ? ShuffleOff : ShuffleOn;

    private const string RepeatOn = "M12.8935 5.23788C13.579 5.95588 14 6.92865 14 7.99975C14 10.1419 12.316 11.8908 10.1996 11.9949L10 11.9997L6.707 11.999L7.85525 13.1479C8.02882 13.3215 8.0481 13.5909 7.91311 13.7858L7.85525 13.855C7.68169 14.0286 7.41226 14.0479 7.21739 13.9129L7.14815 13.855L5.14645 11.8533C4.97288 11.6797 4.9536 11.4103 5.08859 11.2154L5.14645 11.1462L7.14815 9.14449C7.34341 8.94923 7.65999 8.94923 7.85525 9.14449C8.02882 9.31806 8.0481 9.58748 7.91311 9.78235L7.85525 9.8516L6.707 10.999L10 10.9997C11.5977 10.9997 12.9037 9.75083 12.9949 8.17602L13 7.99975C13 7.17778 12.6694 6.43303 12.134 5.89117C12.0522 5.80305 12 5.68194 12 5.54865C12 5.2725 12.2239 5.04865 12.5 5.04865C12.6227 5.04865 12.7351 5.09287 12.8221 5.16624L12.8935 5.23788ZM8.78431 2.08664L8.85355 2.14449L10.8553 4.14619L10.9131 4.21544C11.0312 4.38595 11.0312 4.61354 10.9131 4.78405L10.8553 4.8533L8.85355 6.855L8.78431 6.91286C8.6138 7.03098 8.3862 7.03098 8.21569 6.91286L8.14645 6.855L8.08859 6.78575C7.97047 6.61524 7.97047 6.38765 8.08859 6.21714L8.14645 6.14789L9.294 4.99905L6 4.99975C4.40232 4.99975 3.09634 6.24867 3.00509 7.82347L3 7.99975C3 8.8193 3.32863 9.56209 3.8613 10.1035C3.94745 10.1919 4 10.3134 4 10.4472C4 10.7234 3.77614 10.9472 3.5 10.9472C3.36244 10.9472 3.23785 10.8917 3.14745 10.8018C2.4379 10.0823 2 9.09215 2 7.99975C2 5.85755 3.68397 4.10867 5.80036 4.00464L6 3.99975L9.294 3.99905L8.14645 2.8516L8.08859 2.78235C7.9536 2.58748 7.97288 2.31806 8.14645 2.14449C8.32001 1.97093 8.58944 1.95164 8.78431 2.08664Z";
    private const string RepeatOff = "M2.78431 2.08834L2.85355 2.14619L13.8536 13.1462C14.0488 13.3415 14.0488 13.658 13.8536 13.8533C13.68 14.0269 13.4106 14.0462 13.2157 13.9112L13.1464 13.8533L11.1305 11.8378C10.8437 11.9221 10.5435 11.9752 10.2339 11.993L10 11.9997L6.707 11.999L7.85525 13.1479C8.02882 13.3215 8.0481 13.5909 7.91311 13.7858L7.85525 13.855C7.68169 14.0286 7.41226 14.0479 7.21739 13.9129L7.14815 13.855L5.14645 11.8533C4.97288 11.6797 4.9536 11.4103 5.08859 11.2154L5.14645 11.1462L7.14815 9.14449C7.34341 8.94923 7.65999 8.94923 7.85525 9.14449C8.02882 9.31806 8.0481 9.58748 7.91311 9.78235L7.85525 9.8516L6.707 10.999L10 10.9997C10.0941 10.9997 10.1871 10.9954 10.279 10.9869L4.62614 5.33211C3.66034 5.83052 3 6.83802 3 7.99975C3 8.8193 3.32863 9.56209 3.8613 10.1035C3.94745 10.1919 4 10.3134 4 10.4472C4 10.7234 3.77614 10.9472 3.5 10.9472C3.36244 10.9472 3.23785 10.8917 3.14745 10.8018C2.4379 10.0823 2 9.09215 2 7.99975C2 6.56381 2.75664 5.30459 3.89297 4.59904L2.14645 2.8533C1.95118 2.65804 1.95118 2.34146 2.14645 2.14619C2.32001 1.97263 2.58944 1.95334 2.78431 2.08834ZM12.5 5.04865C12.6227 5.04865 12.7351 5.09287 12.8221 5.16624L12.8935 5.23788C13.579 5.95588 14 6.92865 14 7.99975C14 9.07744 13.5738 10.0556 12.8808 10.7748L12.174 10.0671C12.6858 9.52898 13 8.80105 13 7.99975C13 7.17778 12.6694 6.43303 12.134 5.89117C12.0522 5.80305 12 5.68194 12 5.54865C12 5.2725 12.2239 5.04865 12.5 5.04865ZM8.14645 2.14449C8.32001 1.97093 8.58944 1.95164 8.78431 2.08664L8.85355 2.14449L10.8553 4.14619L10.9131 4.21544C11.0312 4.38595 11.0312 4.61354 10.9131 4.78405L10.8553 4.8533L8.907 6.80005L8.2 6.09305L9.294 4.99905H7.105L6.105 3.99905H9.294L8.14645 2.8516L8.08859 2.78235C7.9536 2.58748 7.97288 2.31806 8.14645 2.14449Z";
    public string DataRepeatIcon => IsRepeatOn ? RepeatOff : RepeatOn;

    //private string _checked = "M13.8639 3.65511C14.0533 3.85606 14.0439 4.17251 13.8429 4.36191L5.91309 11.8358C5.67573 12.0595 5.30311 12.0526 5.07417 11.8203L2.39384 9.09995C2.20003 8.90325 2.20237 8.58667 2.39907 8.39286C2.59578 8.19905 2.91235 8.2014 3.10616 8.3981L5.51192 10.8398L13.1571 3.63419C13.358 3.44479 13.6745 3.45416 13.8639 3.65511Z";
    private const string StayOnTopOn = "M10.0589 2.44535C9.34701 1.73087 8.14697 1.90854 7.67261 2.79864L5.6526 6.58902L2.8419 7.52592C2.6775 7.58072 2.5532 7.71673 2.51339 7.88539C2.47357 8.05404 2.52392 8.23128 2.64646 8.35382L4.79291 10.5003L2.14645 13.1467L2 14.0003L2.85356 13.8538L5.50002 11.2074L7.64646 13.3538C7.76899 13.4764 7.94623 13.5267 8.11489 13.4869C8.28354 13.4471 8.41955 13.3228 8.47435 13.1584L9.41143 10.3472L13.1897 8.32448C14.0759 7.85006 14.2538 6.65535 13.5443 5.9433L10.0589 2.44535ZM8.55511 3.26895C8.71323 2.97225 9.11324 2.91303 9.35055 3.15119L12.836 6.64914C13.0725 6.88648 13.0131 7.28472 12.7178 7.44286L8.76403 9.55946C8.65137 9.61977 8.56608 9.72092 8.52567 9.84215L7.7815 12.0746L3.92562 8.21877L6.15812 7.47461C6.27966 7.43409 6.38101 7.34848 6.44126 7.23542L8.55511 3.26895Z";
    private const string StayOnTopOff = "M9.56016 10.2673L14.1464 14.8536C14.3417 15.0488 14.6583 15.0488 14.8536 14.8536C15.0488 14.6583 15.0488 14.3417 14.8536 14.1464L1.85355 1.14645C1.65829 0.951184 1.34171 0.951184 1.14645 1.14645C0.951184 1.34171 0.951184 1.65829 1.14645 1.85355L5.73223 6.43934L5.6526 6.58876L2.8419 7.52566C2.6775 7.58046 2.5532 7.71648 2.51339 7.88513C2.47357 8.05378 2.52392 8.23102 2.64646 8.35356L4.79291 10.5L2.14645 13.1465L2 14L2.85356 13.8536L5.50002 11.2071L7.64646 13.3536C7.76899 13.4761 7.94623 13.5264 8.11489 13.4866C8.28354 13.4468 8.41955 13.3225 8.47435 13.1581L9.41143 10.3469L9.56016 10.2673ZM8.82138 9.52849L8.76403 9.5592C8.65137 9.61951 8.56608 9.72066 8.52567 9.84189L7.7815 12.0744L3.92562 8.21851L6.15812 7.47435C6.27966 7.43383 6.38101 7.34822 6.44126 7.23516L6.47143 7.17854L8.82138 9.52849ZM12.7178 7.4426L10.6636 8.54227L11.4024 9.28105L13.1897 8.32422C14.0759 7.84981 14.2538 6.65509 13.5443 5.94304L10.0589 2.44509C9.34701 1.73062 8.14697 1.90828 7.67261 2.79838L6.71556 4.59421L7.45476 5.33341L8.55511 3.26869C8.71323 2.97199 9.11324 2.91277 9.35055 3.15093L12.836 6.64888C13.0725 6.88623 13.0131 7.28446 12.7178 7.4426Z";
    public string DataStayOnTopIcon => IsStayOnTop ? StayOnTopOff : StayOnTopOn;

    private const string CheckedBox = "M4.5 2C3.11929 2 2 3.11929 2 4.5V11.5C2 12.8807 3.11929 14 4.5 14H11.5C12.8807 14 14 12.8807 14 11.5V4.5C14 3.11929 12.8807 2 11.5 2H4.5ZM3 4.5C3 3.67157 3.67157 3 4.5 3H11.5C12.3284 3 13 3.67157 13 4.5V11.5C13 12.3284 12.3284 13 11.5 13H4.5C3.67157 13 3 12.3284 3 11.5V4.5ZM10.8536 6.85355C11.0488 6.65829 11.0488 6.34171 10.8536 6.14645C10.6583 5.95118 10.3417 5.95118 10.1464 6.14645L7 9.29289L5.85355 8.14645C5.65829 7.95118 5.34171 7.95118 5.14645 8.14645C4.95118 8.34171 4.95118 8.65829 5.14645 8.85355L6.64645 10.3536C6.84171 10.5488 7.15829 10.5488 7.35355 10.3536L10.8536 6.85355Z";
    private const string UncheckedBox = "M2 4.5C2 3.11929 3.11929 2 4.5 2H11.5C12.8807 2 14 3.11929 14 4.5V11.5C14 12.8807 12.8807 14 11.5 14H4.5C3.11929 14 2 12.8807 2 11.5V4.5ZM4.5 3C3.67157 3 3 3.67157 3 4.5V11.5C3 12.3284 3.67157 13 4.5 13H11.5C12.3284 13 13 12.3284 13 11.5V4.5C13 3.67157 12.3284 3 11.5 3H4.5Z";

    private const string CheckedCircle = "M2 8C2 4.68629 4.68629 2 8 2C11.3137 2 14 4.68629 14 8C14 11.3137 11.3137 14 8 14C4.68629 14 2 11.3137 2 8ZM8 1C4.13401 1 1 4.13401 1 8C1 11.866 4.13401 15 8 15C11.866 15 15 11.866 15 8C15 4.13401 11.866 1 8 1ZM10.8536 6.85355C11.0488 6.65829 11.0488 6.34171 10.8536 6.14645C10.6583 5.95118 10.3417 5.95118 10.1464 6.14645L7.25 9.04289L5.85355 7.64645C5.65829 7.45118 5.34171 7.45118 5.14645 7.64645C4.95118 7.84171 4.95118 8.15829 5.14645 8.35355L6.89645 10.1036C7.09171 10.2988 7.40829 10.2988 7.60355 10.1036L10.8536 6.85355Z";
    public static Avalonia.Media.Geometry DataCheckedCircleIcon => Avalonia.Media.Geometry.Parse(CheckedCircle);

    private const string UnCheckedCircle = "M8 14C11.3137 14 14 11.3137 14 8C14 4.68629 11.3137 2 8 2C4.68629 2 2 4.68629 2 8C2 11.3137 4.68629 14 8 14ZM8 13C5.23858 13 3 10.7614 3 8C3 5.23858 5.23858 3 8 3C10.7614 3 13 5.23858 13 8C13 10.7614 10.7614 13 8 13Z";
    public static Avalonia.Media.Geometry DataUnCheckedCircleIcon => Avalonia.Media.Geometry.Parse(UnCheckedCircle);

    public string DataCrossfadeIcon => IsEffectCrossfadeOn ? CheckedBox : UncheckedBox;

    public string DataNoEffectsIcon => IsNoEffectsOn ? CheckedCircle : UnCheckedCircle;

    public string DataPageSlideIcon => IsEffectPageSlideOn ? CheckedCircle : UnCheckedCircle;

    public string DataFadeInFadeOutIcon => IsEffectFadeInAndOutOn ? CheckedCircle : UnCheckedCircle;

    private const string FullscreenOn = "M11 4C11 4.55228 11.4477 5 12 5H13.5C13.7761 5 14 5.22386 14 5.5C14 5.77614 13.7761 6 13.5 6H12C10.8954 6 10 5.10457 10 4V2.5C10 2.22386 10.2239 2 10.5 2C10.7761 2 11 2.22386 11 2.5V4ZM11 12C11 11.4477 11.4477 11 12 11H13.5C13.7761 11 14 10.7761 14 10.5C14 10.2239 13.7761 10 13.5 10H12C10.8954 10 10 10.8954 10 12V13.5C10 13.7761 10.2239 14 10.5 14C10.7761 14 11 13.7761 11 13.5V12ZM4 11C4.55228 11 5 11.4477 5 12V13.5C5 13.7761 5.22386 14 5.5 14C5.77614 14 6 13.7761 6 13.5V12C6 10.8954 5.10457 10 4 10H2.5C2.22386 10 2 10.2239 2 10.5C2 10.7761 2.22386 11 2.5 11H4ZM5 4C5 4.55228 4.55228 5 4 5H2.5C2.22386 5 2 5.22386 2 5.5C2 5.77614 2.22386 6 2.5 6H4C5.10457 6 6 5.10457 6 4V2.5C6 2.22386 5.77614 2 5.5 2C5.22386 2 5 2.22386 5 2.5V4Z";
    private const string FullscreenOff = "M3.75 3C3.33579 3 3 3.33579 3 3.75V5.5C3 5.77614 2.77614 6 2.5 6C2.22386 6 2 5.77614 2 5.5V3.75C2 2.7835 2.7835 2 3.75 2H5.5C5.77614 2 6 2.22386 6 2.5C6 2.77614 5.77614 3 5.5 3H3.75ZM10 2.5C10 2.22386 10.2239 2 10.5 2H12.25C13.2165 2 14 2.7835 14 3.75V5.5C14 5.77614 13.7761 6 13.5 6C13.2239 6 13 5.77614 13 5.5V3.75C13 3.33579 12.6642 3 12.25 3H10.5C10.2239 3 10 2.77614 10 2.5ZM2.5 10C2.77614 10 3 10.2239 3 10.5V12.25C3 12.6642 3.33579 13 3.75 13H5.5C5.77614 13 6 13.2239 6 13.5C6 13.7761 5.77614 14 5.5 14H3.75C2.7835 14 2 13.2165 2 12.25V10.5C2 10.2239 2.22386 10 2.5 10ZM13.5 10C13.7761 10 14 10.2239 14 10.5V12.25C14 13.2165 13.2165 14 12.25 14H10.5C10.2239 14 10 13.7761 10 13.5C10 13.2239 10.2239 13 10.5 13H12.25C12.6642 13 13 12.6642 13 12.25V10.5C13 10.2239 13.2239 10 13.5 10Z";
    public string DataFullscreenOnIcon => IsFullscreenOn ? FullscreenOn : FullscreenOff;

    public string DataSystemDpiScalingFactorOnIcon => IsOverrideSystemDpiScalingFactorOn ? CheckedBox : UncheckedBox;

    public string DataStretchInOnIcon => IsStretchInOn ? CheckedBox : UncheckedBox;

    public string DataStretchOutOnIcon => IsStretchOutOn ? CheckedBox : UncheckedBox;

    public string DataViewFilePathPopupOnIcon => IsViewFilePathPopupOn ? CheckedBox : UncheckedBox;

    public string DataViewImageListOnIcon => IsViewImageListOn ? CheckedBox : UncheckedBox;

    #endregion

    #endregion

    #region == Strings ==

    public string AppVersion
    {
        get
        {
            if (!string.IsNullOrEmpty(field)) return field;

            var assembly = Assembly.GetExecutingAssembly().GetName();
            var version = assembly.Version;
            field = $"{version?.Major}.{version?.Minor}.{version?.Build}.{version?.Revision}";

            return field;
        }
    } = string.Empty;

    public string SlideshowStartStopString => IsSlideshowOn ? ImageViewer.Properties.Resources.Label_SlideshowStop : ImageViewer.Properties.Resources.Label_SlideshowStart;

    #endregion

    #endregion

    #region == Private Methods ==

    private async Task GetPicturesAsync(IEnumerable<object>? imageInfoItems)
    {
        if (imageInfoItems is null)     
        {
            Debug.WriteLine("imageInfoItems is null @GetPicturesAsync");
            return;
        }

        if (Queue.Count <= 0)
        {
            Debug.WriteLine("Queue.Count == 0 @GetPicturesAsync");
            // Just in case...
            return;
        }

        foreach (var item in imageInfoItems)
        {
            if (_cts is null)
            {
                Debug.WriteLine("@GetPicturesAsync() in foreach _cts is null");
                return;
            }
            if (_cts.IsCancellationRequested)
            {
                Debug.WriteLine("@GetPicturesAsync() in foreach IsCancellationRequested");
                return;
            }   

            if (item is not ImageInfo img)
            {
                Debug.WriteLine("item is not ImageInfo @GetPicturesAsync");
                continue;
            }

            if (string.IsNullOrEmpty(img.ImageFilePath))
            {
                Debug.WriteLine("img.ImageFilePath is null or empty @GetPicturesAsync");
                continue;
            }

            if (img.IsAcquired)
            {
                //Debug.WriteLine("img.IsAcquired");
                continue;
            }

            if (img.IsLoading)
            {
                //Debug.WriteLine("img.IsLoading @GetPictures");
                continue;
            }
            /*
            if (File.Exists(img.ImageFilePath))
            {

            }
            */
            img.IsLoading = true;

            //Debug.WriteLine($"@GetPictures IsLoading: {img.ImageFilePath}");

            // Don't await. Fire and Forget.
            //await Dispatcher.UIThread.InvokeAsync(async () =>
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var bitmap = await Task.Run(() =>
                    {
                        if (_cts is null)
                        {
                            Debug.WriteLine("@GetPictures() before new Bitmap _cts is null");
                            return null;
                        }
                        if (_cts.IsCancellationRequested)
                        {
                            Debug.WriteLine("@GetPictures() before new Bitmap IsCancellationRequested");

                            return null;
                        }

                        try
                        {
                            //return new Bitmap(img.ImageFilePath);
                            return UniversalBitmapLoader.LoadAnyImage(img.ImageFilePath, _displayDpi, _hardwareDisplayWidth, _hardwareDisplayHeight);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Exception: Failed to load image @GetPictures on new Bitmap {img.ImageFilePath} - {ex}");

                            App.AppendErrorLog($"Exception @GetPictures on new Bitmap() {img.ImageFilePath}", ex.ToString());
                        }

                        return null;

                    }, _cts?.Token ?? CancellationToken.None);
                    //Bitmap? bitmap = new(img.ImageFilePath);

                    if (_cts is not null && _cts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (bitmap is null)
                    {
                        img.IsAcquired = false;
                        img.IsLoading = false;
                        img.ImageSource = null;
                        return;
                    }

                    //img.ImageSource = img.ImageSource.CreateScaledBitmap(new PixelSize((int)(img.ImageSource.PixelSize.Width / 1.5), (int)(img.ImageSource.PixelSize.Height / 1.5)));
                    if (IsOverrideSystemDpiScalingFactorOn && (SystemDpiScalingFactor > 1))
                    {
                        img.ImageWidth = (bitmap.PixelSize.Width / SystemDpiScalingFactor);
                        img.ImageHeight = (bitmap.PixelSize.Height / SystemDpiScalingFactor);
                    }
                    else
                    {
                        img.ImageWidth = (bitmap.PixelSize.Width);
                        img.ImageHeight = (bitmap.PixelSize.Height);
                    }

                    img.ImageSource = bitmap;

                    AdjustStretchProperty(img);

                    img.IsAcquired = true;
                    img.IsLoading = false;
                }
                catch (Exception e)
                {
                    img.IsAcquired = false;
                    img.IsLoading = false;
                    img.ImageSource = null;

                    Debug.WriteLine("GetPictures: Exception while loading: " + img.ImageFilePath + Environment.NewLine + e.Message);

                    //continue;
                }
                finally
                {
                    img.IsLoading = false;
                }

                // meaningless to wait here.
                //await Task.Delay(200);
                await Task.Yield();

            }, DispatcherPriority.Background);//Default//.Background //Loaded

            await Task.Delay(200);
        }
    }

    private async void OnSlideshowTimerTick(object? sender, EventArgs e)
    {
        try
        {
            // This code runs on the UI thread, so it's safe to update UI elements.
            if (Queue.Count <= 0) return;
            if (_queueIndex > (Queue.Count - 1))
            {
                if (IsRepeatOn)
                {
                    // Reset index.
                    _queueIndex = 0;
                }
                else
                {
                    if (IsSlideshowOn)
                    {
                        // No more to show.
                        IsSlideshowOn = false;
                    }

                    return;
                }
            }

            IsTransitionReversed = false;

            await ShowAsync();
        }
        catch (Exception ex)
        {
            _ = ex;
            Debug.WriteLine($"{ex}");
            // TODO handle exception
        }
    }

    private async Task ShowAsync()
    {
        if (_timerSlideshow.IsEnabled)
        {
            _timerSlideshow.Stop();
        }

        if (IsWorking)
        {
            await Task.Delay(300);
        }

        if (_cts is null || _cts.IsCancellationRequested)
        {
            Debug.WriteLine("@Show() IsCancellationRequested");
            return;
        }

        if (Queue.Count <= 0) return;
        if (_queueIndex < 0) return;
        if (_queueIndex > (Queue.Count - 1))
        {
            if (IsRepeatOn)
            {
                // Reset index.
                _queueIndex = 0;
            }
            else
            {
                if (IsSlideshowOn)
                {
                    // No more to show.
                    IsSlideshowOn = false;
                }

                return;
            }
        }

        var img = Queue[_queueIndex];

        if (string.IsNullOrEmpty(img.ImageFilePath))
        {
            return;
        }

        // just in case, double check.
        if (HasImageExtension(img.ImageFilePath, ValidExtensions) == false)
        {
            _queueIndex++;
            await ShowAsync();
            return;
        }

        // DO NOT await ShowImageAsync(img) directly on the UI thread or NEVER. It will freeze the UI for a while when loading from sleeping HDD.
        // Little hackkish... but it seems to work great without locking the UI when HDD (not SSD) is slowly waking up from sleep or loading very large file for example.
        // Also await Dispatcher.UIThread.InvokeAsync is needed, so don't even try removing it.
        await Task.Run(async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                IsWorking = true;

                // Never ever await(whatever they say) here: Don't await Task.Run(async () => await ShowImageAsync(img))
                if (await Task.Run(() => ShowImageAsync(img), _cts?.Token ?? CancellationToken.None)) 
                {
                    if (_cts is null)
                    {
                        Debug.WriteLine("@Show() after ShowImageAsync() _cts is null");
                        return;
                    }
                    if (_cts.IsCancellationRequested)
                    {
                        Debug.WriteLine("@Show() after ShowImageAsync() IsCancellationRequested");
                        return;
                    }

                    IsWorking = false;

                    if (IsSlideshowOn)
                    {
                        _timerSlideshow.Start();
                    }

                    QueueHasBeenChanged?.Invoke(this, _queueIndex - 1);
                }
                else
                {
                    IsWorking = false;
                }
            }, DispatcherPriority.Background);

        }, _cts?.Token ?? CancellationToken.None);

    }

    private async Task<bool> ShowImageAsync(ImageInfo img)
    {
        // Never ever await this (whatever they say).
        // Don't await Task.Run(async () => await ShowImageAsync(img)) either.
        
        if (_cts is null || _cts.IsCancellationRequested)
        {
            Debug.WriteLine("@ShowImage: IsCancellationRequested");
            return false;
        }

        if (string.IsNullOrEmpty(img.ImageFilePath))
        {
            Debug.WriteLine("@ShowImage: ImageFilePath is null or empty");
            return false;
        }

        if (!string.IsNullOrEmpty(_currentFile))
        {
            if (img.ImageFilePath.Equals(_currentFile))
            {
                Debug.WriteLine($"@ShowImage: Dupe {_queueIndex} skipping [{_currentFile}]");
                _queueIndex++;
                return true;
            }
        }

        var idx = _queueIndex;

        _currentFile = img.ImageFilePath;

        // Test not good.
        //_isWorking = true;

        //Debug.WriteLine($"{idx} Enter critical section.");
        /*
        Debug.WriteLine(
            $"[Fade check] acquired={img.IsAcquired}, " +
            $"loading={img.IsLoading}, " +
            $"hasBitmap={img.ImageSource is not null}, " +
            $"file={img.ImageFilePath}");
        */

        if (img.IsLoading)
        {
            Debug.WriteLine($"@ShowImage: Waiting for preload: {img.ImageFilePath}");

            while (img.IsLoading && !(_cts is null || _cts.IsCancellationRequested))
            {
                await Task.Delay(10);
            }
        }

        if (_cts is null || _cts.IsCancellationRequested)
        {
            return false;
        }

        if (!img.IsAcquired || img.ImageSource is null)
        {
            img.IsLoading = true;

            try
            {
                var bitmap = await Task.Run(() => UniversalBitmapLoader.LoadAnyImage(img.ImageFilePath, _displayDpi, _hardwareDisplayWidth, _hardwareDisplayHeight),_cts?.Token ?? CancellationToken.None);

                if (_cts is null || _cts.IsCancellationRequested)
                {
                    bitmap?.Dispose();
                    return false;
                }

                if (bitmap is null)
                {
                    img.IsAcquired = false;
                    img.IsLoading = false;
                    DisplayImage = null;
                    _queueIndex = idx + 1;
                    return true;
                }

                img.ImageSource = bitmap;

                if (IsOverrideSystemDpiScalingFactorOn && SystemDpiScalingFactor > 1)
                {
                    img.ImageWidth = img.ImageSource.PixelSize.Width / SystemDpiScalingFactor;
                    img.ImageHeight = img.ImageSource.PixelSize.Height / SystemDpiScalingFactor;
                }
                else
                {
                    img.ImageWidth = img.ImageSource.PixelSize.Width;
                    img.ImageHeight = img.ImageSource.PixelSize.Height;
                }

                AdjustStretchProperty(img);
                img.IsAcquired = true;
            }
            catch (OperationCanceledException) when (_cts is not null && _cts.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{ex} @ShowImage");
                img.IsAcquired = false;
                img.IsLoading = false;
                //DisplayImage = null;
                App.AppendErrorLog($"Exception @ShowImage on new Bitmap() {img.ImageFilePath}",ex.ToString());
            }
            finally
            {
                img.IsLoading = false;
            }
        }

        DisplayImage = img;

        _queueIndex = idx + 1;

        #region == Unload some of the images from memory ==

        if (_queueIndex > 20)
        {
            var i = _queueIndex - 15;
            if ((Queue[i].ImageSource is not null) && Queue[i].IsAcquired)
            {
                Queue[i].ImageSource?.Dispose();
                Queue[i].ImageSource = null;
                Queue[i].IsAcquired = false;
                Queue[i].IsLoading = false;
            }
        }

        var c = Queue.Count;
        if (_queueIndex + 20 < c)
        {
            var i = _queueIndex + 15;
            if ((Queue[i].ImageSource is not null) && Queue[i].IsAcquired)
            {
                Queue[i].ImageSource?.Dispose();
                Queue[i].ImageSource = null;
                Queue[i].IsAcquired = false;
                Queue[i].IsLoading = false;
            }
        }

        #endregion

        return true;
    }

    private void UpdateDisplayImageMaxSizeAndStretchProperty()
    {
        if (DisplayImage is null)
        {
            return;
        }

        DisplayImage.ImageMaxWidth = ClientAreaWidth;
        DisplayImage.ImageMaxHeight = ClientAreaHeight;

        if (Queue.Count > 0)
        {
            foreach (var img in Queue)
            {
                if ((img.ImageSource is not null) && (img.IsAcquired))
                {
                    AdjustStretchProperty(img);
                    /*
                    if (IsStretchInOn && IsStretchOutOn)
                    {
                        img.ImageMaxWidth = ClientAreaWidth;
                        img.ImageMaxHeight = ClientAreaHeight;
                    }
                    else if (IsStretchInOn)
                    {
                        img.ImageMaxWidth = img.ImageWidth;
                        img.ImageMaxHeight = img.ImageHeight;
                    }
                    else
                    {
                        img.ImageMaxWidth = ClientAreaWidth;
                        img.ImageMaxHeight = ClientAreaHeight;
                    }

                    if (IsStretchNoneOn)
                    {
                        img.ImageStretch = Avalonia.Media.Stretch.None;
                    }
                    else
                    {
                        img.ImageStretch = Avalonia.Media.Stretch.Uniform;
                    }

                    if (IsStretchInOn && IsStretchOutOn)
                    {
                        img.ImageStretchDirection = StretchDirection.Both;
                    }
                    else if (IsStretchInOn)
                    {
                        img.ImageStretchDirection = StretchDirection.DownOnly;
                    }
                    else if (IsStretchOutOn)
                    {
                        img.ImageStretchDirection = StretchDirection.UpOnly;
                    }
                    */
                }
            }
        }

        OnPropertyChanged(nameof(DisplayImage));
    }

    private void AdjustStretchProperty(ImageInfo img)
    {
        switch (IsStretchInOn)
        {
            case true when IsStretchOutOn:
                img.ImageMaxWidth = ClientAreaWidth;
                img.ImageMaxHeight = ClientAreaHeight;
                break;
            case true:
                img.ImageMaxWidth = img.ImageWidth;
                img.ImageMaxHeight = img.ImageHeight;
                break;
            default:
                img.ImageMaxWidth = ClientAreaWidth;
                img.ImageMaxHeight = ClientAreaHeight;
                break;
        }

        img.ImageStretch = IsStretchNone ? Avalonia.Media.Stretch.None : Avalonia.Media.Stretch.Uniform;

        switch (IsStretchInOn)
        {
            case true when IsStretchOutOn:
                img.ImageStretchDirection = StretchDirection.Both;
                break;
            case true:
                img.ImageStretchDirection = StretchDirection.DownOnly;
                break;
            default:
                {
                    if (IsStretchOutOn)
                    {
                        img.ImageStretchDirection = StretchDirection.UpOnly;
                    }

                    break;
                }
        }
    }

    private static void ReleaseImage(ImageInfo image)
    {
        var bitmap = image.ImageSource;
        image.ImageSource = null;

        image.ImageWidth = 0;
        image.ImageHeight = 0;
        image.IsAcquired = false;
        image.IsLoading = false;

        bitmap?.Dispose();
    }
    private static bool PathsEqual(string left, string right)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            comparison);
    }

    #endregion

    #region == Public Methods ==

    // Dropped or Open with.
    public async Task DroppedFilesAsync(List<ImageInfo> images, string singleSelectedOriginalFile)
    {
        //Debug.WriteLine("DroppedFilesAsync()");

        if (images.Count < 1)
        {
            IsWorking = false;
            return;
        }

        IsWorking = true;

        if (_timerSlideshow.IsEnabled)
        {
            _timerSlideshow.Stop();
        }

        // rest 
        _queueIndex = 0;

        // Let's keep the prev image for the sake of fade out effects.
        //DiplayImage1 = null;

        // Just because.
        SelectedQueueImage = null;


        // Keep old bitmaps alive while the outgoing image transitions away.
        // Do not release ImageInfo instances reused in the incoming queue.
        var incomingImages = new HashSet<ImageInfo>(images, ReferenceEqualityComparer.Instance);
        var oldImages = Queue
            .Concat(_originalQueue)
            .Where(image => !incomingImages.Contains(image))
            .Distinct<ImageInfo>(ReferenceEqualityComparer.Instance)
            .ToArray();

        
        Queue.Clear();
        _originalQueue.Clear();

        Queue = new ObservableCollection<ImageInfo>(images);
        _originalQueue = images;//[.. images];

        // When the same files dropped, the file won't reload due to "dup". So clear it.
        _currentFile = string.Empty;

        if (Queue.Count <= 0)
        {
            IsWorking = false;
            return;
        }

        // Hide welcome screen
        QueueLoaded?.Invoke(this, EventArgs.Empty);

        if (!IsFullscreen)
        {
            // show file path if on.
            IsFilePathPopupVisible = _isViewFilePathPopupOn;
            // show listbox if on.
            IsQueueListBoxVisible = _isViewImageListOn;

            if (IsViewImageListOn)
            {
                // Hide if count = 1
                IsQueueListBoxVisible = Queue.Count != 1;
            }
        }

        ImageInfo? selectedItem = null;
        if (!string.IsNullOrWhiteSpace(singleSelectedOriginalFile))
        {
            selectedItem = Queue.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.ImageFilePath) &&
                PathsEqual(x.ImageFilePath, singleSelectedOriginalFile));
        }

        if (_isShuffleOn)
        {
            //Debug.WriteLine("Shuffle @DroppedFiles()");

            Queue.Shuffle();

            // Move the single selected image file to top/ 0 index if exist for easier viewing when shuffled. This is for the case when user select a file and drag and drop to the app.
            //if (!string.IsNullOrEmpty(singleSelectedOriginalFile))
            if (selectedItem is not null)
            {
                /*
                var item = Queue.First(x => x.ImageFilePath == singleSelectedOriginalFile);
                Queue.Remove(item);
                Queue.Insert(0, item);
                _queueIndex = 0;
                */
                Queue.Remove(selectedItem);
                Queue.Insert(0, selectedItem);
                _queueIndex = 0;
            }
        }
        else
        {
            // Move index to the single selected file if exist. This is for the case when user select a file and drag and drop to the app.
            //if (!string.IsNullOrEmpty(singleSelectedOriginalFile))
            if (selectedItem is not null)
            {
                //var item = Queue.First(x => x.ImageFilePath == singleSelectedOriginalFile);
                //_queueIndex = Queue.IndexOf(item);
                _queueIndex = Queue.IndexOf(selectedItem);
            }
        }

        // Test -> yap. IsWorking causes await delay 300 mil sec in Show(),
        IsWorking = false;

        IsTransitionReversed = false;

        //Debug.WriteLine("Calling Show() @DroppedFiles()");

        // Show Image.
        await ShowAsync();

        // Wait untill the Image drawn before loading ListBox which starts loading images on its own.
        //await Task.Delay(500);

        // Dispose old images that are not in the new queue.
        foreach (var image in oldImages)
        {
            ReleaseImage(image);
        }

        IsWorking = true;
        await Task.Yield();

        //Debug.WriteLine("Updating Queue @DroppedFiles()");

        // Display images in the ListBox.
        OnPropertyChanged(nameof(Queue));

        // Need this for trigger UpdateVisibleItems because if only a few images are loaded,
        // scrollviewer does not fire changed events.
        if (_queueIndex > 0)
        {
            // Shuffled
            QueueHasBeenChanged?.Invoke(this, _queueIndex - 1);
        }
        else
        {
            QueueHasBeenChanged?.Invoke(this, 0);
        }

        IsWorking = false;
        await Task.Yield();
    }

    public async Task NextKeyPressedAsync()
    {
        if (_timerSlideshow.IsEnabled)
        {
            _timerSlideshow.Stop();
        }

        if (Queue.Count <= 0) return;

        //_queueIndex++;
        if ((_queueIndex) > (Queue.Count - 1))
        {
            if (IsRepeatOn)
            {
                // Reset index.
                _queueIndex = 0;
            }
            else
            {
                if (IsSlideshowOn)
                {
                    // No more to show.
                    IsSlideshowOn = false;
                }

                return;
            }
        }

        IsTransitionReversed = false;

        await ShowAsync();
    }

    public async Task PrevKeyPressedAsync()
    {
        if (Queue.Count <= 0) return;

        if (_queueIndex == 1)
        {
            //Debug.WriteLine("(_queueIndex == 0) PrevKeyPressed");
            return;
        }

        if ((_queueIndex - 2) <= -1)
        {
            return;
        }

        if (_timerSlideshow.IsEnabled)
        {
            _timerSlideshow.Stop();
        }

        _queueIndex -= 2;

        IsTransitionReversed = true;

        await ShowAsync();
    }

    public async Task ListBoxItemSelectedAsync(ImageInfo img)
    {
        // TODO: check index and determie 
        IsTransitionReversed = false;

        _queueIndex = Queue.IndexOf(img);

        await ShowAsync();
    }

    public static bool HasImageExtension(string fileName, HashSet<string> extensions)
    {
        var extension = System.IO.Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }
        return extensions.Contains(extension);
        /*
        foreach (var validExt in extensions)
        {
            if (string.Equals(extension, validExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        */
    }

    public void ClientAreaSizeChanged(double width, double height)
    {
        ClientAreaWidth = width;
        ClientAreaHeight = height;
        UpdateDisplayImageMaxSizeAndStretchProperty();
    }

    public void UpdateScreenInfo(double dpiScalingFactor, int hardwareDisplayWidth, int hardwareDisplayHeight)
    {
        SystemDpiScalingFactor = dpiScalingFactor;
        _hardwareDisplayWidth = hardwareDisplayWidth;
        _hardwareDisplayHeight = hardwareDisplayHeight;

        _displayDpi = new Vector(96.0 * SystemDpiScalingFactor, 96.0 * SystemDpiScalingFactor);
        

        UpdateDisplayImageMaxSizeAndStretchProperty();
    }

    public void UpdateSettings()
    {
        var config = _configurationService.CurrentConfiguration;

        config.IsSaveLog = IsSaveLog;
        config.IsShuffleOn = IsShuffleOn;
        config.IsRepeatOn = IsRepeatOn;
        config.IsOverrideSystemDpiScalingFactorOn = IsOverrideSystemDpiScalingFactorOn;
        config.IsViewImageListOn = IsViewImageListOn;
        config.IsViewFilePathPopupOn = IsViewFilePathPopupOn;
        config.SlideshowTimerInterval = SlideshowTimerInterval;
        config.IsStretchInOn = IsStretchInOn;
        config.IsStretchOutOn = IsStretchOutOn;
        config.IsEffectFadeInAndOutOn = IsEffectFadeInAndOutOn;
        config.IsEffectPageSlideOn = IsEffectPageSlideOn;
        config.IsNoEffectsOn = IsNoEffectsOn;
        config.IsEffectCrossfadeOn = IsEffectCrossfadeOn;

    }

    public void Dispose()
    {
        try
        {
            if (_timerSlideshow is not null)
            {
                _timerSlideshow.Stop();
                _timerSlideshow.Tick -= OnSlideshowTimerTick;
            }

        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception @Dispose() _timerSlideshow.Stop() - {ex}");
        }

        try
        {
            try
            {
                if (Queue.Count > 0)
                {
                    foreach (var img in Queue)
                    {
                        ReleaseImage(img);
                    }
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception @Dispose() ReleaseImage() - {ex}");
            }

            _cts?.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Expected exception when cancellation is requested.
            Debug.WriteLine("@Dispose() _cts.Cancel() - OperationCanceledException");
        }
        catch (ObjectDisposedException) when (_cts is not null && _cts.IsCancellationRequested)
        {
            // Expected exception when cancellation token source is already disposed.
            Debug.WriteLine("@Dispose() _cts.Cancel() - ObjectDisposedException");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception @Dispose() _cts.Cancel() - {ex}");
        }
        finally
        {
            try
            {
                _cts?.Dispose();
                //_cts = null;
                Debug.WriteLine("@Dispose() _cts.Dispose()");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception @Dispose() _cts.Dispose() - {ex}");
            }
        }
    }

    #endregion

    #region == Commands ==

    [RelayCommand]
    public void ToggleStayOnTop()
    {
        var win = App.GetService<MainWindow>();

        win.Topmost = !IsStayOnTop;

        IsStayOnTop = !IsStayOnTop;
    }

    [RelayCommand(CanExecute = nameof(CanToggleCrossfade))]
    public void ToggleCrossfade()
    {
        IsEffectCrossfadeOn = !IsEffectCrossfadeOn;
    }

    private bool CanToggleCrossfade()
    {
        return !IsNoEffectsOn;
    }

    [RelayCommand]
    public void ToggleFadeInAndOut()
    {
        IsEffectFadeInAndOutOn = true;//!IsEffectFadeInAndOutOn;
        IsEffectPageSlideOn = !IsEffectFadeInAndOutOn;
        IsNoEffectsOn = !IsEffectFadeInAndOutOn;
    }

    [RelayCommand]
    public void TogglePageSlide()
    {
        IsEffectPageSlideOn = true;//!IsEffectPageSlideOn;
        IsEffectFadeInAndOutOn = !IsEffectPageSlideOn;
        IsNoEffectsOn = !IsEffectPageSlideOn;
    }

    [RelayCommand]
    public void ToggleNoEffects()
    {
        IsNoEffectsOn = true;// !IsNoEffectsOn;
        IsEffectFadeInAndOutOn = !IsNoEffectsOn;
        IsEffectPageSlideOn = !IsNoEffectsOn;
    }

    [RelayCommand(CanExecute = nameof(CanToggleSlideshow))]
    public void ToggleSlideshow()
    {
        var isOn = IsSlideshowOn;

        var page = App.GetService<MainView>();
        page.ToggleSlideshowAnimation(isOn, TimeSpan.FromMilliseconds(600));

        if (IsSlideshowOn)
        {
            //Debug.WriteLine("StartSlideshow false");
            IsSlideshowOn = false;

            if (_timerSlideshow.IsEnabled)
            {
                _timerSlideshow.Stop();
            }
        }
        else
        {
            if (_timerSlideshow.IsEnabled)
            {
                _timerSlideshow.Stop();
            }

            if (Queue.Count > 0)
            {
                //Debug.WriteLine("StartSlideshow true");
                IsSlideshowOn = true;

                // If at the end, reset index.
                if (_queueIndex >= (Queue.Count - 1))
                {
                    _queueIndex = 0;
                }

                _timerSlideshow.Start();
                //_ = Show(_crossfadeWaitDuration);
            }
        }

        OnPropertyChanged(nameof(IsSlideshowOn));
    }
    private bool CanToggleSlideshow()
    {
        return Queue.Count > 1;
    }

    [RelayCommand]
    public void ToggleShuffle()
    {
        IsShuffleOn = !IsShuffleOn;
    }

    [RelayCommand]
    public void ToggleRepeat()
    {
        IsRepeatOn = !IsRepeatOn;
    }

    [RelayCommand]
    public void ToggleFullscreen()
    {
        ToggleFullscreenState?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void SetSlideshowInterval(string strSecond)
    {
        if (string.IsNullOrEmpty(strSecond))
        {
            return;
        }

        if (long.TryParse(strSecond, out long number))
        {
            if (number >= 0)
            {
                SlideshowTimerInterval = number;
            }
        }
    }

    [RelayCommand]
    public void ToggleSystemDpiScalingFactor()
    {
        IsOverrideSystemDpiScalingFactorOn = !IsOverrideSystemDpiScalingFactorOn;
    }

    [RelayCommand]
    public void ToggleStretchInOn()
    {
        IsStretchInOn = !IsStretchInOn;
    }

    [RelayCommand]
    public void ToggleStretchOutOn()
    {
        IsStretchOutOn = !IsStretchOutOn;
    }

    [RelayCommand(CanExecute = nameof(CanToggleViewFilePath))]
    public void ToggleViewFilePath()
    {
        IsViewFilePathPopupOn = !IsViewFilePathPopupOn;
    }
    public bool CanToggleViewFilePath()
    {
        //if (Queue.Count <= 0) return false;
        if (_isFullscreen) return false;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanToggleViewImageList))]
    public void ToggleViewImageList()
    {
        IsViewImageListOn = !IsViewImageListOn;
    }
    private bool CanToggleViewImageList()
    {
        //if (Queue.Count <= 1) return false;
        if (_isFullscreen) return false;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    public async Task ShowInExplorer()
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            return;
        }

        if (!System.IO.File.Exists(_currentFile))
        {
            return;
        }

        var dir = System.IO.Path.GetDirectoryName(_currentFile);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        var mainWin = App.GetService<MainWindow>();
        var launcher = TopLevel.GetTopLevel(mainWin)?.Launcher;
        if (launcher is null)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Avalonia's Launcher does not support selecting file in explorer on Windows. So roll our own.

            // Just open the directory.
            //Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });

            // This works currectly (select intended file) only if execute twice. Not sure why.
            Process.Start("explorer.exe", $"/select,\"{_currentFile}\"");

            // Pretty much the same as above. This one requires pinvoke.
            //ShellExecute(IntPtr.Zero, "open", "explorer.exe", $"/select,\"{_currentFile}\"", null, 1);

            // Reuse explorer window. But failed to select the first time same as above.
            /*
            SHParseDisplayName(dir, IntPtr.Zero, out nint pidlFolder, 0, out uint attrs);
            SHParseDisplayName(_currentFile, IntPtr.Zero, out nint pidlFile, 0, out attrs);

            try
            {
                SHOpenFolderAndSelectItems(pidlFolder, 1, [pidlFile], 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidlFolder);
                Marshal.FreeCoTaskMem(pidlFile);
            }
            */
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS uses 'open -R' to reveal in Finder
            Process.Start("open", $"-R \"{_currentFile}\"");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // On Linux, there is no way to support selecting file in file manager.
            // So, just use Avalonia's Launcher to open the directory.
            if (await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir)))
            {
                // ok
            }
            else
            {
                // TODO: show error message?
                // This failes in Debug session when the app is attached VSCode from Snap packages on Linux. 
            }

            // Or this. (not tested)
            // Process.Start("xdg-open", dir);
        }

    }
    private bool CanShowInExplorer()
    {
        return Queue.Count > 0;
    }

    [RelayCommand]
    public async Task QueueListviewEnterKey(ImageInfo img)
    {
        await ListBoxItemSelectedAsync(img);
    }

    [RelayCommand]
    public async Task GoNext()
    {
        //Debug.WriteLine("GoNext");

        await NextKeyPressedAsync();
    }

    [RelayCommand]
    public async Task GoPrev()
    {
        //Debug.WriteLine("GoPrev");

        await PrevKeyPressedAsync();
    }

    [RelayCommand]
    public static void Quit()
    {
        // If the flyout is open, app freezes in linux when trying to close. So close the flyout first.
        //var mainWin = App.GetService<MainWindow>();
        //mainWin.Close();
    }

    #endregion

}


