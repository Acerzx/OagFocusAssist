using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace OagFocusAssist.Services;

public interface IOagFocusService : INotifyPropertyChanged
{
    ObservableCollection<double> HfrValues { get; }
    double CurrentHfr { get; set; }
    double AverageHfr { get; set; }
    double MinHfr { get; set; }
    double MaxHfr { get; set; }
    string TrendText { get; set; }
    bool IsSessionActive { get; set; }

    Task WaitForCompletionAsync(CancellationToken token);
    void SignalCompletion();
    void Reset();
}