// gameapp/GameAppBackground.cs -- a build keeps running when the user leaves the app (docs/DECISIONS.md entry 81).
//
// iOS 26's BGContinuedProcessingTask: the user taps "Build", the app submits a BGContinuedProcessingTaskRequest, and the
// system lets the process run on after the app is backgrounded, showing its own progress UI (title, subtitle, a bar
// from the task's NSProgress) on the lock screen and Dynamic Island. That UI is the system's: no ActivityKit extension,
// so no second App ID on a free sideloading account.
//
// Why the rules here are what they are:
//   * The request is created from the foreground (the user just tapped), with strategy Fail: a build that cannot start
//     NOW is not queued behind an unknown load; the app says so and the build simply runs as before, foreground only.
//     Resources stay at the default (CPU, no GPU): the GPU variant needs an entitlement a sideloading profile cannot carry.
//   * The identifier is `<bundle id>.build.<n>`; Info.plist permits `<bundle id>.build.*`. The launch handler is registered
//     once per identifier, just before its submit (a second registration of an identifier kills the app); continued-processing
//     registrations are allowed after launch.
//   * The task must report progress or the scheduler may expire it as stalled: the ticker (1 s) feeds it, never going back.
//   * Nothing here touches UIKit: the callbacks run on a system queue, and the only app state they read or write is
//     under `backgroundLock`.
//   * On expiration the system ends the task, not the process; the chain goes on as far as iOS lets the process live, and
//     the kill/resume of entry 74 covers the rest.
//   * A build that ends the process on purpose between the rewrite and the shaders (entry 75) ends the task with it: iOS
//     cannot start an app by itself, so the continuation still needs the user's tap.
using System;
using System.Threading;

using BackgroundTasks;
using Foundation;

public sealed partial class GameAppViewController
{
    private const int ProgressUnits = 10000;

    private static readonly object backgroundLock = new object();
    private static BGContinuedProcessingTask backgroundTask;   // set by the system's launch handler
    private static string backgroundId;                         // the identifier of the request now in flight
    private static long backgroundDone;                         // the last unit count reported
    private static string backgroundSubtitle;
    private static int backgroundCounter;

    private static string BackgroundPrefix => (NSBundle.MainBundle.BundleIdentifier ?? "dev.nxpocket.gameapp") + ".build.";

    /// Ask the system to keep this build running in the background. Called from BeginRun for a build (not a boot).
    private void BackgroundBegin(string game)
    {
        if (!UIKit.UIDevice.CurrentDevice.CheckSystemVersion(26, 0)) return;
        try
        {
            lock (backgroundLock)
            {
                if (backgroundId != null) return;
                var id = BackgroundPrefix + (++backgroundCounter);
                // Each submitted identifier is registered once, itself (the Info.plist permits the wildcard; a registration of
                // the wildcard string was refused by the system as "not advertised"). A second registration of the SAME
                // identifier kills the app, hence the counter.
                if (!BGTaskScheduler.Shared.Register(id, null, BackgroundStarted))
                { Write("background: the system refused the task registration (is the identifier in Info.plist?)"); return; }
                var title = "正在构建 " + GameAppUi.DisplayName(game);
                var request = new BGContinuedProcessingTaskRequest(id, title, "开始")
                {
                    Strategy = BGContinuedProcessingTaskRequestSubmissionStrategy.Fail,
                };
                backgroundId = id;
                backgroundDone = 0;
                backgroundSubtitle = "开始";
                BGTaskScheduler.Shared.Submit(request, error =>
                {
                    if (error == null) return;
                    lock (backgroundLock) if (backgroundId == id && backgroundTask == null) backgroundId = null;
                    BeginInvokeOnMainThread(() => Write($"background: the build runs only while iBuilder is open ({error.Code}: {error.LocalizedDescription})"));
                });
            }
        }
        catch (Exception error) { Write($"background: not started ({error.GetType().Name}: {error.Message})"); }
    }

    /// The system's launch handler: runs on its queue, once the task may run. Keep it small.
    private void BackgroundStarted(BGTask task)
    {
        if (task is not BGContinuedProcessingTask continued) { task.SetTaskCompleted(false); return; }
        lock (backgroundLock)
        {
            if (backgroundId == null) { continued.SetTaskCompleted(true); return; }   // the build ended before the system got here
            backgroundTask = continued;
            continued.Progress.TotalUnitCount = ProgressUnits;
            continued.Progress.CompletedUnitCount = backgroundDone;
            continued.ExpirationHandler = () =>
            {
                lock (backgroundLock) { if (backgroundTask == continued) backgroundTask = null; }
                BeginInvokeOnMainThread(() => Write("background: the system ended the background task; the build goes on while iOS lets this process live"));
            };
        }
        BeginInvokeOnMainThread(() => Write("background: the build continues if you leave the app (progress on the lock screen)"));
    }

    /// Feed the system's progress UI: the stage's fraction and the step as the subtitle. Called from the ticker (1 s).
    private void BackgroundTick()
    {
        var stage = GameAppUi.ParseStage(lastStage);
        lock (backgroundLock)
        {
            var task = backgroundTask;
            if (task == null) return;
            var done = Math.Max(backgroundDone, (long) (stage.Fraction * ProgressUnits));
            if (done != backgroundDone) { backgroundDone = done; task.Progress.CompletedUnitCount = done; }
            var step = stage.Valid ? $"{stage.Step}/{stage.Of}: {stage.Text}" : (lastStage ?? "开始");
            if (step.Length > 80) step = step.Substring(0, 80);
            if (step != backgroundSubtitle)
            {
                backgroundSubtitle = step;
                task.UpdateTitle(task.Title, step);
            }
        }
    }

    /// The build ended (Finish) or the process is about to end on purpose (RestartForStage3).
    private void BackgroundEnd(bool success)
    {
        lock (backgroundLock)
        {
            var task = backgroundTask;
            var id = backgroundId;
            backgroundTask = null;
            backgroundId = null;
            if (task != null)
            {
                if (success) task.Progress.CompletedUnitCount = ProgressUnits;
                task.SetTaskCompleted(success);
            }
            else if (id != null) BGTaskScheduler.Shared.Cancel(id);   // never started: do not let it start for a finished build
        }
    }
}
