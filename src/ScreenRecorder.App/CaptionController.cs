using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

// Caption jobs after Stop: one at a time, never during a countdown or a recording, and only in memory.
public sealed partial class BarWindow
{
    readonly CaptionService captions = new(Path.Combine(Folder, "captions"));
    readonly CaptionQueue captionQueue = new();
    CancellationTokenSource? captionStop;
    Task? captionWork;
    bool captionBusy, captionsClosed;

    Task<bool> CaptionsReady(CaptionChoice c) =>
        captions.IsReady(c) ? Task.FromResult(true) : CaptionConsentWindow.AskAsync(captions, c);

    async void QueueCaptions(string folder)
    {
        var c = settings.Captions;
        if (c == CaptionChoice.Off || CaptionService.TrackIn(folder) is null) return;
        var ready = await CaptionsReady(c);
        if (captionsClosed) return;
        if (!ready)
        {
            Save(settings with { Captions = CaptionChoice.Off });
            settingsWindow?.ShowCaptions(CaptionChoice.Off);
            return;
        }
        captionQueue.Add(folder);
        PumpCaptions();
    }

    // A recording or countdown stops the running job, which starts again from the front of the line afterwards.
    void UpdateCaptions(bool canRun)
    {
        if (!canRun)
        {
            if (captionQueue.Pause() is not null) captionStop?.Cancel();
            return;
        }
        captionQueue.Resume();
        PumpCaptions();
    }

    // captionBusy stays set until the job's own continuation has run, so a job never starts twice.
    async void PumpCaptions()
    {
        if (captionsClosed || captionBusy || captionQueue.Next() is not { } folder) return;
        captionBusy = true;
        var c = settings.Captions;
        var stop = captionStop = new CancellationTokenSource();
        var stopped = false;
        if (c != CaptionChoice.Off)
        {
            ShowCaptionLine(folder, "Making captions");
            try
            {
                await (captionWork = captions.RunAsync(folder, c, new Progress<int>(p => ShowCaptionLine(folder, $"Making captions {p}%")), stop.Token));
                ShowCaptionLine(folder, "Captions saved");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                stopped = true;
                ShowCaptionLine(folder, "");
            }
            // A failed job never touches the recording itself.
            catch (Exception e)
            {
                ShowCaptionLine(folder, "Captions failed");
                try { CaptionService.AddNote(folder, $"captions failed: {e.Message}"); }
                catch (Exception note) { System.Diagnostics.Debug.WriteLine(note); }
            }
        }
        if (!stopped) captionQueue.Done(folder);
        captionBusy = false;
        PumpCaptions();
    }

    // The card is closed with the bar.
    void ShowCaptionLine(string folder, string text)
    {
        if (!captionsClosed) card.ShowCaptions(folder, text);
    }

    // On quit the consent window closes first, then the helper is killed and the job deletes its temporary files.
    void StopCaptions()
    {
        captionsClosed = true;
        CaptionConsentWindow.CloseForQuit();
        captionStop?.Cancel();
        try { captionWork?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }
}
