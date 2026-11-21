use crate::session::PauseEntry;
use std::sync::{Mutex, MutexGuard};
use std::time::Duration;

const TICKS_PER_SECOND: i64 = 10_000_000;

#[derive(Default)]
struct Times {
    start: Option<Duration>,
    paused_at: Option<Duration>,
    paused_total: Duration,
    pauses: Vec<PauseEntry>,
}

/// One clock for every recorded file, so all of them get the same length and a constant frame rate.
pub struct RecordingClock {
    frame_rate: i32,
    now: Box<dyn Fn() -> Duration + Send + Sync>,
    times: Mutex<Times>,
}

impl RecordingClock {
    pub fn new(frame_rate: i32, now: Box<dyn Fn() -> Duration + Send + Sync>) -> Self {
        Self { frame_rate, now, times: Mutex::default() }
    }

    fn lock(&self) -> MutexGuard<'_, Times> {
        // The lock only guards plain arithmetic, so data behind a poisoned lock is still valid.
        self.times.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn media(&self, t: &Times) -> Duration {
        match t.start {
            None => Duration::ZERO,
            Some(start) => t.paused_at.unwrap_or_else(|| (self.now)()).saturating_sub(start).saturating_sub(t.paused_total),
        }
    }

    pub fn start(&self) {
        let mut t = self.lock();
        t.start.get_or_insert_with(|| (self.now)());
    }

    pub fn pause(&self) {
        let mut t = self.lock();
        if t.start.is_some() && t.paused_at.is_none() {
            t.paused_at = Some((self.now)());
        }
    }

    pub fn resume(&self) {
        let mut t = self.lock();
        let (Some(start), Some(p)) = (t.start, t.paused_at) else { return };
        let length = (self.now)().saturating_sub(p);
        let at = p.saturating_sub(start).saturating_sub(t.paused_total);
        t.pauses.push(PauseEntry { at_seconds: at.as_secs_f64(), duration_seconds: length.as_secs_f64() });
        t.paused_total += length;
        t.paused_at = None;
    }

    pub fn is_paused(&self) -> bool {
        self.lock().paused_at.is_some()
    }

    pub fn media_time(&self) -> Duration {
        self.media(&self.lock())
    }

    pub fn frames_due(&self) -> i64 {
        self.ticks() * i64::from(self.frame_rate) / TICKS_PER_SECOND
    }

    pub fn samples_due(&self, sample_rate: i32) -> i64 {
        self.ticks() * i64::from(sample_rate) / TICKS_PER_SECOND
    }

    pub fn pauses(&self) -> Vec<PauseEntry> {
        self.lock().pauses.clone()
    }

    // Integer math on 100-nanosecond ticks, so counts never drift from rounding.
    fn ticks(&self) -> i64 {
        (self.media_time().as_nanos() / 100) as i64
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering::SeqCst};
    use std::sync::Arc;

    // Fake time in 100-nanosecond ticks.
    fn make(fps: i32) -> (Arc<AtomicU64>, RecordingClock) {
        let t = Arc::new(AtomicU64::new(0));
        let t2 = t.clone();
        (t, RecordingClock::new(fps, Box::new(move || Duration::from_nanos(t2.load(SeqCst) * 100))))
    }

    fn advance(t: &AtomicU64, seconds: f64) {
        t.fetch_add((seconds * 1e7).round() as u64, SeqCst);
    }

    #[test]
    fn frames_follow_time() {
        let (t, c) = make(60);
        c.start();
        advance(&t, 1.5);
        assert_eq!(90, c.frames_due());
    }

    #[test]
    fn pause_cuts_time() {
        let (t, c) = make(60);
        c.start();
        advance(&t, 2.0);
        c.pause();
        advance(&t, 3.0);
        c.resume();
        assert_eq!(Duration::from_secs(2), c.media_time());
        assert_eq!(vec![PauseEntry { at_seconds: 2.0, duration_seconds: 3.0 }], c.pauses());
    }

    #[test]
    fn frames_do_not_grow_while_paused() {
        let (t, c) = make(60);
        c.start();
        advance(&t, 1.0);
        c.pause();
        advance(&t, 5.0);
        assert_eq!(60, c.frames_due());
        assert!(c.pauses().is_empty());
    }

    #[test]
    fn samples_follow_time() {
        let (t, c) = make(60);
        c.start();
        advance(&t, 0.5);
        assert_eq!(24_000, c.samples_due(48_000));
    }

    #[test]
    fn pause_twice_is_ignored() {
        let (t, c) = make(60);
        c.start();
        advance(&t, 1.0);
        c.pause();
        advance(&t, 1.0);
        c.pause();
        advance(&t, 1.0);
        c.resume();
        assert_eq!(1, c.pauses().len());
        assert_eq!(2.0, c.pauses()[0].duration_seconds);
    }

    #[test]
    fn pause_before_start_and_resume_when_running_do_nothing() {
        let (t, c) = make(60);
        c.pause();
        assert!(!c.is_paused());
        c.start();
        advance(&t, 1.0);
        c.resume();
        assert_eq!(Duration::from_secs(1), c.media_time());
        assert!(c.pauses().is_empty());
    }

    #[test]
    fn frames_exact_at_boundary() {
        let (t, c) = make(100);
        c.start();
        advance(&t, 0.29);
        assert_eq!(29, c.frames_due());
    }

    #[test]
    fn start_twice_is_ignored() {
        let (t, c) = make(60);
        c.start();
        advance(&t, 1.0);
        c.start();
        advance(&t, 1.0);
        assert_eq!(Duration::from_secs(2), c.media_time());
    }

    #[test]
    fn frames_never_decrease_while_another_thread_pauses() {
        let t0 = std::time::Instant::now();
        let c = Arc::new(RecordingClock::new(1000, Box::new(move || t0.elapsed())));
        c.start();
        let c2 = c.clone();
        let worker = std::thread::spawn(move || {
            for _ in 0..1000 {
                c2.pause();
                c2.resume();
            }
        });
        let mut last = 0;
        while !worker.is_finished() {
            let n = c.frames_due();
            assert!(n >= last, "{n} < {last}");
            last = n;
        }
        worker.join().unwrap();
        assert!(c.frames_due() >= last);
    }
}
