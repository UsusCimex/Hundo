using System.Windows.Input;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>A newer release: its version, and its download and notes opened in the browser.</summary>
public sealed class UpdateNoticeViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private UpdateInfo? _update;

    public UpdateNoticeViewModel(MainViewModel main, AppServices svc)
    {
        _svc = svc;
        DownloadCommand = new RelayCommand(() => main.OpenUrl(_update?.DownloadUrl), () => _update != null);
        NotesCommand = new RelayCommand(() => main.OpenUrl(_update?.PageUrl), () => _update != null);
    }

    public bool IsAvailable => _update != null;
    public string Text => _update is { } u ? Loc.T("update.available", u.Version.ToString(3)) : "";
    public RelayCommand DownloadCommand { get; }
    public RelayCommand NotesCommand { get; }

    public async Task CheckAsync()
    {
        if (!_svc.Settings.CheckUpdates) return;
        try
        {
            _update = await _svc.Updates.CheckAsync();
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Info("Update check: " + e.Message);
            return;
        }
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(Text));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The text in a new interface language.</summary>
    public void RefreshText() => OnPropertyChanged(nameof(Text));
}
