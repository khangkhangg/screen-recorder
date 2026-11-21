#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecorderState {
    Idle,
    Countdown,
    Recording,
    Paused,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecorderCommand {
    ToggleRecord,
    CountdownDone,
    TogglePause,
    Failed,
}

pub fn next(s: RecorderState, c: RecorderCommand, countdown_enabled: bool) -> RecorderState {
    use RecorderCommand::*;
    use RecorderState::*;
    match (s, c) {
        (_, Failed) => Idle,
        (Idle, ToggleRecord) if countdown_enabled => Countdown,
        (Idle, ToggleRecord) => Recording,
        (Countdown, CountdownDone) => Recording,
        (_, ToggleRecord) => Idle,
        (Recording, TogglePause) => Paused,
        (Paused, TogglePause) => Recording,
        _ => s,
    }
}

#[cfg(test)]
mod tests {
    use super::RecorderCommand::*;
    use super::RecorderState::*;
    use super::*;

    #[test]
    fn transitions() {
        for (s, c, countdown, expected) in [
            (Idle, ToggleRecord, true, Countdown),
            (Countdown, CountdownDone, true, Recording),
            (Countdown, ToggleRecord, true, Idle),
            (Recording, TogglePause, true, Paused),
            (Paused, TogglePause, true, Recording),
            (Idle, ToggleRecord, false, Recording),
            (Recording, ToggleRecord, true, Idle),
            (Paused, ToggleRecord, true, Idle),
            (Recording, Failed, true, Idle),
            (Idle, TogglePause, true, Idle),
        ] {
            assert_eq!(expected, next(s, c, countdown), "{s:?} {c:?}");
        }
    }
}
