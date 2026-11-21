use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use recorder_win::{AudioIn, AudioSource, Error, WavWriter};

/// A WAV path in the temp folder. The file and its copy are deleted on drop.
struct TempWav(PathBuf);

impl TempWav {
    fn new() -> TempWav {
        static NEXT: AtomicU32 = AtomicU32::new(0);
        let name = format!("recorder-audio-{}-{}.wav", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst));
        TempWav(std::env::temp_dir().join(name))
    }

    fn copy(&self) -> PathBuf {
        self.0.with_extension("copy.wav")
    }
}

impl Drop for TempWav {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
        let _ = std::fs::remove_file(self.copy());
    }
}

fn u16_at(b: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([b[i], b[i + 1]])
}

fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes(b[i..i + 4].try_into().unwrap())
}

#[test]
fn wav_header_is_valid() {
    let path = TempWav::new();
    let mut w = WavWriter::create(&path.0).unwrap();
    w.write(&vec![0.0; 96_000]).unwrap();
    w.finish().unwrap();
    let b = std::fs::read(&path.0).unwrap();
    assert_eq!(b.len(), 44 + 96_000 * 4);
    assert_eq!(&b[0..4], b"RIFF");
    assert_eq!(u32_at(&b, 4), 36 + 96_000 * 4);
    assert_eq!(&b[8..16], b"WAVEfmt ");
    assert_eq!(u16_at(&b, 20), 3);
    assert_eq!(u16_at(&b, 22), 2);
    assert_eq!(u32_at(&b, 24), 48_000);
    assert_eq!(u32_at(&b, 28), 48_000 * 8);
    assert_eq!(u16_at(&b, 32), 8);
    assert_eq!(u16_at(&b, 34), 32);
    assert_eq!(&b[36..40], b"data");
    assert_eq!(u32_at(&b, 40), 96_000 * 4);
}

#[test]
fn cut_off_wav_has_usable_header() {
    let path = TempWav::new();
    let mut w = WavWriter::create(&path.0).unwrap();
    for _ in 0..3 {
        w.write(&vec![0.0; 96_000]).unwrap();
    }
    std::fs::copy(&path.0, path.copy()).unwrap();
    let b = std::fs::read(path.copy()).unwrap();
    assert!(u32_at(&b, 40) >= 2 * 48_000 * 8, "{}", u32_at(&b, 40));
}

#[test]
fn wav_finish_twice_is_fine() {
    let path = TempWav::new();
    let mut w = WavWriter::create(&path.0).unwrap();
    w.finish().unwrap();
    w.finish().unwrap();
}

#[test]
fn an_existing_wav_is_kept() {
    let path = TempWav::new();
    std::fs::write(&path.0, "keep me").unwrap();
    let result = WavWriter::create(&path.0);
    assert!(matches!(result, Err(Error::Io(ref e)) if e.kind() == std::io::ErrorKind::AlreadyExists));
    assert_eq!("keep me", std::fs::read_to_string(&path.0).unwrap());
}

#[test]
#[ignore = "needs the desktop"]
fn loopback_reads_48k_stereo_pairs() {
    let mut src = AudioSource::open_loopback().unwrap();
    thread::sleep(Duration::from_millis(300));
    let n = src.read(&mut vec![0.0; 96_000]);
    // At most 0.6 s of stereo floats.
    assert!(n <= 57_600, "{n}");
    assert_eq!(n % 2, 0);
    assert!(!src.is_lost());
}

#[test]
#[ignore = "needs the desktop"]
fn dropping_a_source_right_after_opening_is_quick() {
    let src = AudioSource::open_loopback().unwrap();
    let watch = Instant::now();
    drop(src);
    assert!(watch.elapsed() < Duration::from_secs(2), "{:?}", watch.elapsed());
}

#[test]
#[ignore = "needs the desktop"]
fn unknown_mic_id_gives_a_plain_message() {
    let Err(e) = AudioSource::open_mic(Some("no-such-mic")) else { panic!("a made-up id opened") };
    assert_eq!(e.to_string(), "No microphone found on this PC.");
}

#[test]
#[ignore = "needs the desktop"]
fn mic_opens_or_fails_with_a_plain_message() {
    let mics = AudioSource::list_mics();
    println!("{mics:?}");
    match AudioSource::open_mic(None) {
        Ok(mut src) => {
            // A working microphone delivers samples even in silence.
            let watch = Instant::now();
            let mut n = 0;
            while n == 0 && watch.elapsed() < Duration::from_secs(1) {
                thread::sleep(Duration::from_millis(50));
                n = src.read(&mut vec![0.0; 4800]);
            }
            println!("opened, read {n} floats");
            assert!(n > 0 && n % 2 == 0, "{n}");
        }
        Err(e) => {
            let text = e.to_string();
            println!("{}: {text}", if text.contains("privacy") { "blocked by privacy" } else { "no microphone" });
            // No microphone, or Windows privacy settings block it.
            assert!(
                if mics.is_empty() { text.contains("No microphone") } else { text.contains("privacy") },
                "{text}"
            );
        }
    }
}
