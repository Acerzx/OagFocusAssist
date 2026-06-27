using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OagFocusAssist.Services;

[System.ComponentModel.Composition.Export(typeof(IOagFocusService))]
[System.ComponentModel.Composition.PartCreationPolicy(System.ComponentModel.Composition.CreationPolicy.Shared)]
public sealed class OagFocusService : IOagFocusService
{
    private TaskCompletionSource _tcs = new();
    private readonly object _lock = new();

    public ObservableCollection<double> HfrValues { get; } = [];

    private double _currentHfr;
    public double CurrentHfr { get => _currentHfr; set => SetProperty(ref _currentHfr, value); }

    private double _averageHfr;
    public double AverageHfr { get => _averageHfr; set => SetProperty(ref _averageHfr, value); }

    private double _minHfr;
    public double MinHfr { get => _minHfr; set => SetProperty(ref _minHfr, value); }

    private double _maxHfr;
    public double MaxHfr { get => _maxHfr; set => SetProperty(ref _maxHfr, value); }

    private string _trendText = "—";
    public string TrendText { get => _trendText; set => SetProperty(ref _trendText, value); }

    private bool _isSessionActive;
    public bool IsSessionActive
    {
        get => _isSessionActive;
        set
        {
            if (SetProperty(ref _isSessionActive, value) && value)
            {
                lock (_lock) { _tcs = new TaskCompletionSource(); }
            }
        }
    }

    public Task WaitForCompletionAsync(CancellationToken token)
    {
        lock (_lock)
        {
            token.Register(() => _tcs.TrySetCanceled());
            return _tcs.Task;
        }
    }

    public void SignalCompletion()
    {
        lock (_lock) { _tcs.TrySetResult(); }
    }

    public void Reset()
    {
        HfrValues.Clear();
        CurrentHfr = 0;
        AverageHfr = 0;
        MinHfr = 0;
        MaxHfr = 0;
        TrendText = "—";
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}