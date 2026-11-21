use std::thread;
use std::time::{Duration, Instant};

use recorder_win::{init_process, Camera, Device, WebcamSource};

#[test]
fn listing_cameras_does_not_fail() {
    init_process().unwrap();
    let cameras = WebcamSource::list();
    println!("{cameras:?}");
    assert!(cameras.iter().all(|(id, name)| !id.is_empty() && !name.is_empty()), "{cameras:?}");
}

#[test]
fn unknown_camera_id_gives_a_plain_message() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let Err(e) = WebcamSource::open("no-such-camera", &device) else { panic!("a made-up id opened") };
    assert!(e.to_string().starts_with("Windows could not open the camera."), "{e}");
}

#[test]
#[ignore = "needs a camera"]
fn first_camera_delivers_frames_of_its_size() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let Some((id, name)) = WebcamSource::list().into_iter().next() else {
        println!("skipped: no camera connected");
        return;
    };
    let mut camera = WebcamSource::open(&id, &device).unwrap();
    println!("{name}: {:?}", camera.size());
    let watch = Instant::now();
    let frame = loop {
        if let Some(frame) = camera.latest_frame() {
            break frame;
        }
        assert!(watch.elapsed() < Duration::from_secs(3), "no frame within 3 seconds");
        thread::sleep(Duration::from_millis(20));
    };
    assert_eq!(frame.size, camera.size());
    assert!(!camera.is_lost());
    let watch = Instant::now();
    drop(camera);
    assert!(watch.elapsed() < Duration::from_secs(2), "{:?}", watch.elapsed());
}
