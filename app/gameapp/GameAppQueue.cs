// The build queue and the heat of the phone: the logic with no UIKit, so GameAppUi.Tests runs it.
//
// iOS lets a Python runtime start once per process (GameAppMain.pythonRanThisProcess), so a queue cannot build two games
// in one process: after a build ends the app closes and the next one starts at the next launch. The queue file says
// whose turn it is (autostart) and what became of each game; a launch that finds a game still "running" knows the
// process died in it, and never starts anything by itself from that (the interrupted-run rule).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public sealed record QueueItem(string Game, string State, string Note);

public sealed class BuildQueueState
{
    /// The next launch starts the first queued game (set when a queued build ended well and more are waiting).
    public bool AutoStart;
    public List<QueueItem> Items = new List<QueueItem>();

    public int Queued => Items.Count(i => i.State == BuildQueue.Queued);
}

public static class BuildQueue
{
    public const string Queued = "queued", Running = "running", Failed = "failed";

    public static BuildQueueState Parse(string text)
    {
        var state = new BuildQueueState();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                if (line.StartsWith("# autostart", StringComparison.Ordinal)) state.AutoStart = line.EndsWith("1", StringComparison.Ordinal);
                continue;
            }
            var p = line.Split('\t');
            var kind = p.Length > 1 ? p[1] : Queued;
            if (kind != Queued && kind != Running && kind != Failed) kind = Queued;
            if (p[0].Length > 0 && !state.Items.Any(i => i.Game == p[0])) state.Items.Add(new QueueItem(p[0], kind, p.Length > 2 ? p[2] : ""));
        }
        return state;
    }

    public static string Format(BuildQueueState state)
    {
        var sb = new StringBuilder();
        sb.Append("# autostart ").Append(state.AutoStart ? '1' : '0').Append('\n');
        foreach (var i in state.Items) sb.Append(i.Game).Append('\t').Append(i.State).Append('\t').Append(i.Note.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
        return sb.ToString();
    }

    /// Adds a game at the end. False when it is already in the queue.
    public static bool Add(BuildQueueState state, string game)
    {
        if (string.IsNullOrEmpty(game) || state.Items.Any(i => i.Game == game)) return false;
        state.Items.Add(new QueueItem(game, Queued, ""));
        return true;
    }

    public static bool Remove(BuildQueueState state, string game) => state.Items.RemoveAll(i => i.Game == game) > 0;

    /// The game whose turn it is: the first one still queued (a failed one is skipped, and stays to be seen).
    public static QueueItem Next(BuildQueueState state) => state.Items.FirstOrDefault(i => i.State == Queued);

    public static void MarkRunning(BuildQueueState state, string game) => Replace(state, game, Running, "");

    /// A queued build ended: a good one leaves the queue; a failed one stays with the reason, and the queue goes on.
    public static void Ended(BuildQueueState state, string game, bool ok, string note)
    {
        if (ok) Remove(state, game); else Replace(state, game, Failed, note ?? "");
        state.AutoStart = Next(state) != null;
    }

    /// At launch: a game left "running" was in the build when the process died. It is marked failed and nothing starts.
    /// `continuing` is the game whose build the last process ended on purpose before stage 3 (gameapp.continue): it goes on.
    public static bool ResolveInterrupted(BuildQueueState state, string continuing = null)
    {
        bool any = false;
        foreach (var i in state.Items.Where(i => i.State == Running && i.Game != continuing).ToList())
        {
            Replace(state, i.Game, Failed, "构建期间应用被关闭");
            any = true;
        }
        if (any) state.AutoStart = false;
        return any;
    }

    private static void Replace(BuildQueueState state, string game, string kind, string note)
    {
        int at = state.Items.FindIndex(i => i.Game == game);
        if (at >= 0) state.Items[at] = new QueueItem(game, kind, note);
    }
}

/// What the phone's thermal state means for a build (NSProcessInfo.ThermalState: 0 nominal, 1 fair, 2 serious, 3 critical).
public static class BuildHeat
{
    public const int Serious = 2;

    /// Documents/gameapp.thermal: "<state> <lowperf>", read by the pipeline to slow down.
    public static string ThermalLine(int state, bool lowPerformance) => $"{Math.Clamp(state, 0, 3)} {(lowPerformance ? 1 : 0)}\n";

    /// Ask before starting when the phone is already hot.
    public static bool ShouldAsk(int state) => state >= Serious;

    /// "Wait" ends when it is back to fair or nominal.
    public static bool CoolEnough(int state) => state <= 1;

    /// The subtle note on the build screen: nothing while nominal.
    public static string Chip(int state) => state switch
    {
        1 => "发热", 2 => "很热：暂停新任务", >= 3 => "非常热：暂停新任务", _ => null,
    };
}
