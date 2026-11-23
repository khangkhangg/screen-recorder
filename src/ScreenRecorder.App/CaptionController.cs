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

    Task<bool> CaptionsReady(CaptionChoice c) =>
        captions.IsReady(c) ? Task.FromResult(true) : CaptionConsentWindow.AskAsync(captions, c);

    async void QueueCaptions(string folder)
    {
        var c = settings.Captions;
        if (c == CaptionChoice.Off || CaptionService.TrackIn(folder) is null) return;
        if (!await CaptionsReady(c))
        {
            Save(settings with { Captions = CaptionChoice.Off });
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

    async void PumpCaptions()
    {
        if (captionWork is { IsCompleted: false } || captionQueue.Next() is not { } folder) return;
        var c = settings.Captions;
        var stop = captionStop = new CancellationTokenSource();
        if (c != CaptionChoice.Off)
        {
            card.ShowCaptions(folder, "Making captions");
            try
            {
                await (captionWork = captions.RunAsync(folder, c, new Progress<int>(p => card.ShowCaptions(folder, $"Making captions {p}%")), stop.Token));
                card.ShowCaptions(folder, "Captions saved");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                card.ShowCaptions(folder, "");
            }
            // A failed job never touches the recording itself.
            catch (Exception e)
            {
                card.ShowCaptions(folder, "Captions failed");
                try { CaptionService.AddNote(folder, $"captions failed: {e.Message}"); }
                catch (Exception note) { System.Diagnostics.Debug.WriteLine(note); }
            }
        }
        if (!stop.IsCancellationRequested) captionQueue.Done();
        PumpCaptions();
    }

    // On quit the helper is killed and the job deletes its temporary files before the app exits.
    void StopCaptions()
    {
        captionStop?.Cancel();
        try { captionWork?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }
}
