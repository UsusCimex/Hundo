using System.Runtime.Versioning;

namespace AC.Infrastructure;

/// <summary>
/// The app's services, built once over one data folder and one set of settings. Each does one job; what they change is
/// saved at once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppServices
{
    private readonly JsonStore _store;

    public AppSettings Settings { get; }
    public BridgeClient Bridge { get; }
    public UpdateService Updates { get; }
    public SteamLibrary Library { get; }
    public Games Games { get; }
    public GameLoader Loader { get; }
    public StatusReader Status { get; }
    public MarkService Marks { get; }
    public GuideTipService Guides { get; }
    public SampleCollector Sample { get; }

    public AppServices(JsonStore store, SteamWebApi api, BridgeClient bridge, SteamHunters hunters, UpdateService? updates = null)
    {
        _store = store;
        Bridge = bridge;
        Updates = updates ?? new UpdateService(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        Settings = store.LoadSettings();
        if (Settings.SteamId64 == 0 && SteamLocal.MostRecentUser() is { } u)
        {
            Settings.SteamId64 = u.SteamId;
            SaveSettings();
        }
        var percentages = new GlobalPercentages(api, hunters);
        Games = new Games(store, Settings);
        Library = new SteamLibrary(store, api, Settings);
        Status = new StatusReader(api, bridge, Games, percentages, Settings);
        Loader = new GameLoader(api, hunters, Games, Status, percentages, Library, Settings);
        Marks = new MarkService(bridge, store, Games, Settings);
        Guides = new GuideTipService(api, store, Settings);
        Sample = new SampleCollector(api, Games, Settings);
    }

    public static AppServices CreateDefault()
    {
        var http = new HttpClient(new HangGuard()) { Timeout = TimeSpan.FromSeconds(20) };
        return new AppServices(new JsonStore(), new SteamWebApi(http), new BridgeClient(), new SteamHunters(http));
    }

    public void SaveSettings() => _store.SaveSettings(Settings);
}
