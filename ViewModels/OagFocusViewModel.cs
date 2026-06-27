using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using OagFocusAssist.Analyzers;
using OagFocusAssist.Properties;
using OagFocusAssist.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OagFocusAssist.ViewModels;

public sealed class OagFocusViewModel : INotifyPropertyChanged
{
    private readonly IOagFocusService _focusService;
    private readonly ICameraMediator _camera;
    private readonly IImagingMediator _imaging;
    private readonly IProfileService _profileService;

    private CancellationTokenSource _seriesCts;
    private CancellationTokenSource _captureCts;
    private Task _activeCaptureTask;
    private readonly object _captureLock = new();
    private bool _isCapturing;
    private bool _isSeriesRunning;
    private bool _pauseRequested;
    private double _exposureTime;

    private const double HfrChangeThreshold = 0.08;
    private const int MinPointsForFilter = 3;

    // ═══════════════════════════════════════════════════════════
    // ЦВЕТА СТАТУСОВ (используются в UpdateTrendBrush)
    // ═══════════════════════════════════════════════════════════
    private static readonly SolidColorBrush BrushRed = new(Color.FromRgb(0xF8, 0x71, 0x71));      // Ухудшается
    private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(0x4A, 0xDE, 0x80));     // Улучшается
    private static readonly SolidColorBrush BrushBlue = new(Color.FromRgb(0x5B, 0x8D, 0xEF));      // Плато
    private static readonly SolidColorBrush BrushWhite = new(Color.FromRgb(0xE4, 0xE4, 0xE7));     // Сбор данных / —

    static OagFocusViewModel()
    {
        BrushRed.Freeze();
        BrushGreen.Freeze();
        BrushBlue.Freeze();
        BrushWhite.Freeze();
    }

    public double ExposureTime
    {
        get => _exposureTime;
        set
        {
            double clamped = Math.Clamp(value, 0.01, 300);
            if (Math.Abs(_exposureTime - clamped) > 1e-9)
            {
                _exposureTime = clamped;
                Settings.Default.ExposureTime = clamped;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Название подключенной камеры для отображения в overlay графика.
    /// </summary>
    public string CameraName
    {
        get
        {
            try
            {
                var device = _camera?.GetDevice();
                if (device?.Connected == true)
                {
                    var name = device.DisplayName ?? device.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        name = name.Trim();
                        if (name.Length > 35)
                        {
                            name = name.Substring(0, 32) + "...";
                        }
                        return $"{name}:";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"OAG Focus: не удалось получить имя камеры: {ex.Message}");
            }

            return Settings.Default.DemoMode ? "Демо-режим:" : "Камера:";
        }
    }

    private PointCollection _graphPoints = [];
    public PointCollection GraphPoints
    {
        get => _graphPoints;
        set => SetProperty(ref _graphPoints, value);
    }

    private PointCollection _minPoint = [];
    public PointCollection MinPoint
    {
        get => _minPoint;
        set => SetProperty(ref _minPoint, value);
    }

    public double CurrentHfr => _focusService?.CurrentHfr ?? 0;
    public double AverageHfr => _focusService?.AverageHfr ?? 0;
    public double MinHfr => _focusService?.MinHfr ?? 0;
    public double MaxHfr => _focusService?.MaxHfr ?? 0;
    public string TrendText => _focusService?.TrendText ?? "—";
    public int FrameCount => _focusService?.HfrValues.Count ?? 0;

    // ═══════════════════════════════════════════════════════════
    // ЦВЕТ СТАТУСА: динамически меняется в зависимости от TrendText
    // ═══════════════════════════════════════════════════════════
    private SolidColorBrush _trendBrush = BrushWhite;
    public SolidColorBrush TrendBrush
    {
        get => _trendBrush;
        private set => SetProperty(ref _trendBrush, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set => SetProperty(ref _isCapturing, value);
    }

    public bool IsSeriesRunning
    {
        get => _isSeriesRunning;
        private set
        {
            if (SetProperty(ref _isSeriesRunning, value))
            {
                Logger.Info($"OAG Focus: IsSeriesRunning = {value}");
                OnPropertyChanged(nameof(IsPlayEnabled));
                OnPropertyChanged(nameof(IsPauseEnabled));

                ((RelayCommand)StartSeriesCommand).RaiseCanExecuteChanged();
                ((RelayCommand)PauseSeriesCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlayEnabled => !IsSeriesRunning;
    public bool IsPauseEnabled => IsSeriesRunning;

    private FocusState _focusState = FocusState.Collecting;
    public FocusState FocusState
    {
        get => _focusState;
        private set => SetProperty(ref _focusState, value);
    }

    public bool IsOptimalState => FocusState is FocusState.Optimal or FocusState.Plateau;
    public bool IsDegradedState => FocusState is FocusState.Degraded;

    public ICommand StartSeriesCommand { get; }
    public ICommand PauseSeriesCommand { get; }
    public ICommand CompleteCommand { get; }
    public ICommand SetExposureCommand { get; }

    public OagFocusViewModel(
        IOagFocusService focusService,
        ICameraMediator camera,
        IImagingMediator imaging,
        IProfileService profileService)
    {
        _focusService = focusService ?? throw new ArgumentNullException(nameof(focusService));
        _camera = camera;
        _imaging = imaging;
        _profileService = profileService;
        _exposureTime = Settings.Default.ExposureTime;

        // Инициализируем цвет статуса начальным значением
        UpdateTrendBrush(TrendText);

        Logger.Info($"OAG Focus: ViewModel создан, ExposureTime={_exposureTime:F2}, " +
                    $"Camera={CameraName}");

        StartSeriesCommand = new RelayCommand(
            execute: async () => await StartSeriesAsync(),
            canExecute: () => IsPlayEnabled);

        PauseSeriesCommand = new RelayCommand(
            execute: PauseSeries,
            canExecute: () => IsPauseEnabled);

        CompleteCommand = new RelayCommand(() =>
        {
            Logger.Info("OAG Focus: CompleteCommand вызван");
            PauseSeries();
            _focusService.SignalCompletion();
        });

        SetExposureCommand = new RelayCommand<double>(v => ExposureTime = v);

        _focusService.PropertyChanged += OnServicePropertyChanged;
    }

    /// <summary>
    /// Обновляет цвет статуса в зависимости от текста TrendText
    /// </summary>
    private void UpdateTrendBrush(string trendText)
    {
        TrendBrush = trendText switch
        {
            "Ухудшается" => BrushRed,
            "Улучшается" => BrushGreen,
            "Плато" => BrushBlue,
            _ => BrushWhite  // "Сбор данных...", "—", и любые другие
        };
    }

    private async Task StartSeriesAsync()
    {
        if (IsSeriesRunning)
        {
            Logger.Warning("OAG Focus: StartSeriesAsync вызван, но серия уже запущена");
            return;
        }

        Logger.Info($"OAG Focus: ▶ START серии (экспозиция {ExposureTime:F2} сек, " +
                    $"demo={Settings.Default.DemoMode}, камера={CameraName})");

        IsSeriesRunning = true;
        _pauseRequested = false;
        _seriesCts?.Cancel();
        _seriesCts = new CancellationTokenSource();
        var seriesToken = _seriesCts.Token;

        OnPropertyChanged(nameof(CameraName));

        try
        {
            while (!seriesToken.IsCancellationRequested && !_pauseRequested)
            {
                Logger.Info($"OAG Focus: начало итерации цикла (кадр #{_focusService.HfrValues.Count + 1})");

                _captureCts?.Cancel();
                _captureCts = CancellationTokenSource.CreateLinkedTokenSource(seriesToken);
                var captureToken = _captureCts.Token;

                Task<double> captureTask = null;

                try
                {
                    IsCapturing = true;

                    captureTask = Settings.Default.DemoMode
                        ? GenerateDemoHfrAsync(captureToken)
                        : CaptureRealHfrAsync(captureToken);

                    lock (_captureLock) { _activeCaptureTask = captureTask; }

                    var cancelCompletion = WaitForCancellationAsync(captureToken);
                    var completedTask = await Task.WhenAny(captureTask, cancelCompletion);

                    if (completedTask == cancelCompletion)
                    {
                        Logger.Info("OAG Focus: отмена произошла раньше завершения съёмки");

                        if (!Settings.Default.DemoMode)
                        {
                            ForceAbortExposure();
                        }

                        try
                        {
                            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await Task.WhenAny(captureTask, Task.Delay(Timeout.Infinite, waitCts.Token));
                        }
                        catch { }

                        break;
                    }

                    lock (_captureLock) { _activeCaptureTask = null; }

                    double hfr = await captureTask;

                    if (seriesToken.IsCancellationRequested || _pauseRequested)
                    {
                        Logger.Info("OAG Focus: пауза после завершения кадра");
                        if (hfr > 0)
                        {
                            _focusService.HfrValues.Add(hfr);
                            ApplyAnalysis();
                        }
                        break;
                    }

                    if (hfr > 0)
                    {
                        _focusService.HfrValues.Add(hfr);
                        ApplyAnalysis();
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("OAG Focus: OperationCanceled — остановка серии");
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    Logger.Error($"OAG Focus: {ex.Message}");
                    ShowError($"Ошибка съёмки:\n\n{ex.Message}", MessageBoxImage.Warning);
                    break;
                }
                catch (TimeoutException ex)
                {
                    Logger.Error($"OAG Focus: {ex.Message}");
                    ShowError($"Таймаут:\n\n{ex.Message}", MessageBoxImage.Warning);
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"OAG Focus: {ex}");
                    ShowError($"Ошибка:\n\n{ex.Message}", MessageBoxImage.Error);
                    break;
                }
                finally
                {
                    lock (_captureLock) { _activeCaptureTask = null; }
                    IsCapturing = false;
                }

                if (!seriesToken.IsCancellationRequested && !_pauseRequested)
                {
                    try
                    {
                        await Task.Delay(150, seriesToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("OAG Focus: серия отменена через OperationCanceledException");
        }
        finally
        {
            Logger.Info("OAG Focus: завершение серии (finally блок)");
            IsCapturing = false;
            IsSeriesRunning = false;
            _pauseRequested = false;
            lock (_captureLock) { _activeCaptureTask = null; }
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PauseSeries()
    {
        Logger.Info($"OAG Focus: ⏸ PAUSE вызван (IsSeriesRunning={IsSeriesRunning})");

        if (!IsSeriesRunning)
        {
            Logger.Warning("OAG Focus: PauseSeries вызван, но серия не запущена");
            return;
        }

        _pauseRequested = true;
        Logger.Info("OAG Focus: _pauseRequested = true");

        try
        {
            _captureCts?.Cancel();
            Logger.Info("OAG Focus: _captureCts.Cancel() выполнен");
        }
        catch (Exception ex)
        {
            Logger.Warning($"OAG Focus: ошибка при отмене _captureCts: {ex.Message}");
        }

        try
        {
            _seriesCts?.Cancel();
            Logger.Info("OAG Focus: _seriesCts.Cancel() выполнен");
        }
        catch (Exception ex)
        {
            Logger.Warning($"OAG Focus: ошибка при отмене _seriesCts: {ex.Message}");
        }

        if (!Settings.Default.DemoMode)
        {
            ForceAbortExposure();
        }
    }

    private void ForceAbortExposure()
    {
        Logger.Info("OAG Focus: ForceAbortExposure — остановка камеры");

        try
        {
            if (_camera != null)
            {
                _camera.AbortExposure();
                Logger.Info("OAG Focus: ✓ AbortExposure через ICameraMediator");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"OAG Focus: AbortExposure через Mediator: {ex.Message}");
        }

        try
        {
            var device = _camera?.GetDevice();
            if (device != null)
            {
                var abortMethod = device.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "AbortExposure" &&
                        m.GetParameters().Length == 0);

                if (abortMethod != null)
                {
                    abortMethod.Invoke(device, null);
                    Logger.Info("OAG Focus: ✓ AbortExposure через ICamera device");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"OAG Focus: AbortExposure через device: {ex.Message}");
        }

        try
        {
            var info = _camera?.GetInfo();
            if (info?.IsExposing == true)
            {
                _camera?.AbortExposure();
                Logger.Info("OAG Focus: ✓ Повторный AbortExposure (камера всё ещё экспонирует)");
            }
        }
        catch { }
    }

    private void ShowError(string message, MessageBoxImage icon)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            MessageBox.Show(message, "OAG Focus", MessageBoxButton.OK, icon);
        });
    }

    private async Task<double> GenerateDemoHfrAsync(CancellationToken token)
    {
        Logger.Info($"OAG Focus: GenerateDemoHfrAsync (задержка {(int)(ExposureTime * 1000)}мс)");

        try
        {
            await Task.Delay((int)(ExposureTime * 1000), token);
        }
        catch (OperationCanceledException)
        {
            Logger.Info("OAG Focus: GenerateDemoHfrAsync отменён");
            throw;
        }

        var random = new Random();
        int n = _focusService.HfrValues.Count;

        const double optimumIndex = 6.5;
        const double baseHfr = 2.0;
        const double vFactor = 0.25;

        double positionDeviation = Math.Abs(n - optimumIndex);
        double hfr = baseHfr + vFactor * positionDeviation * positionDeviation;
        hfr += (random.NextDouble() - 0.5) * 0.15;

        Logger.Info($"OAG Focus: Demo HFR = {hfr:F2}");
        return Math.Max(1.0, hfr);
    }

    private async Task<double> CaptureRealHfrAsync(CancellationToken token)
    {
        if (_camera is null)
            throw new InvalidOperationException("ICameraMediator не инициализирован.");

        if (_imaging is null)
            throw new InvalidOperationException("IImagingMediator не инициализирован.");

        var device = _camera.GetDevice();
        if (device is null || !device.Connected)
            throw new InvalidOperationException("Камера не подключена.");

        var cameraInfo = _camera.GetInfo();
        if (cameraInfo is null)
            throw new InvalidOperationException("Не удалось получить информацию о камере.");

        string cameraName = device.Name ?? device.DisplayName ?? "Unknown";
        Logger.Info($"OAG Focus: съёмка '{cameraName}', exp={ExposureTime:F2}с");

        short binX = Math.Max((short)1, cameraInfo.BinX);
        short binY = Math.Max((short)1, cameraInfo.BinY);
        int gain = cameraInfo.Gain;
        int offset = cameraInfo.Offset;

        var profile = _profileService?.ActiveProfile;
        if (profile?.CameraSettings is { } cameraSettings)
        {
            if (gain < 0) gain = cameraSettings.Gain ?? 0;
            if (offset < 0) offset = cameraSettings.Offset ?? 0;
        }

        var sequence = new CaptureSequence
        {
            ExposureTime = ExposureTime,
            Binning = new BinningMode(binX, binY),
            Gain = gain,
            Offset = offset,
            ImageType = CaptureSequence.ImageTypes.SNAPSHOT,
            TotalExposureCount = 1
        };

        try
        {
            token.ThrowIfCancellationRequested();

            var progress = new Progress<ApplicationStatus>(s =>
                Logger.Info($"OAG Focus: {s.Status}"));

            var exposureData = await _imaging.CaptureImage(
                sequence,
                token,
                progress);

            token.ThrowIfCancellationRequested();

            if (exposureData is null)
                throw new InvalidOperationException("Камера вернула null.");

            var imageData = await exposureData.ToImageData(progress, token);
            if (imageData is null)
                throw new InvalidOperationException("Не удалось получить IImageData.");

            var starAnalysis = imageData.StarDetectionAnalysis;
            double hfr = starAnalysis?.HFR ?? 0;
            int detectedStars = starAnalysis?.DetectedStars ?? 0;

            if (hfr <= 0 || double.IsNaN(hfr) || detectedStars == 0)
            {
                var renderedImage = imageData.RenderImage();
                if (renderedImage is not null)
                {
                    await renderedImage.DetectStars(
                        annotateImage: false,
                        sensitivity: StarSensitivityEnum.Normal,
                        noiseReduction: NoiseReductionEnum.None,
                        cancelToken: token,
                        progress: progress);

                    starAnalysis = imageData.StarDetectionAnalysis;
                    hfr = starAnalysis?.HFR ?? 0;
                    detectedStars = starAnalysis?.DetectedStars ?? 0;
                }
            }

            if (hfr <= 0 || double.IsNaN(hfr) || double.IsInfinity(hfr))
            {
                var starList = starAnalysis?.StarList;
                if (starList is { Count: > 0 })
                {
                    var validHfrs = starList
                        .Where(s => s.HFR > 0.5 && s.HFR < 20.0)
                        .Select(s => s.HFR)
                        .OrderBy(x => x)
                        .ToList();

                    if (validHfrs.Count > 0)
                    {
                        int topCount = Math.Max(1, validHfrs.Count / 2);
                        hfr = validHfrs.Take(topCount).Average();
                    }
                }
            }

            if (hfr <= 0 || double.IsNaN(hfr) || double.IsInfinity(hfr))
                throw new InvalidOperationException("Не удалось получить HFR.");

            Logger.Info($"OAG Focus: HFR={hfr:F2}, звёзд={detectedStars}");
            return hfr;
        }
        catch (OperationCanceledException)
        {
            ForceAbortExposure();
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException
                                       and not TimeoutException
                                       and not OperationCanceledException)
        {
            ForceAbortExposure();
            throw new InvalidOperationException($"Ошибка съёмки: {ex.Message}", ex);
        }
    }

    private void ApplyAnalysis()
    {
        var analysis = HfrAnalyzer.Analyze(_focusService.HfrValues);

        _focusService.CurrentHfr = analysis.CurrentHfr;
        _focusService.AverageHfr = analysis.AverageHfr;
        _focusService.MinHfr = analysis.MinHfr;
        _focusService.MaxHfr = analysis.MaxHfr;
        _focusService.TrendText = analysis.TrendText;

        FocusState = analysis.State;

        // Обновляем цвет статуса сразу после установки TrendText
        UpdateTrendBrush(analysis.TrendText);

        UpdateGraph(analysis);
    }

    private void UpdateGraph(HfrAnalyzer.AnalysisResult analysis)
    {
        var vals = _focusService.HfrValues;
        if (vals.Count == 0)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                GraphPoints = [];
                MinPoint = [];
            });
            return;
        }

        const double width = 650;
        const double height = 260;
        const double padding = 50;

        double minH = analysis.MinHfr;
        double maxH = analysis.MaxHfr;

        if (Math.Abs(maxH - minH) < 0.1) { minH -= 0.5; maxH += 0.5; }

        var filteredIndices = new List<int> { 0 };

        for (int i = 1; i < vals.Count; i++)
        {
            if (vals.Count <= MinPointsForFilter)
            {
                filteredIndices.Add(i);
                continue;
            }

            double currentHfr = vals[i];
            double previousFilteredHfr = vals[filteredIndices[^1]];
            double hfrChange = Math.Abs(currentHfr - previousFilteredHfr);

            if (hfrChange >= HfrChangeThreshold ||
                i == vals.Count - 1 ||
                i == analysis.MinHfrIndex)
            {
                filteredIndices.Add(i);
            }
        }

        var points = new PointCollection();
        double xStep = filteredIndices.Count <= 1 ? 0 : (width - 2 * padding) / (filteredIndices.Count - 1);

        for (int i = 0; i < filteredIndices.Count; i++)
        {
            int originalIndex = filteredIndices[i];
            double x = filteredIndices.Count == 1
                ? width / 2
                : padding + i * xStep;

            double y = height - padding - ((vals[originalIndex] - minH) / (maxH - minH) * (height - 2 * padding));
            points.Add(new Point(x, y));
        }

        var minPts = new PointCollection();
        if (analysis.MinHfrIndex >= 0 && filteredIndices.Contains(analysis.MinHfrIndex))
        {
            int filteredMinIndex = filteredIndices.IndexOf(analysis.MinHfrIndex);
            if (filteredMinIndex >= 0 && filteredMinIndex < points.Count)
            {
                minPts.Add(points[filteredMinIndex]);
            }
        }

        Application.Current?.Dispatcher?.Invoke(() =>
        {
            GraphPoints = points;
            MinPoint = minPts;
        });
    }

    private void OnServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentHfr));
        OnPropertyChanged(nameof(AverageHfr));
        OnPropertyChanged(nameof(MinHfr));
        OnPropertyChanged(nameof(MaxHfr));
        OnPropertyChanged(nameof(TrendText));
        OnPropertyChanged(nameof(FrameCount));

        // Обновляем цвет статуса при изменении TrendText в сервисе
        if (e.PropertyName == nameof(IOagFocusService.TrendText))
        {
            UpdateTrendBrush(TrendText);
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object parameter)
    {
        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler CanExecuteChanged;
    private void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object parameter) => _execute();
    public event EventHandler CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action<T> execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object parameter)
    {
        if (parameter is T value)
        {
            _execute(value);
        }
        else if (parameter is string s && typeof(T) == typeof(double) && double.TryParse(s, out var d))
        {
            _execute((T)(object)d);
        }
    }

    public event EventHandler CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}