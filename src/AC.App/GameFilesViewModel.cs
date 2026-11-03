using System.IO;
using AC.Core;
using AC.Infrastructure;
using Microsoft.Win32;

namespace AC.App;

/// <summary>Files of the open game: its achievements as CSV or JSON, and its tags to save or load.</summary>
public sealed class GameFilesViewModel
{
    private readonly MainViewModel _main;
    private readonly AppServices _svc;

    public GameFilesViewModel(MainViewModel main, AppServices svc)
    {
        _main = main;
        _svc = svc;
        ExportCsvCommand = new RelayCommand(p => Export("csv", p as string == "manual"), _ => main.Game != null);
        ExportJsonCommand = new RelayCommand(p => Export("json", p as string == "manual"), _ => main.Game != null);
        ExportTagsCommand = new RelayCommand(ExportTags, () => main.Game != null);
        ImportTagsCommand = new RelayCommand(ImportTags, () => main.Game != null);
    }

    /// <summary>Parameter: "manual" - the ones done but not counted, else all.</summary>
    public RelayCommand ExportCsvCommand { get; }
    /// <summary>Parameter: "manual" - the ones done but not counted, else all.</summary>
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportTagsCommand { get; }
    public RelayCommand ImportTagsCommand { get; }

    private void Export(string format, bool manualOnly)
    {
        if (_main.Game is not { } game) return;
        var scope = manualOnly ? ExportScope.ManualOnly : ExportScope.All;
        var dlg = new SaveFileDialog
        {
            FileName = $"{Sanitize(game.Name)}_{game.AppId}{(manualOnly ? "_not_counted" : "")}.{format}",
            Filter = format == "csv" ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (format == "csv") File.WriteAllBytes(dlg.FileName, Exporter.ToCsv(game, scope));
            else File.WriteAllText(dlg.FileName, Exporter.ToJson(game, scope));
            _main.StatusMessage = Loc.T("status.saved", dlg.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _main.StatusMessage = Loc.T("status.saveFailedLocked", e.Message);
        }
    }

    private void ExportTags()
    {
        if (_main.Game is not { } game) return;
        var dlg = new SaveFileDialog { FileName = $"{Sanitize(game.Name)}_{game.AppId}_tags.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, TagPack.From(game).ToJson());
            _main.StatusMessage = Loc.T("status.tagsSaved", dlg.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _main.StatusMessage = Loc.T("status.saveFailed", e.Message);
        }
    }

    private void ImportTags()
    {
        if (_main.Game is not { } game) return;
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = _svc.Games.ImportTags(game, File.ReadAllText(dlg.FileName));
            _main.Recompute(rebuildRows: true);
            _main.StatusMessage = Loc.Plural("status.tagsApplied", n);
        }
        catch (FormatException e)
        {
            _main.StatusMessage = e.Message;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            _main.StatusMessage = Loc.T("status.tagsBad", e.Message);
        }
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "game" : s;
    }
}
