using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using OagFocusAssist.Services;
using OagFocusAssist.Views;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OagFocusAssist.SequenceItems;

[ExportMetadata("Name", "Ручная фокусировка OAG")]
[ExportMetadata("Description", "Приостанавливает секвенсор для ручной фокусировки OAG с графиком HFR")]
[ExportMetadata("Icon", "AutoFocusSVG")]
[ExportMetadata("Category", "Фокусер")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
[method: ImportingConstructor]
public class OagManualFocusInstruction(
    IOagFocusService focusService,
    ICameraMediator camera,
    IImagingMediator imaging,
    IProfileService profileService) : SequenceItem
{
    private readonly IOagFocusService _focusService = focusService;
    private readonly ICameraMediator _camera = camera;
    private readonly IImagingMediator _imaging = imaging;
    private readonly IProfileService _profileService = profileService;

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        progress.Report(new ApplicationStatus { Status = "⏳ OAG Focus: открытие окна..." });

        _focusService.IsSessionActive = true;
        _focusService.Reset();

        Properties.Settings.Default.Save();

        var windowClosedTcs = new TaskCompletionSource<bool>();
        OagFocusWindow window = null;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            progress.Report(new ApplicationStatus { Status = "❌ Dispatcher недоступен" });
            return;
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    window = new OagFocusWindow(_focusService, _camera, _imaging, _profileService)
                    {
                        Topmost = true,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        ShowInTaskbar = true
                    };
                    window.Closed += (_, _) => windowClosedTcs.TrySetResult(true);
                    window.Show();
                    window.Activate();
                }
                catch (Exception ex)
                {
                    windowClosedTcs.TrySetException(ex);
                }
            });

            progress.Report(new ApplicationStatus { Status = "⏳ OAG Focus: ожидание..." });

            var userFinishedTask = _focusService.WaitForCompletionAsync(token);
            var completedTask = await Task.WhenAny(userFinishedTask, windowClosedTcs.Task);

            if (window is not null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    try { if (window.IsVisible) window.Close(); } catch { }
                });
            }

            progress.Report(new ApplicationStatus
            {
                Status = completedTask == userFinishedTask ? "✅ Завершено" : "✅ Окно закрыто"
            });
        }
        catch (OperationCanceledException)
        {
            progress.Report(new ApplicationStatus { Status = "❌ Отменено" });
            if (window is not null)
            {
                try
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        try { if (window.IsVisible) window.Close(); } catch { }
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            progress.Report(new ApplicationStatus { Status = $"❌ Ошибка: {ex.Message}" });
            throw;
        }
        finally
        {
            _focusService.IsSessionActive = false;
        }
    }

    private OagManualFocusInstruction(OagManualFocusInstruction copyMe)
        : this(copyMe._focusService, copyMe._camera, copyMe._imaging, copyMe._profileService)
    {
        CopyMetaData(copyMe);
    }

    public override object Clone()
    {
        return new OagManualFocusInstruction(this);
    }

    public override string ToString()
    {
        return $"Category: {Category}, Item: {Name}";
    }
}