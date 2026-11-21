use recorder_win::{find_window, init_process, monitors, windows, Device};

#[test]
#[ignore = "needs the desktop"]
fn first_monitor_is_primary_with_a_size() {
    init_process().unwrap();
    let list = monitors();
    assert!(!list.is_empty());
    let first = &list[0];
    assert!(first.primary, "{first:?}");
    assert!(first.width > 0 && first.height > 0, "{first:?}");
}

#[test]
#[ignore = "needs the desktop"]
fn device_is_created() {
    init_process().unwrap();
    Device::new().unwrap();
}

#[test]
#[ignore = "needs the desktop"]
fn find_window_with_made_up_title_finds_nothing() {
    init_process().unwrap();
    assert_eq!(Err(vec![]), find_window("no window has this title 7f3a9c"));
}

#[test]
#[ignore = "needs the desktop"]
fn windows_all_have_titles() {
    init_process().unwrap();
    assert!(windows().iter().all(|w| !w.title.trim().is_empty()));
}
