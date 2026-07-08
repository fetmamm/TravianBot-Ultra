using System.Windows;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async Task<bool> HandleUnexpectedTravianLanguageAsync(UnexpectedTravianLanguageException ex)
    {
        _loopController.RequestQueueStop();
        _loopController.RequestLoopStop();
        AppendLog($"[language] Bot paused: {ex.Message}");
        return await ShowTravianLanguageGateAsync(ex.CurrentLanguage);
    }

    private async Task<bool> ShowTravianLanguageGateAsync(string? currentLanguage)
    {
        if (_travianLanguageGateActive)
        {
            AppendLog("[language] Language popup is already open.");
            return false;
        }

        _travianLanguageGateActive = true;
        try
        {
            if (Dispatcher.CheckAccess())
            {
                return ShowTravianLanguageGateCore(currentLanguage);
            }

            return await Dispatcher.InvokeAsync(() => ShowTravianLanguageGateCore(currentLanguage));
        }
        finally
        {
            _travianLanguageGateActive = false;
            if (Dispatcher.CheckAccess())
            {
                ToggleUiBusy(_uiBusy);
            }
            else
            {
                _ = Dispatcher.BeginInvoke(() => ToggleUiBusy(_uiBusy));
            }
        }
    }

    private bool ShowTravianLanguageGateCore(string? currentLanguage)
    {
        var options = LoadBotOptions();
        var token = _loopController.AcquireSessionScopeToken();
        var window = new TravianLanguageGateWindow(
            currentLanguage,
            async () =>
            {
                var language = await _botService.SetLanguageToEnglishAsync(options, AppendLog, token);
                if (string.Equals(language?.Trim(), TravianClient.ExpectedLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    AcknowledgeLanguageAlarmEntries();
                }

                return language;
            },
            async () => await _botService.ReadCurrentLanguageAsync(options, AppendLog, token),
            () => _loopController.IsClosing)
        {
            Owner = this,
        };

        var verified = window.ShowDialog() == true;
        if (window.ForceClosed)
        {
            AppendLog("[language] Language popup force-closed. Automation remains stopped; next login/start will check language again.");
        }

        return verified;
    }
}
