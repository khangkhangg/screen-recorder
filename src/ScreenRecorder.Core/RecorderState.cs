namespace ScreenRecorder.Core;

public enum RecorderState { Idle, Countdown, Recording, Paused }
public enum RecorderCommand { ToggleRecord, CountdownDone, TogglePause, Failed }

public static class RecorderStateMachine
{
    public static RecorderState Next(RecorderState s, RecorderCommand c, bool countdownEnabled) => (s, c) switch
    {
        (_, RecorderCommand.Failed) => RecorderState.Idle,
        (RecorderState.Idle, RecorderCommand.ToggleRecord) => countdownEnabled ? RecorderState.Countdown : RecorderState.Recording,
        (RecorderState.Countdown, RecorderCommand.CountdownDone) => RecorderState.Recording,
        (_, RecorderCommand.ToggleRecord) => RecorderState.Idle,
        (RecorderState.Recording, RecorderCommand.TogglePause) => RecorderState.Paused,
        (RecorderState.Paused, RecorderCommand.TogglePause) => RecorderState.Recording,
        _ => s,
    };
}
