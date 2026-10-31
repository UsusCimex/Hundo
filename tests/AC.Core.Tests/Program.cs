using System.Net;
using System.Reflection;
using System.Text;
using AC.Core;
using AC.Core.SteamFormats;

// Mini runner: every public static void method of the Tests class is a test.
// The app's own files (log, settings) go to a temporary folder, never to the user's data folder.
var dataDir = Path.Combine(Path.GetTempPath(), "ac-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable(AC.Infrastructure.AppPaths.DataDirVariable, dataDir);
int failed = 0, passed = 0;
Loc.SetLanguage(Loc.Fallback);
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    try
    {
        m.Invoke(null, null);
        passed++;
        Console.WriteLine($"OK    {m.Name}");
    }
    catch (TargetInvocationException e)
    {
        failed++;
        Console.WriteLine($"FAIL  {m.Name}: {e.InnerException?.Message}");
    }
}
Console.WriteLine($"Passed: {passed}, Failed: {failed}");
try { Directory.Delete(dataDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
return failed == 0 ? 0 : 1;

static class Assert
{
    public static void True(bool cond, string msg = "expected true")
    {
        if (!cond) throw new Exception(msg);
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected {expected}, got {actual}");
    }
}

static class Tests
{
    static GameData SampleGame()
    {
        var g = new GameData { AppId = 1, Name = "Test", CompletionMinutes = 600, PlaytimeMinutes = 0 };
        void Add(string id, string name, string desc, double pct) =>
            g.Achievements.Add(new AchievementDef { ApiName = id, Name = name, Description = desc, GlobalPercent = pct });
        Add("ch1", "Глава 1", "Пройдите главу 1", 90);
        Add("ch5", "Finale", "Complete chapter 5", 30);
        Add("miss", "Mercy", "Spare the guard before leaving the prison", 20);
        Add("grind", "Collector", "Collect all 300 feathers", 2);
        Add("misc", "Photographer", "Take a photo", 50);
        GameLogic.AutoClassify(g);
        TimeEstimator.EstimateFromGlobalPercent(g);
        return g;
    }

    public static void Classifier_Basic()
    {
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Глава 1", "Пройдите главу 1"));
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Finale", "Complete chapter 5"));
        Assert.Equal(AchievementKind.Missable, KindClassifier.Classify("Mercy", "Spare the guard before leaving the prison"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Collector", "Collect all 300 feathers"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Охотник", "Убейте 1000 врагов"));
        Assert.Equal(AchievementKind.Missable, KindClassifier.Classify("Призрак", "Пройдите уровень, ни разу не попавшись"));
        Assert.Equal(AchievementKind.Other, KindClassifier.Classify("Photographer", "Take a photo"));
        Assert.Equal(AchievementKind.Unknown, KindClassifier.Classify("", ""));
    }

    public static void Classifier_RussianFormsAndPlaces()
    {
        Loc.SetLanguage("ru");
        // Steam often uses the infinitive rather than the imperative
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Ягодные места", "Собрать 80 ягод"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Охотник", "Убить 50 врагов"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Следопыт", "Найти все секреты"));
        // a chapter mention is a place, not story
        Assert.Equal(AchievementKind.Other, KindClassifier.Classify("Сердце", "Получить Хрустальное сердце в главе 1"));
        Assert.Equal("Глава 1", KindClassifier.Location("Сердце", "Получить Хрустальное сердце в главе 1"));
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Сторона Б", "Завершить «Сторону Б» главы 2"));
        Assert.Equal("Глава 2", KindClassifier.Location("Сторона Б", "Завершить «Сторону Б» главы 2"));
        // a chapter number is not a grind counter
        Assert.Equal(AchievementKind.Other, KindClassifier.Classify("Тайник", "Получить ключ в главе 10"));
        Assert.Equal("Глава 10", KindClassifier.Location("Тайник", "Получить ключ в главе 10"));
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Farewell", "Complete Chapter 9"));
        Assert.Equal("Глава 9", KindClassifier.Location("Farewell", "Complete Chapter 9"));
        Assert.Equal("Акт II", KindClassifier.Location("Hidden Path", "Find the hidden path in Act II"));
        Assert.Equal((string?)null, KindClassifier.Location("Активист", "Активируйте 3 рычага"));
        Assert.Equal((string?)null, KindClassifier.Location("Photographer", "Take a photo"));
    }

    public static void AutoGroup_RespectsManualAndLegacy()
    {
        Loc.SetLanguage("ru");
        var g = SampleGame();
        Assert.Equal("Глава 5", g.Tags["ch5"].Group); // from "Complete chapter 5"
        Assert.True(!g.Tags["ch5"].GroupIsManual, "group detected automatically");
        Assert.True(GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "ch5").GroupIsAuto, "row knows the group is auto");

        GameLogic.SetGroup(g, "ch5", ""); // the user cleared the group - auto-detection will not bring it back
        GameLogic.AutoClassify(g);
        Assert.Equal("", g.Tags["ch5"].Group);

        GameLogic.SetGroup(g, "misc", "Фоторежим");
        GameLogic.AutoClassify(g);
        Assert.Equal("Фоторежим", g.Tags["misc"].Group);

        // old format: a group without a source was set by the user
        Assert.True(new AchievementTag { Group = "Старая группа" }.GroupIsManual);
        Assert.True(!new AchievementTag().GroupIsManual);
    }

    public static void Classifier_PlacesFollowLanguage()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Chapter 9", KindClassifier.Location("Farewell", "Complete Chapter 9"));
        Assert.Equal("Act II", KindClassifier.Location("Hidden Path", "Find the hidden path in Act II"));
        Assert.Equal("Episode 1", KindClassifier.Location("Сердце", "Пройти эпизод 1"));
    }

    public static void ManualKind_NotOverwrittenByAuto()
    {
        var g = SampleGame();
        GameLogic.SetKind(g, "misc", AchievementKind.Missable);
        GameLogic.AutoClassify(g);
        Assert.Equal(AchievementKind.Missable, g.Tags["misc"].Kind);
    }

    public static void Estimator_MonotonicByRarity()
    {
        var g = SampleGame();
        Assert.Equal(0.0, g.Estimates["ch1"].P50!.Value);
        Assert.Equal(600.0, g.Estimates["grind"].P50!.Value);
        Assert.True(g.Estimates["ch5"].P50 < g.Estimates["miss"].P50, "rarer must come later");
        Assert.True(g.Estimates["miss"].P90 > g.Estimates["miss"].P50, "P90 above P50");
    }

    public static void Estimator_NoCompletionTime_NoEstimates()
    {
        var g = SampleGame();
        g.CompletionMinutes = null;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(0, g.Estimates.Count);
    }

    public static void Estimator_ValidMinutes()
    {
        // JSON cannot hold infinity or NaN: a game holding one is never saved again
        foreach (var bad in new[] { double.PositiveInfinity, double.NaN, 0, -1, 1e8 })
            Assert.True(!TimeEstimator.IsValidMinutes(bad), $"{bad} is refused");
        Assert.True(TimeEstimator.IsValidMinutes(1) && TimeEstimator.IsValidMinutes(TimeEstimator.MaxMinutes));
    }

    public static void ManualEstimate_Wins()
    {
        var g = SampleGame();
        g.ManualEstimates["grind"] = 5;
        var row = GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "grind");
        Assert.Equal(5.0, row.EstimateMinutes);
        Assert.True(row.EstimateIsManual);
    }

    public static void Status_ManualThenSteam()
    {
        var g = SampleGame();
        // The first read tells nothing new: what is unlocked came before the app knew the game.
        Assert.Equal(0, GameLogic.ApplySteamState(g, new[] { ("misc", true, (DateTimeOffset?)null) }, DateTimeOffset.Now).Count);
        Assert.Equal(EffectiveStatus.Unlocked, GameLogic.StatusOf(g.States["misc"]));

        var s = g.StateOf("ch5");
        s.ManualDone = true;
        s.Note = "играл под другим AppID";
        Assert.Equal(EffectiveStatus.ManualDone, GameLogic.StatusOf(s));

        var t = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var fresh = GameLogic.ApplySteamState(g, new[] { ("ch5", true, (DateTimeOffset?)t) }, DateTimeOffset.Now);
        Assert.Equal(1, fresh.Count);
        Assert.Equal(EffectiveStatus.Unlocked, GameLogic.StatusOf(g.States["ch5"]));
        Assert.Equal("играл под другим AppID", g.States["ch5"].Note);

        fresh = GameLogic.ApplySteamState(g, new[] { ("ch5", true, (DateTimeOffset?)t) }, DateTimeOffset.Now);
        Assert.Equal(0, fresh.Count);
    }

    public static void ManualMarks_IgnoredWhenOff()
    {
        var g = SampleGame();
        g.CompletionMinutes = null;
        g.PlaytimeMinutes = 300;
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("grind").ManualDone = true;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(496.0, g.DerivedCompletionMinutes); // 300 minutes for the grind stretch, 60% of the scale

        g.CountManualMarks = false;
        Assert.Equal(EffectiveStatus.Locked, GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "grind").Status);
        Assert.Equal((1, 0, 5), GameLogic.Progress(g));
        Assert.True(Planner.Build(g, Percentile.P50).Ordered.Any(r => r.ApiName == "grind"), "without marks grind stays in the plan");
        Assert.Equal(0, new AchievementQuery { Status = StatusFilter.NotCounted }.Apply(g, Percentile.P50).Count);
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal((double?)null, g.DerivedCompletionMinutes);
        Assert.True(g.States["grind"].ManualDone, "the mark is kept, just not counted");
    }

    public static void Classifier_PlaceNumbers()
    {
        Assert.Equal(("chapter", 3), KindClassifier.PlaceIn("Collect the heart in Chapter 3")!.Value);
        Assert.Equal(("act", 4), KindClassifier.PlaceIn("Finish Act IV")!.Value);
        Assert.Equal(("episode", 9), KindClassifier.PlaceIn("Episode ix")!.Value);
        Assert.Equal(("chapter", 2), KindClassifier.PlaceIn("Глава 2")!.Value);
        Assert.True(KindClassifier.PlaceIn("Final run") is null, "no place");
    }

    public static void Missable_ProbablyMissed()
    {
        var g = SampleGame();
        GameLogic.SetGroup(g, "miss", "Chapter 3");
        Assert.Equal(0, GameLogic.ProbablyMissed(g).Count);

        g.StateOf("ch1").SteamUnlocked = true; // chapter 1 is over: chapter 3 is still ahead
        Assert.Equal(0, GameLogic.ProbablyMissed(g).Count);

        g.StateOf("ch5").SteamUnlocked = true; // chapter 5 is done: the chapter 3 one was left behind
        Assert.True(GameLogic.ProbablyMissed(g).Contains("miss"), "chapter 5 is done");
        Assert.True(GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "miss").ProbablyMissed, "flag on the row");

        g.StateOf("miss").SteamUnlocked = true;
        Assert.Equal(0, GameLogic.ProbablyMissed(g).Count);
    }

    public static void Planner_StageOrder()
    {
        var g = SampleGame();
        var plan = Planner.Build(g, Percentile.P50);
        var order = plan.Ordered.Select(r => r.ApiName).ToList();
        Assert.Equal("miss", order[0]);
        Assert.True(order.IndexOf("ch1") < order.IndexOf("ch5"), "story ordered by time");
        Assert.Equal("grind", order[^1]);
    }

    public static void Planner_SkipsDone_AndRemaining()
    {
        var g = SampleGame();
        g.StateOf("grind").ManualDone = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.True(plan.Ordered.All(r => r.ApiName != "grind"), "done ones are not in the plan");
        // The rarest one is done (luck or a mark): the others cost their stretches, together the way up to miss.
        Assert.Equal(Math.Round(g.Estimates["miss"].P50!.Value, 1), Math.Round(plan.RemainingMinutes!.Value, 1));

        // ch5 is done as well: misc and miss are left, each with its own stretch.
        g.StateOf("ch5").SteamUnlocked = true;
        plan = Planner.Build(g, Percentile.P50);
        double expected = g.Estimates["misc"].P50!.Value + (g.Estimates["miss"].P50!.Value - g.Estimates["ch5"].P50!.Value);
        Assert.Equal(Math.Round(expected, 1), Math.Round(plan.RemainingMinutes!.Value, 1));
        Assert.True(!plan.IsComplete, "not all done");

        // All done is complete; a game without achievements (or before their list came) is not.
        foreach (var a in g.Achievements) g.StateOf(a.ApiName).SteamUnlocked = true;
        Assert.True(Planner.Build(g, Percentile.P50).IsComplete, "all done");
        Assert.True(!Planner.Build(new GameData { AppId = 2 }, Percentile.P50).IsComplete, "no achievements");
    }

    public static void Planner_IdlePlaytimeChangesNothing()
    {
        // Hours without a single unlock (AFK, a game left open) neither shorten nor stretch the estimate.
        var g = SampleGame();
        foreach (var minutes in new[] { 0, 590, 59000 })
        {
            g.PlaytimeMinutes = minutes;
            var plan = Planner.Build(g, Percentile.P50);
            Assert.Equal(1.0, plan.Pace);
            Assert.Equal(600.0, Math.Round(plan.RemainingMinutes!.Value, 1));
        }
    }

    public static void Planner_PaceFromOwnProgress()
    {
        // Slow player: 500 minutes played, yet only ch5 unlocked (its stretch is 81 of the median 600 minutes).
        var g = SampleGame();
        g.PlaytimeMinutes = 500;
        g.StateOf("ch5").SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.True(plan.Pace > 2.5 && plan.Pace <= Planner.MaxPace, $"pace {plan.Pace}");
        Assert.True(plan.RemainingMinutes > 600, $"remaining {plan.RemainingMinutes} with pace");

        // Fast player: the median is not sped up.
        g.PlaytimeMinutes = 60;
        plan = Planner.Build(g, Percentile.P50);
        Assert.Equal(1.0, plan.Pace);
        Assert.True(plan.RemainingMinutes <= 540.0001, $"remaining {plan.RemainingMinutes}");
    }

    public static void Planner_ManualEstimateTakesMargin()
    {
        // At P90 an own estimate gets the same margin as the model ones.
        var g = SampleGame();
        g.ManualEstimates["grind"] = 600;
        Assert.Equal(600.0, Math.Round(Planner.Build(g, Percentile.P50).RemainingMinutes!.Value, 1));
        Assert.Equal(Math.Round(600 * TimeEstimator.P90Factor, 1), Math.Round(Planner.Build(g, Percentile.P90).RemainingMinutes!.Value, 1));
    }

    public static void Planner_CompleteGame()
    {
        var g = SampleGame();
        foreach (var a in g.Achievements) g.StateOf(a.ApiName).SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.True(plan.IsComplete, "all unlocked");
        Assert.Equal((double?)null, plan.RemainingMinutes);
    }

    public static void Planner_TimeLeft()
    {
        // Nothing unlocked: the time left to each one is its time from the start.
        var g = SampleGame();
        var left = Planner.Build(g, Percentile.P50).TimeLeft;
        foreach (var name in new[] { "misc", "ch5", "miss", "grind" })
            Assert.Equal(Math.Round(g.Estimates[name].P50!.Value, 1), Math.Round(left[name], 1));

        // Played in order: the way from the last unlock to the next one.
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("misc").SteamUnlocked = true;
        left = Planner.Build(g, Percentile.P50).TimeLeft;
        Assert.True(!left.ContainsKey("misc"), "done ones have no time left");
        Assert.Equal(Math.Round(g.Estimates["ch5"].P50!.Value - g.Estimates["misc"].P50!.Value, 1), Math.Round(left["ch5"], 1));

        // A lucky rare unlock does not shorten the way to the earlier ones; the last one is the time to 100%.
        g.StateOf("grind").SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.Equal(Math.Round(g.Estimates["miss"].P50!.Value - g.Estimates["misc"].P50!.Value, 1), Math.Round(plan.TimeLeft["miss"], 1));
        Assert.Equal(Math.Round(plan.RemainingMinutes!.Value, 1), Math.Round(plan.TimeLeft.Values.Max(), 1));
    }

    public static void Planner_Groups()
    {
        var g = SampleGame();
        g.Achievements.Add(new AchievementDef { ApiName = "nd", Name = "No death", Description = "Finish without dying", GlobalPercent = 1 });
        GameLogic.AutoClassify(g);
        g.TagOf("miss").Group = "Финальный забег";
        g.TagOf("nd").Group = "финальный забег";
        var plan = Planner.Build(g, Percentile.P50);
        var section = plan.Sections.Single(s => s.Stage == PlanStage.Missable);
        Assert.Equal(1, section.Groups.Count);
        Assert.Equal(2, section.Groups[0].Items.Count);
    }

    public static void Query_FiltersAndSort()
    {
        var g = SampleGame();
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("misc").ManualDone = true;
        var q = new AchievementQuery { Status = StatusFilter.Open, Sort = SortMode.Rarity };
        var rows = q.Apply(g, Percentile.P50);
        Assert.True(rows.All(r => r.ApiName != "ch1" && r.ApiName != "misc"));
        Assert.Equal("grind", rows[0].ApiName);

        q = new AchievementQuery { Status = StatusFilter.Done, Sort = SortMode.Name };
        Assert.Equal("ch1,misc", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName).OrderBy(n => n)));
        q = new AchievementQuery { Status = StatusFilter.NotCounted };
        Assert.Equal("misc", q.Apply(g, Percentile.P50).Single().ApiName);

        q = new AchievementQuery { Search = "feather" };
        Assert.Equal(1, q.Apply(g, Percentile.P50).Count);

        q = new AchievementQuery();
        q.Kinds.Add(AchievementKind.Story);
        Assert.Equal(2, q.Apply(g, Percentile.P50).Count);
    }

    public static void Query_SortsByTimeLeft()
    {
        var g = SampleGame();
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("ch5").SteamUnlocked = true;
        var q = new AchievementQuery { Sort = SortMode.Time };
        Assert.Equal("ch1,misc,ch5,miss,grind", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName)));

        // By time left: the locked ones ahead first, the nearest on top, then the rest. misc is gone past (ch5 after
        // it is done): it has no time left to count.
        q.TimeLeft = Planner.Build(g, Percentile.P50).TimeLeft;
        Assert.Equal("miss,grind,ch1,misc,ch5", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName)));
    }

    public static void Query_SortsByUnlockDate()
    {
        var g = SampleGame();
        var now = DateTimeOffset.Now;
        g.StateOf("ch5").SteamUnlocked = true;
        g.StateOf("ch5").SteamUnlockTime = now.AddDays(-1);
        g.StateOf("grind").SteamUnlocked = true;
        g.StateOf("grind").SteamUnlockTime = now.AddHours(-1);
        // Unlocked ones newest first, then the locked ones as they are expected to come, not in the schema order.
        var q = new AchievementQuery { Sort = SortMode.UnlockTime };
        Assert.Equal("grind,ch5,ch1,misc,miss", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName)));
        q.Status = StatusFilter.Open;
        q.TimeLeft = new Dictionary<string, double> { ["miss"] = 5, ["misc"] = 50 };
        Assert.Equal("miss,misc,ch1", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName)));

        // Unlocked without a date: still before the locked ones, the list groups them apart.
        g.StateOf("miss").SteamUnlocked = true;
        q.Status = StatusFilter.All;
        q.TimeLeft = new Dictionary<string, double> { ["ch1"] = 10, ["misc"] = 50 };
        Assert.Equal("grind,ch5,miss,ch1,misc", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName)));
    }

    public static void Export_CsvHasBomAndEscapes()
    {
        var g = SampleGame();
        g.StateOf("misc").ManualDone = true;
        g.StateOf("misc").Note = "сделал; но \"не засчиталось\"";
        var bytes = Exporter.ToCsv(g, ExportScope.ManualOnly);
        Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.True(lines[1].Contains("\"сделал; но \"\"не засчиталось\"\"\""), lines[1]);
        Assert.True(lines[1].Contains("done_not_counted"));

        // With ";" the percentage has a decimal comma: Excel would read 50.0 as a date. With "," it keeps the point.
        Assert.True(lines[1].Contains(";50,0;"), lines[1]);
        var comma = Encoding.UTF8.GetString(Exporter.ToCsv(g, ExportScope.ManualOnly, ',')).Split(Environment.NewLine)[1];
        Assert.True(comma.Contains(",50.0,"), comma);

        // A text that starts like a formula stays text in Excel.
        g.StateOf("misc").Note = "=HYPERLINK(\"http://x/\";\"open\")";
        g.Achievements.Single(a => a.ApiName == "misc").Name = "@sum";
        var formula = Encoding.UTF8.GetString(Exporter.ToCsv(g, ExportScope.ManualOnly)).Split(Environment.NewLine)[1];
        Assert.True(formula.Contains(";'@sum;") && formula.EndsWith(";\"'=HYPERLINK(\"\"http://x/\"\";\"\"open\"\")\""), formula);
    }

    public static void TagPack_RoundTrip()
    {
        var g = SampleGame();
        GameLogic.SetKind(g, "misc", AchievementKind.Missable);
        GameLogic.SetGroup(g, "ch5", "Глава 5");
        GameLogic.SetGroup(g, "ch1", ""); // a manual empty group is not exported
        g.ManualEstimates["grind"] = 120;
        g.StateOf("miss").Note = "  Talk to the guard twice  ";
        var json = TagPack.From(g).ToJson();

        var g2 = SampleGame();
        g2.StateOf("miss").Note = "My own note";
        var applied = TagPack.FromJson(json).ApplyTo(g2);
        Assert.Equal(4, applied);
        Assert.Equal("My own note\n\nTalk to the guard twice", g2.States["miss"].Note);
        TagPack.FromJson(json).ApplyTo(g2);
        Assert.Equal("My own note\n\nTalk to the guard twice", g2.States["miss"].Note); // not added twice
        Assert.Equal(AchievementKind.Missable, g2.Tags["misc"].Kind);
        Assert.Equal(KindSource.Manual, g2.Tags["misc"].Source);
        Assert.Equal("Глава 5", g2.Tags["ch5"].Group);
        Assert.True(g2.Tags["ch5"].GroupIsManual, "a group from the tags file is manual");
        Assert.Equal(120.0, g2.ManualEstimates["grind"]);
    }

    public static void TagPack_SkipsBadInput()
    {
        try
        {
            new TagPack { AppId = 2, Game = "Other" }.ApplyTo(SampleGame());
            Assert.True(false, "a file of another game is refused");
        }
        catch (FormatException e) { Assert.True(e.Message.Contains("Other"), e.Message); }

        var json = """
            { "AppId": 0, "Game": null, "CompletionMinutes": -1, "Items": [
              null,
              { "ApiName": null, "Note": "x" },
              { "ApiName": "ch1", "EstimateMinutes": 0, "Kind": 99 },
              { "ApiName": "ch5", "EstimateMinutes": 1e9 },
              { "ApiName": "grind", "EstimateMinutes": 90, "Kind": "Missable" } ] }
            """;
        var g = SampleGame();
        g.CompletionMinutes = null;
        Assert.Equal(3, TagPack.FromJson(json).ApplyTo(g));
        Assert.True(!g.ManualEstimates.ContainsKey("ch1") && !g.ManualEstimates.ContainsKey("ch5"), "a zero or huge estimate is skipped");
        Assert.Equal(90.0, g.ManualEstimates["grind"]);
        Assert.Equal(AchievementKind.Missable, g.Tags["grind"].Kind);
        Assert.True(g.Tags["ch1"].Source != KindSource.Manual, "an unknown kind is skipped");
        Assert.Equal(null, g.CompletionMinutes);
        Assert.Equal(0, TagPack.FromJson("""{ "Items": null }""").ApplyTo(g));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void MergeSchema_KeepsKnownData()
    {
        var g = SampleGame();
        g.Achievements[0].SamplePercent = 42;
        g.Achievements[0].Protected = true;
        var entries = g.Achievements
            .Select(a => new AC.Infrastructure.SchemaEntry(a.ApiName, a.Name + "!", "", a.Hidden, "icon.jpg", "gray.jpg"))
            .ToList();
        AC.Infrastructure.GameService.MergeSchema(g, entries);
        Assert.Equal(42.0, g.Achievements[0].SamplePercent);
        Assert.True(g.Achievements[0].Protected);
        Assert.Equal(90.0, g.Achievements[0].GlobalPercent);
        Assert.Equal("Глава 1!", g.Achievements[0].Name);
        Assert.Equal("Пройдите главу 1", g.Achievements[0].Description); // an empty value from the Web API does not overwrite a known one
    }

    public static void Loc_PluralCategories()
    {
        string Ru(long n) => Loc.Category("ru", n);
        Assert.Equal("one", Ru(1));
        Assert.Equal("few", Ru(2));
        Assert.Equal("many", Ru(5));
        Assert.Equal("many", Ru(11));
        Assert.Equal("one", Ru(21));
        Assert.Equal("few", Ru(32));
        Assert.Equal("many", Ru(112));
        Assert.Equal("many", Ru(0));
        Assert.Equal("one", Loc.Category("en", 1));
        Assert.Equal("other", Loc.Category("en", 0));
        Assert.Equal("other", Loc.Category("en", 21));
    }

    public static void Loc_SwitchAndFallback()
    {
        Assert.Equal("en", Loc.Languages[0]);
        Assert.True(Loc.Languages.Contains("ru"), "Russian is shipped");
        int changed = 0;
        void OnChanged() => changed++;
        Loc.Changed += OnChanged;
        try
        {
            Loc.SetLanguage("ru");
            Assert.Equal("Получены", Loc.T("list.done"));
            Assert.Equal("Загружено 21 достижение", Loc.Plural("status.gameLoaded", 21));
            Assert.Equal("russian", Loc.SteamLanguage(Loc.Language));
            Loc.SetLanguage("en");
            Assert.Equal("Unlocked", Loc.T("list.done"));
            Assert.Equal("Loaded 1 achievement", Loc.Plural("status.gameLoaded", 1));
            Assert.Equal("Loaded 21 achievements", Loc.Plural("status.gameLoaded", 21));
            Assert.Equal("no.such.key", Loc.T("no.such.key"));
            Assert.Equal("ru", Loc.Resolve("ru"));
            Assert.True(Loc.Languages.Contains(Loc.Resolve("xx")), "unknown language falls back to a shipped one");
            Assert.Equal(2, changed);
        }
        finally
        {
            Loc.Changed -= OnChanged;
            Loc.SetLanguage(Loc.Fallback);
        }
    }

    /// <summary>Every translation has the English keys and placeholders, its language's plural forms and formats cleanly.</summary>
    public static void Loc_TranslationsMatchFallback()
    {
        static string Base(string key) => key.Split('#')[0];
        static HashSet<string> Holes(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet();
        var english = Loc.Strings(Loc.Fallback);
        foreach (var lang in Loc.Languages)
        {
            var strings = Loc.Strings(lang);
            Assert.True(english.Keys.Select(Base).ToHashSet().SetEquals(strings.Keys.Select(Base)), $"{lang}: keys differ from English");
            var forms = lang == "ru" ? new[] { "few", "many", "one" } : new[] { "one", "other" };
            foreach (var group in strings.GroupBy(kv => Base(kv.Key)))
            {
                var expected = english.Where(kv => Base(kv.Key) == group.Key).SelectMany(kv => Holes(kv.Value)).ToHashSet();
                bool plural = group.First().Key.Contains('#');
                if (plural)
                    Assert.True(group.Select(kv => kv.Key.Split('#')[1]).Order().SequenceEqual(forms), $"{lang}: {group.Key} plural forms");
                foreach (var (key, text) in group)
                {
                    var holes = Holes(text);
                    Assert.True(plural ? holes.IsSubsetOf(expected) : holes.SetEquals(expected), $"{lang}: {key} placeholders differ from English");
                    _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, "a", "b", "c", "d");
                }
            }
        }
    }

    /// <summary>Keys used in code exist in the translation files, and the files have no extra keys.</summary>
    public static void Loc_KeysMatchCode()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AchievementCompanion.sln"))) root = root.Parent;
        Assert.True(root != null, "repository root not found");
        var src = Path.Combine(root!.FullName, "src");
        var sections = Loc.Strings(Loc.Fallback).Keys.Select(k => k.Split('.')[0]).ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?:""|\{local:Tr )([a-z][A-Za-z]*)\.([a-z][A-Za-z0-9]*)[""}]"))
            {
                // file names like "settings.json" look like keys
                if (m.Groups[2].Value is "json" or "jsonl" or "log" or "exe" or "dll" or "bin" or "vdf" or "zip") continue;
                if (sections.Contains(m.Groups[1].Value)) used.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }
        }
        foreach (var lang in Loc.Languages)
        {
            var keys = Loc.Strings(lang).Keys.Select(k => k.Split('#')[0]).ToHashSet();
            var missing = used.Where(k => !keys.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"{lang}: missing keys {string.Join(", ", missing)}");
            var unused = keys.Where(k => !used.Contains(k)).ToList();
            Assert.True(unused.Count == 0, $"{lang}: unused keys {string.Join(", ", unused)}");
        }
    }

    public static void BinaryKeyValue_SchemaParse()
    {
        // Binary KV built by hand: { "480" { "stats" { "1" { type=4 bits { "0" { name, display{...}, permission } } } } } }
        var ms = new MemoryStream();
        void B(byte b) => ms.WriteByte(b);
        void S(string s) { var x = Encoding.UTF8.GetBytes(s); ms.Write(x); B(0); }
        void Str(string k, string v) { B(1); S(k); S(v); }
        void Int(string k, int v) { B(2); S(k); ms.Write(BitConverter.GetBytes(v)); }
        void Open(string k) { B(0); S(k); }
        void End() => B(8);

        Open("480"); Open("stats"); Open("1");
        Int("type_int", 4);
        Open("bits");
        Open("0");
        Str("name", "ACH_WIN");
        Int("permission", 2);
        Open("display");
        Open("name"); Str("english", "Winner"); Str("russian", "Победитель"); End();
        Open("desc"); Str("english", "Win a game"); End();
        Int("hidden", 1);
        Str("icon", "abc.jpg");
        End(); // display
        End(); // 0
        End(); // bits
        End(); // 1
        End(); // stats
        End(); // 480
        End(); // root

        ms.Position = 0;
        var root = BinaryKeyValue.Read(ms);
        var list = StatsSchemaReader.Read(root, 480, "russian");
        Assert.Equal(1, list.Count);
        Assert.Equal("ACH_WIN", list[0].ApiName);
        Assert.Equal("Победитель", list[0].Name);
        Assert.Equal("Win a game", list[0].Description);
        Assert.True(list[0].Hidden);
        Assert.True(list[0].Protected);
        Assert.Equal("https://cdn.steamstatic.com/steamcommunity/public/images/apps/480/abc.jpg", StatsSchemaReader.IconUrl(480, list[0].Icon));
    }

    public static void SteamClient_ParseCommand()
    {
        var read = AC.Infrastructure.BridgeCommand.Parse(["read", "480", "--lang", "russian", "--ui", "ru"]);
        Assert.Equal(AC.Infrastructure.BridgeAction.Read, read.Action);
        Assert.Equal(480u, read.AppId);
        Assert.Equal("russian", read.Language);
        Assert.Equal("ru", read.UiLanguage);
        Assert.Equal(0, read.Names.Count);

        var unlock = AC.Infrastructure.BridgeCommand.Parse(["UNLOCK", "480", "A", "--lang", "english", "B", "A"]);
        Assert.Equal(AC.Infrastructure.BridgeAction.Unlock, unlock.Action);
        Assert.Equal("A,B", string.Join(",", unlock.Names));

        foreach (string[] bad in (string[][])[["read"], ["fly", "480"], ["read", "-1"], ["read", "0"], ["read", "4x"], ["lock", "480"]])
        {
            bool threw = false;
            try { AC.Infrastructure.BridgeCommand.Parse(bad); }
            catch (ArgumentException) { threw = true; }
            Assert.True(threw, "accepted " + string.Join(" ", bad));
        }
    }

    public static void Update_ParseRelease()
    {
        const string json = """
            {"tag_name": "v0.3.1", "html_url": "https://github.com/UsusCimex/Hundo/releases/tag/v0.3.1", "draft": false, "prerelease": false,
             "assets": [{"name": "notes.txt", "browser_download_url": "https://github.com/UsusCimex/Hundo/releases/download/v0.3.1/notes.txt", "size": 1},
                        {"name": "Hundo-v0.3.1-win-x64.zip", "browser_download_url": "https://github.com/UsusCimex/Hundo/releases/download/v0.3.1/a.zip",
                         "size": 42, "digest": "sha256:abcdef"},
                        {"name": "Hundo-v0.3.1-win-x64-setup.exe", "browser_download_url": "https://github.com/UsusCimex/Hundo/releases/download/v0.3.1/s.exe", "size": 40}]}
            """;
        static AC.Infrastructure.UpdateInfo? Parse(string json, string current = "0.3.0", bool installed = false) =>
            AC.Infrastructure.UpdateService.Parse(json, new Version(current), installed);
        var update = Parse(json)!;
        Assert.Equal(new Version(0, 3, 1), update.Version);
        Assert.Equal("https://github.com/UsusCimex/Hundo/releases/download/v0.3.1/a.zip", update.DownloadUrl);
        Assert.Equal("https://github.com/UsusCimex/Hundo/releases/tag/v0.3.1", update.PageUrl);
        Assert.True(Parse(json, "0.3.1") is null, "same version");
        Assert.True(Parse(json.Replace("\"prerelease\": false", "\"prerelease\": true")) is null, "prerelease");
        Assert.True(Parse(json.Replace("win-x64.zip", "linux.zip")) is null, "no Windows zip");
        Assert.Equal("https://github.com/UsusCimex/Hundo/releases/download/v0.3.1/s.exe", Parse(json, installed: true)!.DownloadUrl);
        Assert.True(Parse(json.Replace("setup.exe", "setup.msi"), installed: true) is null, "no setup");

        // The browser opens the links: only this repository's pages on GitHub, over https.
        foreach (var bad in new[] { "file:///C:/Windows/notepad.exe", @"\\\\host\\share\\x.exe", "http://github.com/UsusCimex/Hundo/x.zip",
                     "https://github.com.evil.example/UsusCimex/Hundo/x.zip", "https://github.com/someone/else/x.zip" })
            Assert.True(Parse(json.Replace("https://github.com/UsusCimex/Hundo/releases/download/v0.3.1/a.zip", bad)) is null, "accepted " + bad);
        Assert.Equal("", Parse(json.Replace("https://github.com/UsusCimex/Hundo/releases/tag/v0.3.1", "ms-settings:"))!.PageUrl);
        Assert.True(Parse("""{"tag_name": 5, "assets": {}}""") is null, "not the expected shape");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Steam_SignedInToAnotherAccount()
    {
        // The helper acts as whoever is signed in to the Steam client: only the account in the settings may be used.
        const ulong mine = 76561198000000000UL;
        uint account = (uint)(mine - 76561197960265728UL);
        Assert.True(!AC.Infrastructure.GameService.OtherAccount(account, mine), "the same account");
        Assert.True(AC.Infrastructure.GameService.OtherAccount(account + 1, mine), "another account");
        Assert.True(!AC.Infrastructure.GameService.OtherAccount(0, mine), "Steam not running: unknown");
        Assert.True(!AC.Infrastructure.GameService.OtherAccount(account + 1, 0), "no account in the settings");
    }

    public static void Vdf_LoginUsers()
    {
        const string vdf = """
        "users"
        {
            "76561198000000001"
            {
                "AccountName"   "old"
                "PersonaName"   "Old"
                "MostRecent"    "0"
            }
            // comment
            "76561198000000002"
            {
                "AccountName"   "usus"
                "PersonaName"   "Usus \"Cimex\""
                "MostRecent"    "1"
            }
        }
        """;
        var u = TextVdf.MostRecentUser(vdf);
        Assert.Equal(76561198000000002UL, u!.Value.SteamId);
        Assert.Equal("Usus \"Cimex\"", u.Value.PersonaName);
    }

    public static void Vdf_LibrariesAndRunningGames()
    {
        const string libraries = """
        "libraryfolders"
        {
            "0"
            {
                "path"      "C:\\Program Files (x86)\\Steam"
                "apps" { "228980" "0" }
            }
            "1"
            {
                "path"      "D:\\SteamLibrary"
            }
        }
        """;
        Assert.Equal(@"C:\Program Files (x86)\Steam|D:\SteamLibrary", string.Join("|", TextVdf.LibraryPaths(libraries)));
        const string manifest = """
        "AppState"
        {
            "appid"         "2315050"
            "name"          "We Were Here Tomorrow"
            "installdir"    "We Were Here Tomorrow"
        }
        """;
        Assert.Equal<(uint, string)?>((2315050u, "We Were Here Tomorrow"), TextVdf.AppManifest(manifest));

        // A process from the game's folder, whatever case Windows reports it in; a folder that only starts the same does not count.
        var dirs = new Dictionary<uint, string>
        {
            [2315050] = @"D:\SteamLibrary\steamapps\common\We Were Here Tomorrow",
            [677160] = @"D:\SteamLibrary\steamapps\common\We Were Here",
        };
        var running = RunningGames.ByProcess(
            [@"d:\steamlibrary\steamapps\common\We Were Here Tomorrow\Bin\WWHT.exe", @"C:\Windows\explorer.exe"], dirs);
        Assert.Equal("2315050", string.Join(",", running));

        // A game and its episodes in one folder: only the one Steam names runs, none when it names another.
        dirs = new Dictionary<uint, string>
        {
            [220] = @"C:\Steam\steamapps\common\Half-Life 2",
            [420] = @"C:\Steam\steamapps\common\Half-Life 2\",
        };
        string[] hl2 = [@"C:\Steam\steamapps\common\Half-Life 2\hl2.exe"];
        Assert.Equal("420", string.Join(",", RunningGames.ByProcess(hl2, dirs, named: 420)));
        Assert.Equal("", string.Join(",", RunningGames.ByProcess(hl2, dirs, named: 480)));
    }

    public static void SteamHunters_Parse()
    {
        const string app = """{"appId":413150,"name":"Stardew Valley","fastestCompletionTime":197,"medianCompletionTime":15674}""";
        const string achievements = """
        [{"achievementId":1,"apiName":"a0","name":"Greenhorn","steamPercentage":62.6,"localPercentage":94.6},
         {"achievementId":2,"apiName":"1","name":"Cowpoke","steamPercentage":55.3,"localPercentage":90.1}]
        """;
        Assert.Equal(15674.0, AC.Infrastructure.SteamHunters.ParseMedian(app));
        Assert.Equal((double?)null, AC.Infrastructure.SteamHunters.ParseMedian("""{"appId":1,"medianCompletionTime":0}"""));
        // A game the site does not know: it answers null.
        Assert.Equal((double?)null, AC.Infrastructure.SteamHunters.ParseMedian("null"));
        var pct = AC.Infrastructure.SteamHunters.ParsePercentages(achievements);
        Assert.Equal(62.6, pct["a0"]);
        Assert.Equal(2, pct.Count);
    }

    public static void SteamApi_ClassifiesFailures()
    {
        const string akamai = """
        <HTML><HEAD><TITLE>Access Denied</TITLE></HEAD><BODY><H1>Access Denied</H1>
        You don't have permission to access this server.<P>https&#58;&#47;&#47;errors&#46;edgesuite&#46;net&#47;18</P></BODY></HTML>
        """;
        const string badKey = "<html><head><title>Forbidden</title></head><body><h1>Forbidden</h1>Access is denied. Retrying will not help. Please verify your <pre>key=</pre> parameter.</body></html>";
        Assert.Equal(AC.Infrastructure.SteamFailure.Blocked, AC.Infrastructure.SteamWebApi.Classify(akamai)!.Value);
        Assert.Equal(AC.Infrastructure.SteamFailure.BadKey, AC.Infrastructure.SteamWebApi.Classify(badKey)!.Value);
        Assert.True(AC.Infrastructure.SteamWebApi.Classify("""{"playerstats":{"error":"Profile is not public","success":false}}""") is null,
            "a private profile is not fatal");
        Assert.True(new AC.Infrastructure.SteamApiException("x", null, AC.Infrastructure.SteamFailure.Blocked).Fatal, "blocked stops");
        Assert.True(!new AC.Infrastructure.SteamApiException("x", null, AC.Infrastructure.SteamFailure.Private).Fatal, "private goes on");
    }

    public static void StoreAssets_Parse()
    {
        const string items = """
        {"response":{"store_items":[{"appid":4450620,"assets":{"asset_url_format":"steam/apps/4450620/${FILENAME}?t=1786114521",
         "header":"697974067797577ee1803c37a44b4217d886502a/header.jpg","library_hero":"6655fbc0/library_hero.jpg"}}]}}
        """;
        var assets = AC.Infrastructure.StoreAssets.ParseItems(items);
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4450620/697974067797577ee1803c37a44b4217d886502a/header.jpg?t=1786114521",
            assets["header"]);
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4450620/6655fbc0/library_hero.jpg?t=1786114521",
            assets["library_hero"]);
        const string details = """{"3768760":{"success":true,"data":{"header_image":"https://x/3768760/c5b5/header_alt_assets_4.jpg?t=1"}}}""";
        Assert.Equal("https://x/3768760/c5b5/header_alt_assets_4.jpg?t=1", AC.Infrastructure.StoreAssets.ParseDetailsHeader(details, 3768760));
        Assert.Equal((string?)null, AC.Infrastructure.StoreAssets.ParseDetailsHeader("""{"1":{"success":false}}""", 1));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Service_BlockedNetworkWarnsOnceAndStopsTheSample()
    {
        const string blocked = "<HTML><HEAD><TITLE>Access Denied</TITLE></HEAD><BODY>errors&#46;edgesuite&#46;net</BODY></HTML>";
        var fake = new FakeHttp(url =>
            url.Contains("api.steampowered.com") ? (HttpStatusCode.Forbidden, blocked)
            : url.EndsWith("/api/apps/1/achievements") ? (HttpStatusCode.OK, HuntersPercentages)
            : url.EndsWith("/api/apps/1") ? (HttpStatusCode.OK, """{"appId":1,"medianCompletionTime":600}""")
            : url.Contains("/appreviews/") ? (HttpStatusCode.OK, Reviews)
            : (HttpStatusCode.NotFound, ""));
        WithService(fake, (svc, store) =>
        {
            var g = SampleGame();
            foreach (var a in g.Achievements) a.GlobalPercent = null;
            store.SaveGame(g);

            var report = svc.LoadGameAsync(1).GetAwaiter().GetResult();
            Assert.Equal(Loc.T("error.networkBlocked"), report.Warnings.Single());
            Assert.True(report.Game.Achievements.All(a => a.GlobalPercent is not null), "percentages from Steam Hunters");
            Assert.Equal(600.0, report.Game.CompletionMinutes);
            Assert.Equal(AC.Infrastructure.SteamHunters.Name, report.Game.CompletionSource);
            Assert.True(!report.PercentagesMissing, "percentages came from Steam Hunters");

            fake.Urls.Clear();
            try
            {
                svc.CollectSampleAsync(report.Game).GetAwaiter().GetResult();
                Assert.True(false, "a blocked network ends the sample");
            }
            catch (AC.Infrastructure.SteamApiException e)
            {
                Assert.Equal(AC.Infrastructure.SteamFailure.Blocked, e.Failure);
            }
            int asked = fake.Urls.Count(u => u.Contains("GetPlayerAchievements"));
            Assert.True(asked <= 4, $"stopped at the first answers, asked {asked}");
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Service_PercentagesMissingOnlyWhenNobodyHasThem()
    {
        var fake = new FakeHttp(url =>
            url.Contains("GetSchemaForGame") ? (HttpStatusCode.OK, Schema)
            : url.Contains("GetGlobalAchievementPercentagesForApp") ? (HttpStatusCode.OK, """{"achievementpercentages":{"achievements":[]}}""")
            : url.Contains("GetPlayerAchievements") ? (HttpStatusCode.OK, """{"playerstats":{"success":true,"achievements":[]}}""")
            : url.Contains("GetOwnedGames") ? (HttpStatusCode.OK, """{"response":{}}""")
            : (HttpStatusCode.NotFound, ""));
        WithService(fake, (svc, store) =>
        {
            var report = svc.LoadGameAsync(1, "Test").GetAwaiter().GetResult();
            Assert.Equal(5, report.Game.Achievements.Count);
            Assert.True(report.PercentagesMissing, "Steam and Steam Hunters answered with nothing");
            Assert.Equal((double?)null, report.Game.CompletionMinutes);
            Assert.True(report.Game.CompletionFetchedAt is not null, "asked once, not on every load");
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Service_ReloadKeepsEditsMadeMeanwhile()
    {
        GameData? open = null;
        var fake = new FakeHttp(url =>
        {
            if (!url.Contains("GetSchemaForGame")) return (HttpStatusCode.NotFound, "");
            if (open is not null) open.StateOf("miss").Note = "typed while Steam answers";
            return (HttpStatusCode.OK, Schema);
        });
        WithService(fake, (svc, store) =>
        {
            store.SaveGame(SampleGame());
            open = store.LoadGame(1)!;
            var report = svc.LoadGameAsync(1, forceSchema: true, current: open).GetAwaiter().GetResult();
            Assert.True(ReferenceEquals(open, report.Game), "the open game is reloaded in place");
            Assert.Equal("typed while Steam answers", store.LoadGame(1)!.States["miss"].Note);
        });
    }

    const string HuntersPercentages = """
        [{"apiName":"ch1","steamPercentage":90},{"apiName":"ch5","steamPercentage":30},{"apiName":"miss","steamPercentage":20},
         {"apiName":"grind","steamPercentage":2},{"apiName":"misc","steamPercentage":50}]
        """;

    const string Reviews = """
        {"success":1,"cursor":"next","reviews":[
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287931","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287932","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287933","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287934","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287935","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287936","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287937","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}},
         {"timestamp_created":1700000000,"author":{"steamid":"76561197960287938","playtime_at_review":100,"playtime_forever":200,"last_played":1700000000}}]}
        """;

    const string Schema = """
        {"game":{"gameName":"Test","availableGameStats":{"achievements":[
         {"name":"ch1","displayName":"Глава 1","description":"Пройдите главу 1","hidden":0},
         {"name":"ch5","displayName":"Finale","description":"Complete chapter 5","hidden":0},
         {"name":"miss","displayName":"Mercy","description":"Spare the guard before leaving the prison","hidden":0},
         {"name":"grind","displayName":"Collector","description":"Collect all 300 feathers","hidden":0},
         {"name":"misc","displayName":"Photographer","description":"Take a photo","hidden":0}]}}}
        """;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void WithService(FakeHttp fake, Action<AC.Infrastructure.GameService, AC.Infrastructure.JsonStore> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AC.Infrastructure.JsonStore(dir);
            // A known account, so the service does not read this machine's Steam login file: antivirus software
            // takes reading it next to DPAPI for a password stealer and freezes the process.
            store.SaveSettings(new AC.Infrastructure.AppSettings { SteamId64 = 76561197960287930 });
            var http = new HttpClient(fake);
            var svc = new AC.Infrastructure.GameService(store, new AC.Infrastructure.SteamWebApi(http),
                new AC.Infrastructure.BridgeClient(), new AC.Infrastructure.SteamHunters(http));
            svc.ApiKey = "00000000000000000000000000000000";
            body(svc, store);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void Guides_ReadsBBCodeLines()
    {
        var lines = GuideParser.Lines("""
            [h1]Chapter one[/h1]
            [list][*][b]First Steps[/b]
            [i]Finish the tutorial[/i]
            Talk to the old man twice, then jump.
            [*][b]Second Wind[/b] - Beat the boss
            [img]https://example.com/a.png[/img]
            [/list]
            [url=https://example.com]A link[/url] and [Note] stay
            [table]
              [tr]
                [td][previewimg=1;sizeThumb;a.jpg]Age[/previewimg][/td]
                [td]PLUMBER[/td]
              [/tr]
            [/table]
            [table][tr][td]Lone Wolf[/td][td]Never pick up a companion.[/td][/tr][/table]
            """);
        Assert.Equal("Chapter one|First Steps|Finish the tutorial|Talk to the old man twice, then jump.|Second Wind - Beat the boss||A link and [Note] stay||PLUMBER|Lone Wolf - Never pick up a companion.",
            string.Join("|", lines.Select(l => l.Text)));
        Assert.True(lines[0].Heading == 1 && lines[1].Bold == 1 && lines[4].Bold < 0.8, "heading and bold");
        Assert.True(lines[1].Item == 1 && lines[3].Item == 1 && lines[4].Item == 2 && lines[5].Media == 1 && lines[6].Item == 0, "list items and media");
        Assert.True(lines[7].Media == 1, "a picture's caption is not text");
        var unclosed = GuideParser.Lines("""
            [h1]Mr. Mayor[/h1]
            [previewimg=1;sizeThumb;a.jpg][previewyoutube=2;leftthumb][/previewyoutube]
            Use the D-Pad on the wall.
            [h1]A Secret Legend[/h1]
            """);
        Assert.Equal("Mr. Mayor||Use the D-Pad on the wall.|A Secret Legend", string.Join("|", unclosed.Select(l => l.Text)));
    }

    public static void Guides_FindsPictures()
    {
        var lines = GuideParser.Lines("""
            [b]Mercy[/b]
            [img]https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/1/icon.jpg[/img] Spare the guard.
            [screenshot=3261043900;sizeThumb,floatLeft;https://images.steamusercontent.com/ugc/1/AB/][/screenshot]
            [previewimg=11;sizeFull,inline;map.png][/previewimg][previewimg=12;sizeThumb,inline;berry.png][/previewimg][previewicon=13;sizeThumb,inline;ui.png]
            See [url=https://i.imgur.com/x.gif]this[/url] or https://example.com/route.jpg, not https://example.com/wiki
            [img]https://example.com/a.png[/img]
            """, [new GuidePicture("11", "https://images.steamusercontent.com/ugc/2/CD/", 90_000), new GuidePicture("12", "https://x/berry", 400),
            new GuidePicture("13", "https://x/ui", 90_000)]);
        Assert.Equal("Mercy|Spare the guard.|||See this or https://example.com/route.jpg, not https://example.com/wiki|",
            string.Join("|", lines.Select(l => l.Text)));
        Assert.Equal("|||https://images.steamusercontent.com/ugc/1/AB/|https://images.steamusercontent.com/ugc/2/CD/" +
            "|https://i.imgur.com/x.gif,https://example.com/route.jpg|https://example.com/a.png",
            "|" + string.Join("|", lines.Select(l => string.Join(",", l.Pictures))));

        var tips = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "", lines)]), "english",
            [new GuideAchievement("M", "Mercy", "")]);
        Assert.Equal(5, tips["M"].Pictures.Count);
        Assert.True(GuideParser.WorthShowing(1920, 1080) && GuideParser.WorthShowing(200, 600), "screenshots and tall maps");
        Assert.True(!GuideParser.WorthShowing(240, 240) && !GuideParser.WorthShowing(536, 91), "icons and pop-up banners");
    }

    public static void Guides_PicturesOnlyFromTheWeb()
    {
        // The app fetches them itself: over https, from named hosts, not from the local network.
        var lines = GuideParser.Lines("""
            [img]http://i.imgur.com/old.png[/img]
            [img]http://192.168.0.1/apply.cgi?dns=1.2.3.4[/img]
            [img]https://router/admin.png[/img]
            [img]https://example.com:8080/a.png[/img]
            [img]file:///C:/Windows/win.ini[/img]
            See http://example.com/route.jpg
            """, []);
        Assert.Equal("https://i.imgur.com/old.png,https://example.com/route.jpg", string.Join(",", lines.SelectMany(l => l.Pictures)));
    }

    public static void Guides_QuotesTheAchievementsEntry()
    {
        var section = new GuideSection("7", "Story", GuideParser.Lines("""
            [list][*][b]First Steps[/b]
            [i]Finish the tutorial[/i]
            Talk to the old man twice, then jump.
            [*][b]Second Wind[/b] - Beat the boss
            [img]https://example.com/a.png[/img][/list]
            Thanks for reading!
            [h2]3. Lone Wolf:[/h2]
            Never pick up a companion. The game counts only the first act.
            """));
        var tips = GuideMatcher.Match(new GuidePage(42, "All achievements", "Ann", [section]), "english", [
            new GuideAchievement("A", "First Steps", "Finish the tutorial"),
            new GuideAchievement("B", "Second Wind", "Beat the boss"),
            new GuideAchievement("C", "Lone Wolf", ""),
            new GuideAchievement("D", "Nowhere", "Not in this guide"),
        ]);
        Assert.Equal("Talk to the old man twice, then jump.", tips["A"].Text);
        Assert.Equal("", tips["B"].Text);
        Assert.Equal("Never pick up a companion. The game counts only the first act.", tips["C"].Text);
        Assert.True(!tips.ContainsKey("D"), "an achievement the guide does not name gets no tip");
        Assert.True(tips["A"].Rank == 0 && tips["C"].Rank == 2, "exact heading, normalized heading");
        var bilingual = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "", GuideParser.Lines("""
            [b]7 - Summit/Вершина.[/b]
            Climb the mountain without help.
            1UP! - Jump six times in a row
            [b]Ghost[/b]
            https://youtu.be/abc
            """))]), "english", [new GuideAchievement("S", "Summit", ""), new GuideAchievement("U", "1UP!", ""), new GuideAchievement("G", "Ghost", "")]);
        Assert.Equal("Climb the mountain without help.", bilingual["S"].Text);
        Assert.Equal("Jump six times in a row", bilingual["U"].Text);
        Assert.Equal("", bilingual["G"].Text);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=42#7", tips["A"].Url);
    }

    public static void Guides_SkipsWhatAddsNothing()
    {
        var tips = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "", GuideParser.Lines("""
            [b]Scholar[/b]
            Confirm half of the glyphs.
            1:31
            Keep a notebook open.
            [b]Ducks in a Row[/b]
            Видео в помощь: https://youtu.be/abc
            [b]Сигилов недостаточно.[/b]
            Sigils Are Not Enough
            [b]Ostrich[/b]
            08:46 - Yoyo
            16:03 - G. Flame and Ostrich
            C sides / Стороны C - https://youtu.be/x
            """)), new GuideSection("3", "11 - Fruitcake", GuideParser.Lines("""
            Description:
            Beat four advanced chambers.
            Where:
            Main menu, then Bonus maps.
            """))]), "russian", [
            new GuideAchievement("S", "Scholar", "Confirm half of all the glyphs in the game"),
            new GuideAchievement("D", "Ducks in a Row", ""),
            new GuideAchievement("N", "Сигилов недостаточно.", "Взять звезду.", "Sigils Are Not Enough"),
            new GuideAchievement("O", "Ostrich", "Find the G. flame"),
            new GuideAchievement("F", "Fruitcake", "Beat four advanced chambers."),
        ]);
        Assert.Equal("Keep a notebook open.", tips["S"].Text);
        Assert.Equal("", tips["D"].Text);
        Assert.Equal("", tips["N"].Text);
        Assert.Equal("", tips["O"].Text);
        Assert.Equal("Where:\nMain menu, then Bonus maps.", tips["F"].Text);
    }

    public static void Guides_SurestPlaceWins()
    {
        var tips = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "", GuideParser.Lines("""
            [b]Traversed World 5[/b]
            Run through the world from start to end, the shadow helps.
            [b]Pilgrim[/b]
            Short tip.
            [b]Pilgrims[/b]
            A much longer text about another achievement with a similar name.
            [b]Overachiver[/b]
            Do everything.
            """))]), "english", [
            new GuideAchievement("W", "Traversed World 6", ""),
            new GuideAchievement("P", "Pilgrim", ""),
            new GuideAchievement("O", "Overachiever", ""),
        ]);
        Assert.True(!tips.ContainsKey("W"), "another number is another achievement");
        Assert.Equal("Short tip.", tips["P"].Text);
        Assert.Equal("Do everything.", tips["O"].Text);
        var paired = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "", GuideParser.Lines("""
            [b]Achievements[/b]
            4) Left Behind - Через левое плечо - 1:31
            5) Obscure Foundations - Мрачный фундамент - 1:57
            Через левое плечо: прыгаем в проход от падения сейфа.
            """))]), "russian", [
            new GuideAchievement("L", "Через левое плечо", "", "Left Behind"),
            new GuideAchievement("O", "Мрачный фундамент", "", "Obscure Foundations"),
        ]);
        Assert.Equal("прыгаем в проход от падения сейфа.", paired["L"].Text);
        Assert.True(!paired.ContainsKey("O"), "a list pairing the names is not a place");
        var both = GuideMatcher.Match(new GuidePage(9, "\"Captain Did It\" and \"Abandon Ship\"", "Ann",
            [new GuideSection("1", "", GuideParser.Lines("Obra Dinn is great, its rarest achievements are not.\nFor Abandon Ship, row away before the end."))]),
            "english", [new GuideAchievement("A", "Abandon Ship", "Leave the ship early.")]);
        Assert.Equal("For Abandon Ship, row away before the end.", both["A"].Text);
    }

    public static void Guides_WalkthroughLeadsUpToTheAchievement()
    {
        var tips = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "Level 2", GuideParser.Lines("""
            [b]Please Use the Other Door[/b]
            Find the nook in Optical
            Go to the end of the corridor and pull out the dice in the trash can.
            [img]https://example.com/a.png[/img]
            Shrink it and put it back in the trash can. You'll then get the achievement:
            [img]https://example.com/b.png[/img]
            [b]Take Your Trash Elsewhere[/b]
            Fail at throwing away trash
            When you get to the room with the moon, take the cheese off it.
            Now on to the next achievement:
            [b]First Steps[/b]
            Talk to the old man twice.
            """))]), "english", [
            new GuideAchievement("D", "Please Use the Other Door", "Find the nook in Optical"),
            new GuideAchievement("T", "Take Your Trash Elsewhere", "Fail at throwing away trash"),
            new GuideAchievement("F", "First Steps", ""),
        ]);
        Assert.Equal("Go to the end of the corridor and pull out the dice in the trash can.\nShrink it and put it back in the trash can. You'll then get the achievement.",
            tips["T"].Text);
        Assert.Equal("Talk to the old man twice.", tips["F"].Text);
        var mentioned = GuideMatcher.Match(new GuidePage(1, "Guide", "Ann", [new GuideSection("2", "Level 3", GuideParser.Lines("""
            The achievement Soda Connaisseur is earned by collecting all six soda cans.
            Grab the cheese and take it to the corridor with the first fire alarm.
            Pull the fire alarm to get the achievement:
            [b]Fire Alarmist[/b]
            """))]), "english", [new GuideAchievement("S", "Soda Connaisseur", "Drink all sodas"), new GuideAchievement("A", "Fire Alarmist", "")]);
        Assert.Equal("Grab the cheese and take it to the corridor with the first fire alarm.\nPull the fire alarm to get the achievement.", mentioned["A"].Text);
    }

    public static void Guides_GuideAboutOneAchievement()
    {
        var sections = new[] { new GuideSection("1", "What is this", GuideParser.Lines("Blame every death on the captain.\nIt takes about an hour.")) };
        GuideAchievement[] achievements = [new("C", "Captain Did It", "Loosely true"), new("G", "Celeste", "Climb the mountain")];
        var tips = GuideMatcher.Match(new GuidePage(9, "How to Achievement: Captain Did It", "Ann", sections), "english", achievements, "Obra Dinn");
        Assert.Equal("Blame every death on the captain.\nIt takes about an hour.", tips["C"].Text);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=9", tips["C"].Url);
        var general = GuideMatcher.Match(new GuidePage(9, "Celeste 100% Guide", "Ann", sections), "english", achievements, "Celeste");
        Assert.True(general.Count == 0, "the game's own name in a title is not an achievement's");
        var pictures = GuideMatcher.Match(new GuidePage(9, "Fastest way to \"60 down 999,999,940 to go\"", "Ann",
            [new GuideSection("1", "", GuideParser.Lines("[img]https://example.com/a.png[/img]"))]), "english",
            [new GuideAchievement("S", "60 down 99,999,940 to go", "Solve 60 puzzles")], "Hexcells");
        Assert.True(pictures["S"].Text.Length == 0, "a misspelled long name still finds its guide, even one of pictures");
        var chapters = GuideMatcher.Match(new GuidePage(9, "Hints", "Ann",
            [new GuideSection("4", "Chapter I: Loose Cargo", GuideParser.Lines("Samuel was crushed by falling cargo."))]), "english",
            [new GuideAchievement("L", "Loose Cargo", "Solve all fates in chapter I.")]);
        Assert.Equal("Samuel was crushed by falling cargo.", chapters["L"].Text);
    }

    public static void Guides_PicksAchievementGuides()
    {
        var picked = GuideParser.Pick([
            new GuideCard(1, "How to get a real girlfriend", 0),
            new GuideCard(2, "Full Video Walkthrough [All achievements]", 0),
            new GuideCard(3, "Celeste 100% Guide", 0),
            new GuideCard(4, "Achievements Guide", 0),
            new GuideCard(5, "Руководство по всем достижениям", 0),
        ], 3);
        Assert.Equal("4,5,3", string.Join(",", picked.Select(c => c.Id)));
    }

    public static void Guides_CombineKeepsOwnLanguageWords()
    {
        static GuideTip Tip(string language, string text) => new(1, "Guide", "Ann", language, "", 0, text);
        var tips = GuideTips.Combine(
            new Dictionary<string, GuideTip> { ["a"] = Tip("russian", "Прыгни дважды"), ["b"] = Tip("russian", "") },
            new Dictionary<string, GuideTip> { ["a"] = Tip("english", "Jump twice"), ["b"] = Tip("english", "Jump"), ["c"] = Tip("english", "Run") });
        Assert.Equal("russian english english", $"{tips["a"].Language} {tips["b"].Language} {tips["c"].Language}");
    }

    public static void Guides_TranslatorReadsTheAnswer()
    {
        var answer = """[[["Прыгни дважды.\n","Jump twice.\n",null,null,10],["Потом беги.","Then run.",null,null,10]],null,"en",null,null,null,1,[]]""";
        Assert.Equal("Прыгни дважды.\nПотом беги.", AC.Infrastructure.Translator.Parse(answer, "ru"));
        Assert.True(AC.Infrastructure.Translator.Parse(answer, "en") is null, "a text already in that language");
    }

    public static void Guides_TipsSurviveStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tip = new GuideTip(42, "All achievements", "Ann", "english", "7", 0, "Jump twice") { Pictures = ["https://example.com/a.png"] };
            new AC.Infrastructure.JsonStore(dir).SaveGuides(5, new GuideTips
                { Format = GuideTips.CurrentFormat, Language = "english", FetchedAt = DateTimeOffset.Now, Tips = { ["A"] = tip } });
            var kept = new AC.Infrastructure.JsonStore(dir).LoadGuides(5)!;
            Assert.Equal(GuideTips.CurrentFormat, kept.Format);
            Assert.Equal(tip, kept.Tips["A"] with { Pictures = tip.Pictures });
            Assert.True(kept.Tips["A"].Pictures.SequenceEqual(tip.Pictures), "pictures survive");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void History_GroupsUnlocksByLocalDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var g = SampleGame();
        void Unlock(string id, DateTimeOffset? at)
        {
            var s = g.StateOf(id);
            s.SteamUnlocked = true;
            s.SteamUnlockTime = at;
        }
        Unlock("ch1", null);
        Unlock("misc", new DateTimeOffset(2026, 10, 1, 22, 30, 0, TimeSpan.Zero)); // already Oct 2 in UTC+3
        Unlock("ch5", new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        Unlock("miss", new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

        var days = UnlockHistory.ByDay(g, zone);
        Assert.Equal("10/02:misc,ch5:3 10/20:miss:4",
            string.Join(" ", days.Select(d => $"{d.Day.Month:00}/{d.Day.Day:00}:{string.Join(",", d.Unlocked.Select(a => a.ApiName))}:{d.Total}")));
        Assert.Equal<(DateOnly, DateOnly)?>((new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 20)),
            UnlockHistory.LongestBreak(days, new DateOnly(2026, 10, 25)));
        Assert.Equal<(DateOnly, DateOnly)?>((new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 30)),
            UnlockHistory.LongestBreak(days, new DateOnly(2026, 11, 30)));
    }

    public static void Session_TrackerReportsSessionsSeenFromTheStart()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        // Looked at only now and then here: no pause is taken for sleep.
        var tracker = new SessionTracker([7], t0.AddMinutes(-20), maxPause: TimeSpan.FromDays(1));
        Assert.Equal(0, tracker.Observe([], t0).Count);
        Assert.Equal(0, tracker.Observe([5], t0.AddMinutes(1)).Count);
        Assert.Equal(0, tracker.Observe([5], t0.AddMinutes(30)).Count);
        Assert.Equal(new PlaySession(5, t0.AddMinutes(1), t0.AddMinutes(90)), tracker.Observe([9], t0.AddMinutes(90)).Single());
        Assert.Equal(new PlaySession(9, t0.AddMinutes(90), t0.AddMinutes(95)), tracker.Observe([], t0.AddMinutes(95)).Single());

        // All play seen is kept: a game running at the start counts from the app's start, the running one up to now.
        Assert.Equal(new PlaySession(7, t0.AddMinutes(-20), t0), tracker.Played(7, t0.AddMinutes(100)).Single());
        tracker.Observe([5], t0.AddMinutes(120));
        Assert.Equal("1-90,120-130", string.Join(",", tracker.Played(5, t0.AddMinutes(130))
            .Select(s => $"{(s.Start - t0).TotalMinutes}-{(s.End - t0).TotalMinutes}")));
    }

    public static void Session_TwoGamesAtOnce()
    {
        // A game, then Spacewar started by an online fix next to it: each has its own session.
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var tracker = new SessionTracker([], t0, maxPause: TimeSpan.FromDays(1));
        tracker.Observe([5], t0.AddMinutes(1));
        Assert.Equal(0, tracker.Observe([5, 480], t0.AddMinutes(2)).Count);
        Assert.True(tracker.IsRunning(5) && tracker.IsRunning(480), "both run");
        Assert.Equal<TimeSpan?>(TimeSpan.FromMinutes(1), tracker.RunningFor(480, t0.AddMinutes(3)));
        Assert.Equal(new PlaySession(480, t0.AddMinutes(2), t0.AddMinutes(50)), tracker.Observe([5], t0.AddMinutes(50)).Single());
        Assert.Equal<TimeSpan?>(null, tracker.RunningFor(480, t0.AddMinutes(51)));
        Assert.Equal(new PlaySession(5, t0.AddMinutes(1), t0.AddMinutes(60)), tracker.Played(5, t0.AddMinutes(60)).Single());
    }

    public static void Session_SleepIsNotPlay()
    {
        // Looked at every 5 seconds; the computer sleeps from 18:30 to 20:30 with the game open.
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var tracker = new SessionTracker([], t0);
        for (var t = t0; t <= t0.AddMinutes(30); t = t.AddSeconds(5)) tracker.Observe([5], t);
        static double Minutes(IEnumerable<PlaySession> s) => Math.Round(s.Sum(p => (p.End - p.Start).TotalMinutes), 1);
        Assert.Equal(30.0, Minutes(tracker.Played(5, t0.AddMinutes(150))));

        // Woken up: the pause is left out, and the game counts as just started for the auto marks.
        Assert.Equal(0, tracker.Observe([5], t0.AddMinutes(150)).Count);
        Assert.Equal<TimeSpan?>(TimeSpan.Zero, tracker.RunningFor(5, t0.AddMinutes(150)));
        for (var t = t0.AddMinutes(150); t <= t0.AddMinutes(160); t = t.AddSeconds(5)) tracker.Observe([5], t);
        Assert.Equal(40.0, Minutes(tracker.Played(5, t0.AddMinutes(160))));

        // The session itself goes on through the pause: it ends when the game closes.
        Assert.Equal(new PlaySession(5, t0, t0.AddMinutes(161)), tracker.Observe([], t0.AddMinutes(161)).Single());
        Assert.Equal(41.0, Minutes(tracker.Played(5, t0.AddMinutes(170))));
    }

    public static void Progress_PlayedSinceTheLastUnlock()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var g = SampleGame();
        g.PlaytimeMinutes = 100;
        Assert.Equal(100.0, Progress.MinutesSinceProgress(g, t0)); // nothing unlocked: all play counts

        // ch1 at 18:00 between the points 17:00 (40 min) and 21:00 (200 min): the play is taken to start right after
        // 17:00, so 60 of the 160 minutes came before the unlock and 100 after it; 30 more since 21:00.
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0.AddHours(-1), Minutes = 40 });
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0.AddHours(3), Minutes = 200 });
        g.PlaytimeMinutes = 230;
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("ch1").SteamUnlockTime = t0;
        Assert.Equal(130.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddDays(1)), 1));

        // A mark counts as progress too, unless marks are off.
        g.StateOf("misc").ManualDone = true;
        g.StateOf("misc").ManualAt = t0.AddMinutes(190);
        Assert.Equal(20.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddDays(1)), 1));
        g.CountManualMarks = false;
        Assert.Equal(130.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddDays(1)), 1));

        g.PlaytimeOffsetMinutes = 30; // the ignored hours are the oldest ones
        Assert.Equal(130.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddDays(1)), 1));
    }

    public static void Progress_PlayedCountsSessionsSteamHasNot()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var g = SampleGame();
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0, Minutes = 300 });
        g.PlaytimeMinutes = 300;
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("ch1").SteamUnlockTime = t0.AddMinutes(-90);
        // The point at 18:00 came after the unlock: those 0..300 minutes are not all known to be after it.
        Assert.Equal(0.0, Progress.MinutesSinceProgress(g, t0.AddMinutes(30)));

        // A session from 19:00, running at 19:40: Steam's playtime does not show it yet.
        var running = new[] { new PlaySession(1, t0.AddHours(1), t0.AddMinutes(100)) };
        Assert.Equal(40.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddMinutes(100), running), 1));

        // misc came at 19:25: the countdown starts again from it.
        g.StateOf("misc").SteamUnlocked = true;
        g.StateOf("misc").SteamUnlockTime = t0.AddMinutes(85);
        Assert.Equal(15.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddMinutes(100), running), 1));

        // Steam's playtime was read after the session: its minutes are in the history now, not on top of it.
        var ended = new PlaySession(1, t0.AddHours(1), t0.AddMinutes(130));
        g.PlaytimeMinutes = 370;
        Progress.AddSessionStart(g, ended, 370);
        Progress.AddPlaytime(g, t0.AddMinutes(135), 370);
        Assert.Equal(45.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddHours(5), [ended]), 1));
    }

    public static void Progress_SteamCountsARunningSession()
    {
        // 300 minutes at 18:00, then a session from 18:00. Read again at 18:50, Steam's playtime shows those 50 minutes
        // already: at 19:00 an hour has passed since the unlock, not 110 minutes.
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var g = SampleGame();
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0, Minutes = 300 });
        g.PlaytimeMinutes = 350;
        g.PlaytimeReadAt = t0.AddMinutes(50);
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("ch1").SteamUnlockTime = t0.AddMinutes(-10);
        var running = new[] { new PlaySession(1, t0, t0.AddHours(1)) };
        Assert.Equal(60.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddHours(1), running), 1));
        Assert.Equal(360.0, Math.Round(Progress.PlaytimeNow(g, t0.AddHours(1), running), 1));

        // Steam's playtime beyond the session is play the app did not see.
        g.PlaytimeMinutes = 380;
        Assert.Equal(90.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddHours(1), running), 1));
    }

    public static void Progress_AppStartedAfterTheGame()
    {
        // 300 minutes at 17:00; the game started a little later. The app starts at 19:00 and reads 355 from Steam, so
        // those minutes are counted already: the session it sees from 19:00 goes on top of them.
        var t0 = new DateTimeOffset(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
        var g = SampleGame();
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0.AddHours(-2), Minutes = 300 });
        g.PlaytimeMinutes = 355;
        g.PlaytimeReadAt = t0;
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("ch1").SteamUnlockTime = t0.AddHours(-3);
        var seen = new[] { new PlaySession(1, t0, t0.AddMinutes(30)) };
        Assert.Equal(385.0, Math.Round(Progress.PlaytimeNow(g, t0.AddMinutes(30), seen), 1));
        Assert.Equal(85.0, Math.Round(Progress.MinutesSinceProgress(g, t0.AddMinutes(30), seen), 1));

        // The same with no history yet: the first load of the game is mid-session.
        g.PlaytimeHistory.Clear();
        Assert.Equal(385.0, Math.Round(Progress.PlaytimeNow(g, t0.AddMinutes(30), seen), 1));

        // The user's own playtime counts the play up to when it was set.
        g.PlaytimeOverrideMinutes = 3000;
        g.PlaytimeOverrideAt = t0.AddMinutes(20);
        Assert.Equal(3010.0, Math.Round(Progress.PlaytimeNow(g, t0.AddMinutes(30), seen), 1));
    }

    public static void Progress_PlaytimeNow()
    {
        // Steam's playtime goes on with the session the app sees, less the hours to ignore.
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var g = SampleGame();
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0, Minutes = 300 });
        g.PlaytimeMinutes = 300;
        Assert.Equal(300.0, Progress.PlaytimeNow(g, t0.AddHours(1)));
        var running = new[] { new PlaySession(1, t0, t0.AddHours(1)) };
        Assert.Equal(360.0, Math.Round(Progress.PlaytimeNow(g, t0.AddHours(1), running), 1));
        g.PlaytimeOffsetMinutes = 100;
        Assert.Equal(260.0, Math.Round(Progress.PlaytimeNow(g, t0.AddHours(1), running), 1));
    }

    public static void Progress_SessionStartJoinsTheHistory()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var session = new PlaySession(1, t0.AddHours(1), t0.AddMinutes(130.6));
        var g = SampleGame();
        g.PlaytimeHistory.Add(new PlaytimePoint { At = t0, Minutes = 300 });

        // Steam counted 70 of the 70.6 minutes: the start is the playtime before the session.
        Progress.AddSessionStart(g, session, 370);
        Assert.Equal(300.0, g.PlaytimeHistory.Single(p => p.At == session.Start).Minutes);

        // Steam has not counted the session yet: the start would contradict the history and is left out.
        g.PlaytimeHistory.RemoveAll(p => p.At == session.Start);
        Progress.AddSessionStart(g, session, 300);
        Assert.True(g.PlaytimeHistory.All(p => p.At != session.Start), "a session Steam has not counted is left out");
    }

    public static void Planner_CountDown()
    {
        var g = SampleGame();
        // The most common one is at the very start: it takes the play and still gets the least margin.
        var start = Planner.Build(g, Percentile.P50).CountDown(10);
        Assert.Equal(new Countdown(5, true), start["ch1"]);

        g.StateOf("ch1").SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        double misc = g.Estimates["misc"].P50!.Value, miscWide = g.Estimates["misc"].P90!.Value, ch5 = g.Estimates["ch5"].P50!.Value;
        Assert.Equal(new Countdown(misc - 50, false), plan.CountDown(50)["misc"]);
        Assert.True(Math.Abs(plan.CountDown(50)["ch5"].Minutes!.Value - (ch5 - 50)) < 0.01, "the next ones count down with it");

        // Past the estimate: more time with the wide margin, then none.
        var late = plan.CountDown(100)["misc"];
        Assert.True(late.Late && Math.Abs(late.Minutes!.Value - (miscWide - 100)) < 0.01, $"late {late}");
        Assert.Equal(new Countdown(null, true), plan.CountDown(miscWide + 1)["misc"]);

        // Hours without an unlock (AFK in a lobby) go to the nearest one only: the others keep their gap to it.
        var idle = plan.CountDown(5000);
        Assert.Equal(1, idle.Values.Count(c => c.Late));
        Assert.True(!idle["ch5"].Late && Math.Abs(idle["ch5"].Minutes!.Value - (ch5 - misc)) < 0.01, $"ch5 {idle["ch5"]}");

        // The time to 100% is the longest countdown: idle hours take only the way to the nearest one from it.
        var total = plan.RemainingAfter(50)!.Value;
        Assert.True(!total.Late && Math.Abs(total.Minutes!.Value - 550) < 0.01, $"total {total}");
        Assert.True(Math.Abs(plan.RemainingAfter(5000)!.Value.Minutes!.Value - (600 - misc)) < 0.01, "after idle hours");

        // With the wide margin chosen, past it there is only the least margin.
        var wide = Planner.Build(g, Percentile.P90).CountDown(miscWide + 5)["misc"];
        Assert.True(wide.Late && Math.Abs(wide.Minutes!.Value - (Planner.MinMarginMinutes - 5)) < 0.01, $"wide {wide}");
    }

    public static void Progress_Behind()
    {
        static string Behind(params (string Name, double At, bool Done)[] a) => string.Join(",", Progress.Behind(a, 15).Order());
        Assert.Equal("", Behind(("a", 0, false), ("b", 60, false)));
        // Skipped early: two thirds of the achievements up to the later done ones are done.
        Assert.Equal("a", Behind(("a", 0, false), ("b", 60, true), ("c", 120, true)));
        // A single rare one done early moves nothing.
        Assert.Equal("", Behind(("a", 0, false), ("b", 60, false), ("c", 120, false), ("d", 600, true)));
        // Nearby ones come about together: one just before the point reached is not behind.
        Assert.Equal("", Behind(("a", 60, false), ("b", 65, true), ("c", 70, true)));
    }

    public static void Planner_PassedLeaveTheWay()
    {
        // ch1 is skipped, misc and ch5 after it are done: the player has gone past it.
        var g = SampleGame();
        g.StateOf("misc").SteamUnlocked = true;
        g.StateOf("ch5").SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.Equal("ch1", string.Join(",", plan.Passed));
        Assert.Equal(new Countdown(null, false, true), plan.CountDown(30)["ch1"]);
        Assert.True(!GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "ch1").ProbablyMissed, "a story one can wait");
        // It takes no play and does not hold the next ones: the nearest one ahead counts down.
        double ch5 = g.Estimates["ch5"].P50!.Value, miss = g.Estimates["miss"].P50!.Value;
        var next = plan.CountDown(30)["miss"];
        Assert.True(!next.Late && Math.Abs(next.Minutes!.Value - (miss - ch5 - 30)) < 0.01, $"miss {next}");

        // A missable one gone past was probably missed; its stretch is left for the end of the way to 100%.
        g = SampleGame();
        foreach (var id in new[] { "ch1", "misc", "ch5", "grind" }) g.StateOf(id).SteamUnlocked = true;
        Assert.True(GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "miss").ProbablyMissed, "the missable one");
        plan = Planner.Build(g, Percentile.P50);
        Assert.Equal("miss", string.Join(",", plan.Passed));
        Assert.True(Math.Abs(plan.RemainingAfter(0)!.Value.Minutes!.Value - (miss - ch5)) < 0.01, $"left {plan.RemainingAfter(0)}");
    }

    public static void Planner_Due()
    {
        // ch1 is the most common, at the very start: due after the least margin, not at once.
        var g = SampleGame();
        Assert.Equal<string?>(null, Planner.Build(g, Percentile.P50).Due(0));
        Assert.Equal<string?>(null, Planner.Build(g, Percentile.P50).Due(Planner.MinMarginMinutes - 1));
        Assert.Equal("ch1", Planner.Build(g, Percentile.P50).Due(Planner.MinMarginMinutes));

        // Marked, the next one counts from it.
        g.StateOf("ch1").ManualDone = true;
        g.StateOf("ch1").ManualAt = DateTimeOffset.Now;
        var plan = Planner.Build(g, Percentile.P50);
        double misc = plan.TimeLeft["misc"];
        Assert.True(misc > 0 && plan.TimeLeft.Values.Min() == misc, $"misc is the nearest: {misc}");
        Assert.Equal<string?>(null, plan.Due(misc - 1));
        Assert.Equal("misc", plan.Due(misc));

        // Of two at the same point the most common goes first.
        g.ManualEstimates["grind"] = g.Estimates["misc"].P50!.Value;
        Assert.Equal("misc", Planner.Build(g, Percentile.P50).Due(misc));

        // A mark by time the user took back is left to them: the next one ahead is due after its own time.
        var back = SampleGame();
        back.StateOf("ch1").ManualDone = true;
        back.StateOf("ch1").ManualAt = DateTimeOffset.Now;
        back.StateOf("misc").AutoMarkedAt = DateTimeOffset.Now;
        plan = Planner.Build(back, Percentile.P50);
        Assert.True(plan.Ordered.Single(r => r.ApiName == "misc").AutoMarkUndone, "misc was taken back");
        var (next, left) = plan.TimeLeft.Where(kv => kv.Key != "misc").MinBy(kv => kv.Value);
        Assert.True(left > misc, $"{next} comes after misc: {left}");
        Assert.Equal<string?>(null, plan.Due(left - 1));
        Assert.Equal(next, plan.Due(left));
    }

    public static void Session_SummaryTakesBackItsUnlocks()
    {
        var start = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        static void Unlock(GameData g, string id, DateTimeOffset at)
        {
            var s = g.StateOf(id);
            s.SteamUnlocked = true;
            s.SteamUnlockTime = at;
        }
        var earlier = SampleGame();
        Unlock(earlier, "ch1", start.AddDays(-1));
        var g = SampleGame();
        Unlock(g, "ch1", start.AddDays(-1));
        Unlock(g, "misc", start.AddMinutes(70));
        Unlock(g, "ch5", start.AddMinutes(20));

        var session = new PlaySession(1, start, start.AddMinutes(90));
        var s = SessionSummary.Of(g, session, Percentile.P50, session.End, []);
        Assert.Equal("ch5,misc", string.Join(",", s.Unlocked.Select(a => a.ApiName)));
        Assert.Equal(TimeSpan.FromMinutes(90), s.Duration);
        Assert.Equal(Planner.Build(earlier, Percentile.P50).RemainingMinutes, s.LeftBefore);
        Assert.Equal(Planner.Build(g, Percentile.P50).RemainingMinutes, s.LeftAfter);
        Assert.True(s.LeftBefore > s.LeftAfter, $"before {s.LeftBefore}, after {s.LeftAfter}");
        Assert.True(g.States["ch5"].SteamUnlocked && g.States["misc"].SteamUnlocked, "the game is left as it was");

        // The app saw the session: the 20 minutes after misc count down the time to 100%, the play before does not.
        s = SessionSummary.Of(g, session, Percentile.P50, session.End, [session]);
        Assert.Equal(Planner.Build(earlier, Percentile.P50).RemainingMinutes, s.LeftBefore);
        Assert.Equal(Math.Round(Planner.Build(g, Percentile.P50).RemainingMinutes!.Value - 20, 1), Math.Round(s.LeftAfter!.Value, 1));
    }

    public static void Summary_SortsByTimeLeft()
    {
        var s = AC.Infrastructure.GameSummary.Of(SampleGame());
        Assert.Equal(600.0, s.RemainingMinutes);
        var near = s with { RemainingMinutes = 60 };
        var unknown = s with { RemainingMinutes = null };
        var done = s with { Unlocked = s.Total, RemainingMinutes = null };
        var order = new[] { done, unknown, s, near }
            .OrderBy(x => x, Comparer<AC.Infrastructure.GameSummary?>.Create(AC.Infrastructure.GameSummary.CompareByTimeLeft)).ToList();
        Assert.True(order[0] == near && order[1] == s && order[2] == unknown && order[3] == done, "nearest first, unknown after, finished last");
    }

    public static void Summary_OldFormatIsRebuilt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AC.Infrastructure.JsonStore(dir).SaveGame(SampleGame());
            File.WriteAllText(Path.Combine(dir, "summary.json"), """{"1":{"Unlocked":0,"Manual":0,"Total":5}}""");
            var summary = new AC.Infrastructure.JsonStore(dir).LoadSummaries()[1];
            Assert.Equal(600.0, summary.RemainingMinutes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void Library_SkipsComputedUrls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AC.Infrastructure.JsonStore(dir);
            store.SaveLibrary(new List<AC.Infrastructure.OwnedGame> { new() { AppId = 480, Name = "Spacewar", IconHash = "abc" } });
            var json = File.ReadAllText(Path.Combine(dir, "library.json"));
            Assert.True(!json.Contains("HeaderUrl") && !json.Contains("IconUrl"), "image URLs are computed, not written to the file");
            Assert.Equal("abc", store.LoadLibrary()!.Single().IconHash);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void JsonStore_KeepsReadableSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AC.Infrastructure.JsonStore(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
                { "Theme": "Neon", "SteamId64": 76561197960287930, "CsvSeparator": "ab", "OverlayToggleHotkey": "Ctrl+Alt+H",
                  "UiLanguage": null, "PollSeconds": 30, "FromNewerVersion": true }
                """);
            var s = store.LoadSettings();
            Assert.Equal(76561197960287930UL, s.SteamId64);
            Assert.Equal("Ctrl+Alt+H", s.OverlayToggleHotkey);
            Assert.Equal(30, s.PollSeconds);
            Assert.Equal(AC.Infrastructure.AppTheme.System, s.Theme);
            Assert.Equal(';', s.CsvSeparator);
            Assert.Equal(Loc.Fallback, s.UiLanguage);
            Assert.Equal(1, Directory.GetFiles(dir, "settings.json.broken-*").Length); // the file as it was
            Assert.True(!File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("Neon"), "written back, not set aside on every start");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void JsonStore_RoundTripAndLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AC.Infrastructure.JsonStore(dir);
            var g = SampleGame();
            g.StateOf("misc").ManualDone = true;
            g.StateOf("misc").Note = "заметка";
            g.StateOf("miss").OffOverlay = true;
            g.PendingUnlocks.Add("misc");
            GameLogic.SetKind(g, "ch1", AchievementKind.Grind);
            g.Tags["grind"] = new AchievementTag { Kind = AchievementKind.Grind, Group = "Старая группа" }; // old format, no GroupSource
            store.SaveGame(g);

            var back = store.LoadGame(1)!;
            Assert.Equal(5, back.Achievements.Count);
            Assert.True(back.States["misc"].ManualDone);
            Assert.Equal("заметка", back.States["misc"].Note);
            var offOverlay = GameLogic.BuildRows(back, Percentile.P50).Where(r => r.OffOverlay).Select(r => r.ApiName);
            Assert.Equal("miss", string.Join(",", offOverlay));
            Assert.Equal(AchievementKind.Grind, back.Tags["ch1"].Kind);
            Assert.Equal(KindSource.Manual, back.Tags["ch1"].Source);
            Assert.Equal("misc", back.PendingUnlocks.Single());
            Assert.Equal(KindSource.Auto, back.Tags["ch5"].GroupSource!.Value);
            Assert.True(back.Tags["grind"].GroupSource is null && back.Tags["grind"].GroupIsManual, "old file: the group is manual");

            // the library summary is updated on save and rebuilt if its file is missing
            var summary = store.LoadSummaries()[1];
            Assert.True(summary is { Unlocked: 0, Manual: 1, Total: 5, RemainingMinutes: > 0 }, $"summary {summary}");
            File.Delete(Path.Combine(dir, "summary.json"));
            Assert.Equal(summary, new AC.Infrastructure.JsonStore(dir).LoadSummaries()[1]);

            var s = store.LoadSettings();
            s.SteamId64 = 76561198000000002UL;
            s.WriteMode = AC.Infrastructure.WriteMode.Queue;
            store.SaveSettings(s);
            var s2 = store.LoadSettings();
            Assert.Equal(76561198000000002UL, s2.SteamId64);
            Assert.Equal(AC.Infrastructure.WriteMode.Queue, s2.WriteMode);
            Assert.Equal(';', s2.CsvSeparator);

            store.AppendLog(new AC.Infrastructure.UnlockLogEntry { At = DateTimeOffset.Now, AppId = 1, ApiName = "misc", Action = "unlock", Ok = true });
            store.AppendLog(new AC.Infrastructure.UnlockLogEntry { At = DateTimeOffset.Now, AppId = 2, ApiName = "x", Action = "unlock", Ok = false, Error = "e" });
            Assert.Equal(1, store.ReadLog(1).Count);
            Assert.Equal(2, store.ReadLog().Count);

            File.WriteAllText(store.GamePath(3), "{ broken");
            Assert.True(store.LoadGame(3) is null, "a broken file loads as null");
            Assert.True(Directory.GetFiles(Path.Combine(dir, "games"), "3.json.broken-*").Length == 1, "a copy of the broken file");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void Playtime_OverrideAndOffset()
    {
        var g = SampleGame();
        g.PlaytimeMinutes = 6000; // 100 h in Steam, but achievements were added later
        foreach (var id in new[] { "ch1", "misc", "ch5" }) g.StateOf(id).SteamUnlocked = true;
        var slow = Planner.Build(g, Percentile.P50);
        Assert.Equal(Planner.MaxPace, slow.Pace);

        g.PlaytimeOffsetMinutes = 5900; // 98 h 20 min were before achievements
        Assert.Equal(100.0, g.EffectivePlaytimeMinutes);
        var withOffset = Planner.Build(g, Percentile.P50);
        Assert.Equal(1.0, withOffset.Pace);
        Assert.True(withOffset.RemainingMinutes < slow.RemainingMinutes, "the offset removes the slow pace");

        g.PlaytimeOverrideMinutes = 400; // the override beats the offset
        Assert.Equal(400.0, g.EffectivePlaytimeMinutes);
        Assert.True(Planner.Build(g, Percentile.P50).Pace > 2, "400 minutes up to ch5 is slow");

        g.PlaytimeOffsetMinutes = 99999;
        g.PlaytimeOverrideMinutes = null;
        Assert.Equal(0.0, g.EffectivePlaytimeMinutes);
    }

    public static void Progress_IdleStretchesDoNotSlowDown()
    {
        var now = DateTimeOffset.Now;
        var g = SampleGame();
        foreach (var id in new[] { "ch1", "misc", "ch5" })
        {
            g.StateOf(id).SteamUnlocked = true;
            g.StateOf(id).SteamUnlockTime = now.AddDays(-10);
        }
        g.PlaytimeMinutes = 3000;
        // Without history all 3000 minutes went into reaching ch5: the slowest pace.
        Assert.Equal(Planner.MaxPace, Planner.Build(g, Percentile.P50, now).Pace);

        // Steam showed 200 minutes the day after ch5; the next 2800 brought nothing: idle, not slow.
        g.PlaytimeHistory.Add(new PlaytimePoint { At = now.AddDays(-9), Minutes = 200 });
        var plan = Planner.Build(g, Percentile.P50, now);
        Assert.True(plan.Pace is > 1.1 and < 1.2, $"pace {plan.Pace}");

        // The time to 100% derived from own progress skips the idle hours too.
        g.CompletionMinutes = null;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.True(g.DerivedCompletionMinutes is > 650 and < 750, $"derived {g.DerivedCompletionMinutes}");
    }

    public static void Progress_EarlyRareUnlockDoesNotFreezePace()
    {
        // miss (20%) came first, in 30 minutes: it brings only its own stretch, and later unlocks still count.
        var now = DateTimeOffset.Now;
        var g = SampleGame();
        void Unlock(string id, int daysAgo)
        {
            g.StateOf(id).SteamUnlocked = true;
            g.StateOf(id).SteamUnlockTime = now.AddDays(-daysAgo);
        }
        Unlock("miss", 10);
        g.PlaytimeHistory.Add(new PlaytimePoint { At = now.AddDays(-9), Minutes = 30 });
        foreach (var id in new[] { "ch1", "misc", "ch5" }) Unlock(id, 2);
        g.PlaytimeHistory.Add(new PlaytimePoint { At = now.AddDays(-1), Minutes = 463 });
        g.PlaytimeMinutes = 463;

        // 433 minutes for the way up to ch5, 173 minutes for the median player.
        var plan = Planner.Build(g, Percentile.P50, now);
        Assert.True(plan.Pace is > 2.4 and < 2.6, $"pace {plan.Pace}");
        double grindStretch = g.Estimates["grind"].P50!.Value - g.Estimates["miss"].P50!.Value;
        Assert.Equal(Math.Round(grindStretch * plan.Pace, 1), Math.Round(plan.RemainingMinutes!.Value, 1));

        g.CompletionMinutes = null;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.True(g.DerivedCompletionMinutes is > 1400 and < 1600, $"derived {g.DerivedCompletionMinutes}");
    }

    public static void Progress_Stretches()
    {
        var s = Progress.Stretches(new[] { ("c", 30.0), ("a", 10.0), ("b", 10.0) });
        Assert.Equal(5.0, s["a"]); // a and b share the way to 10
        Assert.Equal(5.0, s["b"]);
        Assert.Equal(20.0, s["c"]);
    }

    public static void Estimator_DerivedFromOwnProgress()
    {
        var g = SampleGame();
        g.CompletionMinutes = null;
        g.PlaytimeMinutes = 300;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(0, g.Estimates.Count); // nothing earned - nothing to derive from

        foreach (var id in new[] { "ch1", "misc", "ch5" }) g.StateOf(id).SteamUnlocked = true; // about 29% of the scale
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.True(g.DerivedCompletionMinutes is > 300, $"derived {g.DerivedCompletionMinutes}");
        Assert.True(g.Estimates.Count == 5, "estimates appeared");
        Assert.Equal(g.DerivedCompletionMinutes, g.UsedCompletionMinutes);

        g.CompletionMinutes = 1000; // a set value beats a derived one
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal((double?)null, g.DerivedCompletionMinutes);
        Assert.Equal(1000.0, g.Estimates["grind"].P50!.Value);
    }

    public static void SampleStats_PercentAndTimes()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);
        PlayerSample P(int hoursTotal, params (string Name, double HoursIn)[] unl)
        {
            // every real hour is exactly 60 minutes of play, to check the conversion
            var p = new PlayerSample();
            foreach (var (n, h) in unl) p.Unlocks[n] = t0.AddHours(h);
            p.Anchors.Add((t0.AddHours(hoursTotal), hoursTotal * 60));
            return p;
        }
        var players = new List<PlayerSample>
        {
            P(10, ("a", 0), ("b", 2), ("c", 10)),
            P(10, ("a", 0), ("b", 4), ("c", 8)),
            P(5, ("a", 0), ("b", 3)),
            P(5, ("a", 0)),
        };
        var names = new[] { "a", "b", "c" };
        var r = SampleStats.Compute(players, names);
        Assert.Equal(4, r.Players);
        Assert.Equal(100.0, r.Percent["a"]);
        Assert.Equal(75.0, r.Percent["b"]);
        Assert.Equal(50.0, r.Percent["c"]);
        Assert.Equal(180.0, r.Times["b"].P50!.Value); // median of 2, 3 and 4 hours
        Assert.Equal(2, r.Completers);
        Assert.Equal(540.0, r.CompletionMedianMinutes); // 8 and 10 hours

        var g = SampleGame();
        g.Achievements.Clear();
        foreach (var n in names) g.Achievements.Add(new AchievementDef { ApiName = n, Name = n });
        g.CompletionMinutes = null;
        SampleStats.Apply(g, r, DateTimeOffset.Now);
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(75.0, g.Achievements[1].Percent);
        Assert.True(g.Achievements[1].PercentFromSample);
        Assert.Equal(180.0, g.Estimates["b"].P50!.Value); // the model did not overwrite the measurement
        Assert.Equal(SampleStats.SourceName, g.Estimates["b"].Source);
        Assert.Equal(540.0, g.UsedCompletionMinutes);
    }
}

/// <summary>Answers every request by its URL and remembers what was asked.</summary>
sealed class FakeHttp(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
{
    public List<string> Urls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (Urls) Urls.Add(url);
        var (status, body) = answer(url);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
