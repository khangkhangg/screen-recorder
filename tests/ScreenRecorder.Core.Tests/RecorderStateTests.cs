using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class RecorderStateTests
{
    [Theory]
    [InlineData(RecorderState.Idle, RecorderCommand.ToggleRecord, true, RecorderState.Countdown)]
    [InlineData(RecorderState.Countdown, RecorderCommand.CountdownDone, true, RecorderState.Recording)]
    [InlineData(RecorderState.Countdown, RecorderCommand.ToggleRecord, true, RecorderState.Idle)]
    [InlineData(RecorderState.Recording, RecorderCommand.TogglePause, true, RecorderState.Paused)]
    [InlineData(RecorderState.Paused, RecorderCommand.TogglePause, true, RecorderState.Recording)]
    [InlineData(RecorderState.Idle, RecorderCommand.ToggleRecord, false, RecorderState.Recording)]
    [InlineData(RecorderState.Recording, RecorderCommand.ToggleRecord, true, RecorderState.Idle)]
    [InlineData(RecorderState.Paused, RecorderCommand.ToggleRecord, true, RecorderState.Idle)]
    [InlineData(RecorderState.Recording, RecorderCommand.Failed, true, RecorderState.Idle)]
    [InlineData(RecorderState.Idle, RecorderCommand.TogglePause, true, RecorderState.Idle)]
    public void Transitions(RecorderState s, RecorderCommand c, bool cd, RecorderState expected) =>
        Assert.Equal(expected, RecorderStateMachine.Next(s, c, cd));
}
