using System.Globalization;
using AC.Core;

namespace AC.App;

/// <summary>Achievement table row. Edits go straight to MainViewModel (and to disk).</summary>
public sealed class AchievementRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private AchievementRow _row;
    private bool _manualDone;
    private AchievementKind _kind;
    private string _group;
    private string _note;
    private string _estimateInput;

    public AchievementRowViewModel(MainViewModel owner, AchievementRow row)
    {
        _owner = owner;
        _row = row;
        RevealCommand = new RelayCommand(() => _owner.Reveal(ApiName));
        _manualDone = row.Status == EffectiveStatus.ManualDone;
        _kind = row.Kind;
        _group = row.Group;
        _note = row.Note;
        _estimateInput = row.EstimateIsManual && row.EstimateMinutes.HasValue
            ? (row.EstimateMinutes.Value / 60.0).ToString("0.##", CultureInfo.CurrentCulture)
            : "";
    }

    /// <summary>The list heading the row goes under; null - the list is not grouped.</summary>
    public ListSection? Section { get; init; }

    /// <summary>The heading inside <see cref="Section"/>: the plan group.</summary>
    public ListSection? Subsection { get; init; }

    public string ApiName => _row.ApiName;
    public AchievementDef Def => _row.Def;
    public EffectiveStatus Status => _row.Status;
    public bool IsDone => _row.IsDone;
    public bool IsUnlocked => _row.Status == EffectiveStatus.Unlocked;
    public bool IsProtected => _row.Def.Protected;

    public string Name => _row.Def.Name.Length > 0 ? _row.Def.Name : _row.ApiName;

    /// <summary>A hidden unearned achievement's description is blurred until a click reveals it.</summary>
    public bool IsSpoilerHidden =>
        _row.Def.Hidden && !_row.IsDone && _row.Def.Description.Length > 0 && _owner.HideSpoilers && !_owner.IsRevealed(ApiName);

    public RelayCommand RevealCommand { get; }

    public void RefreshSpoiler()
    {
        OnPropertyChanged(nameof(IsSpoilerHidden));
        OnPropertyChanged(nameof(IsTipHidden));
    }

    private GuideTip? Tip => _owner.GuideTipFor(ApiName);

    public bool HasGuideTip => Tip is not null;

    /// <summary>What a guide says about getting it; empty when the guide only shows pictures or a video there.</summary>
    public string GuideText => _translated && _owner.TipTranslation(ApiName) is { } translation ? translation : Tip?.Text ?? "";

    private bool _translated;

    /// <summary>The words are in another language than the interface.</summary>
    public bool CanTranslate => Tip is { Text.Length: > 0 } t && t.Language != Loc.SteamLanguage(Loc.Language);

    public bool ShowsTranslation => _translated;

    public string TranslateLabel => Loc.T(_translated ? "details.original" : "details.translate");

    public void ShowTranslation(bool show)
    {
        _translated = show;
        OnPropertyChanged(nameof(GuideText));
        OnPropertyChanged(nameof(TranslateLabel));
    }

    public string GuideSource => Tip is not { } t ? ""
        : t.Author.Length > 0 ? Loc.T("details.guideSource", t.GuideTitle, t.Author)
        : Loc.T("details.guideSourceTitle", t.GuideTitle);

    /// <summary>A guide tells how to get a hidden achievement even when Steam gave no description: blurred the same way.</summary>
    public bool IsTipHidden => _row.Def.Hidden && !_row.IsDone && _owner.HideSpoilers && !_owner.IsRevealed(ApiName);

    private IReadOnlyList<GuidePictureViewModel>? _pictures;

    /// <summary>The tip's pictures, loaded when first asked for: only the selected achievement shows them.</summary>
    public IReadOnlyList<GuidePictureViewModel> GuidePictures => _pictures ??= (Tip?.Pictures ?? []).Select(u => new GuidePictureViewModel(u)).ToList();

    public void RefreshGuide()
    {
        _pictures = null;
        OnPropertyChanged(nameof(HasGuideTip));
        OnPropertyChanged(nameof(GuideText));
        OnPropertyChanged(nameof(GuideSource));
        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(GuidePictures));
    }

    public bool IsHidden => _row.Def.Hidden;
    public bool IsLocked => _row.Status == EffectiveStatus.Locked;
    public bool IsManualStatus => _row.Status == EffectiveStatus.ManualDone;
    public bool GroupIsAuto => _row.GroupIsAuto;
    public bool IsPendingWrite => _owner.IsPending(ApiName);
    public string KindTitle => Options.KindTitle(_kind);
    public bool HasKind => _kind != AchievementKind.Unknown;

    /// <summary>Rarity caption by the global percentage.</summary>
    public string RarityText
    {
        get
        {
            if (_row.Def.Percent is not { } p) return "";
            var rarity = Loc.T(p switch
            {
                < 5 => "rarity.veryRare",
                < 20 => "rarity.rare",
                < 50 => "rarity.uncommon",
                _ => "rarity.common",
            });
            return _row.Def.PercentFromSample ? Loc.T("rarity.fromSample", rarity) : rarity;
        }
    }

    /// <summary>0 - no data, 1 - common, 2 - uncommon, 3 - rare, 4 - very rare. For the color.</summary>
    public int RarityLevel => _row.Def.Percent switch
    {
        null => 0,
        < 5 => 4,
        < 20 => 3,
        < 50 => 2,
        _ => 1,
    };

    /// <summary>Time estimate source and both readings of it, for the tooltip.</summary>
    public string EstimateHint
    {
        get
        {
            var est = _owner.Game?.Estimates.GetValueOrDefault(ApiName);
            var source = _row.EstimateIsManual ? Loc.T("estimate.own") : est?.Source switch
            {
                AC.Core.SampleStats.SourceName => Loc.Plural("estimate.sample", est.SampleSize),
                AC.Core.TimeEstimator.SourceName => Loc.T("estimate.model"),
                _ => Loc.T("estimate.none"),
            };
            if (_row.EstimateMinutes is null) return source;
            var hint = source + "\n" + EstimateBoth;
            return Left is not { } c || !c.Late && !c.Passed ? hint
                : hint + "\n" + Loc.T(c.Passed ? _row.ProbablyMissed ? "estimate.missedTip" : "estimate.passedTip"
                    : c.Minutes.HasValue ? "estimate.lateTip" : "estimate.overdueTip");
        }
    }

    /// <summary>The estimate from the start of the game and, for a locked one, the time left to it.</summary>
    public string EstimateBoth
    {
        get
        {
            if (_row.EstimateMinutes is null) return "-";
            var fromStart = Loc.T("estimate.fromStart", TimeEstimator.Format(_row.EstimateMinutes));
            return Left is { } c ? fromStart + ", " + LeftText(c) : fromStart;
        }
    }

    private Countdown? Left => _row.IsDone ? null : _owner.CountdownOf(ApiName);

    /// <summary>
    /// Past the estimate it just takes longer. Gone past by the order of unlocks, a missable one was probably missed,
    /// and the others can wait.
    /// </summary>
    private string LeftText(Countdown c) => c.Minutes is { } m ? Loc.T("estimate.left", TimeEstimator.Format(m))
        : !c.Passed ? Loc.T("estimate.overdue")
        : Loc.T(_row.ProbablyMissed ? "card.missedShort" : "estimate.passed");

    /// <summary>The hours show the time left and it is past the estimate, or it was probably missed.</summary>
    public bool IsLate => _owner.ShowTimeLeft && Left is { } c && (c.Late || c.Passed && _row.ProbablyMissed);

    public string Description =>
        _row.Def.Description.Length > 0 ? _row.Def.Description :
        _row.Def.Hidden ? Loc.T("card.noDescription") : "";

    /// <summary>Overlay: the description; a hidden one without it says Steam gave none.</summary>
    public string OverlayDescription =>
        _row.Def.Description.Length > 0 ? _row.Def.Description
        : _row.Def.Hidden ? Loc.T("overlay.noDescription") : "";

    /// <summary>Writable to Steam: writes are on in settings, the achievement is not unlocked or protected.</summary>
    public bool CanWriteToSteam => _owner.ManualUnlocks && !IsUnlocked && !IsProtected;

    public string? Icon
    {
        get
        {
            var url = _row.IsDone ? _row.Def.IconUrl : _row.Def.IconGrayUrl;
            if (string.IsNullOrEmpty(url)) url = _row.Def.IconUrl;
            return string.IsNullOrEmpty(url) ? null : url;
        }
    }

    public string Percent => _row.Def.Percent is { } p ? p.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "-";
    public double PercentValue => _row.Def.Percent ?? -1;

    /// <summary>The hours in the chosen mode; a done one shows none when the mode is the time left.</summary>
    public string Estimate
    {
        get
        {
            var own = _row.EstimateIsManual ? " *" : "";
            if (!_owner.ShowTimeLeft) return TimeEstimator.Format(_row.EstimateMinutes) + own;
            if (_row.IsDone) return "";
            return Left is { } c ? LeftText(c) + (c.Minutes.HasValue ? own : "") : "-";
        }
    }

    public bool IsProbablyMissed => _row.ProbablyMissed;

    /// <summary>Shown in the overlay while locked; its "Hide" button takes it off.</summary>
    public bool InOverlay
    {
        get => !_row.OffOverlay;
        set
        {
            if (value != InOverlay) _owner.OnInOverlayChanged(this, value);
        }
    }

    public bool IsOffOverlay => IsLocked && _row.OffOverlay;

    /// <summary>The overlay pill: the estimate, or a warning for a missable one that was probably missed.</summary>
    public string OverlayBadge => _row.ProbablyMissed ? Loc.T("card.missedShort") : Estimate;

    public bool OverlayBadgeWarns => _row.ProbablyMissed || IsLate;

    /// <summary>The time left moved on: the play since the last unlock grew.</summary>
    public void RefreshTime()
    {
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(EstimateBoth));
        OnPropertyChanged(nameof(EstimateHint));
        OnPropertyChanged(nameof(IsLate));
    }

    public string StatusText => _row.Status switch
    {
        EffectiveStatus.Unlocked => _row.UnlockTime is { } t
            ? Loc.T("card.unlockedAt", t.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(Loc.Language)))
            : Loc.T("card.unlocked"),
        EffectiveStatus.ManualDone => Loc.T("card.manualDone"),
        _ => "",
    };

    /// <summary>"Done but not counted" checkbox. Unavailable once unlocked in Steam.</summary>
    public bool ManualDone
    {
        get => IsUnlocked || _manualDone;
        set
        {
            if (IsUnlocked) return;
            if (!Set(ref _manualDone, value)) return;
            _owner.OnManualDoneChanged(this, value);
        }
    }

    public AchievementKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            OnPropertyChanged(nameof(KindTitle));
            OnPropertyChanged(nameof(HasKind));
            _owner.OnKindChanged(this, value);
        }
    }

    public string Group
    {
        get => _group;
        set
        {
            value ??= "";
            if (!Set(ref _group, value)) return;
            _owner.OnGroupChanged(this, value);
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            value ??= "";
            if (!Set(ref _note, value)) return;
            _owner.OnNoteChanged(this, value);
        }
    }

    /// <summary>Manual estimate in hours, empty - automatic.</summary>
    public string EstimateInput
    {
        get => _estimateInput;
        set
        {
            value ??= "";
            if (!Set(ref _estimateInput, value)) return;
            double? minutes = null;
            var s = value.Trim().Replace(',', '.');
            if (s.Length > 0)
            {
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h <= 0)
                {
                    _owner.StatusMessage = Loc.T("status.estimateBad", value);
                    return;
                }
                minutes = h * 60;
            }
            _owner.OnEstimateChanged(this, minutes);
        }
    }

    // Derived properties only: input fields (Kind, Group, Note, EstimateInput) are skipped,
    // otherwise a background refresh erases what the user is typing.
    private static readonly string[] Derived =
    {
        nameof(Def), nameof(Status), nameof(IsDone), nameof(IsUnlocked), nameof(IsProtected), nameof(Name),
        nameof(Description), nameof(Icon), nameof(Percent), nameof(PercentValue),
        nameof(Estimate), nameof(StatusText), nameof(ManualDone), nameof(OverlayDescription),
        nameof(CanWriteToSteam), nameof(IsSpoilerHidden), nameof(IsTipHidden), nameof(IsHidden), nameof(IsLocked), nameof(IsManualStatus),
        nameof(GroupIsAuto), nameof(IsPendingWrite), nameof(RarityText), nameof(RarityLevel), nameof(EstimateHint),
        nameof(EstimateBoth), nameof(IsLate), nameof(InOverlay), nameof(IsOffOverlay),
    };

    /// <summary>Swaps the row data after a recalculation without recreating the object (keeps focus).</summary>
    public void Update(AchievementRow row)
    {
        _row = row;
        _manualDone = row.Status == EffectiveStatus.ManualDone;
        foreach (var n in Derived) OnPropertyChanged(n);
    }
}
