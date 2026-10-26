#[cfg(windows)]
mod platform {
    use sha2::{Digest, Sha256};
    use std::{
        sync::{
            atomic::{AtomicBool, Ordering},
            Arc,
        },
        thread::JoinHandle,
    };
    use tauri::AppHandle;
    use windows_sys::Win32::{
        Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS},
        System::Threading::{
            CreateEventW, CreateMutexW, OpenEventW, SetEvent, WaitForSingleObject,
            EVENT_MODIFY_STATE, INFINITE,
        },
    };

    pub struct Instance {
        mutex: usize,
        show: usize,
        stop: Arc<AtomicBool>,
        listener: Option<JoinHandle<()>>,
    }
    impl Instance {
        pub fn acquire(path: &std::path::Path) -> Result<Option<Self>, String> {
            let digest = Sha256::digest(path.to_string_lossy().to_uppercase().as_bytes());
            let hash = digest
                .iter()
                .take(12)
                .map(|b| format!("{b:02X}"))
                .collect::<String>();
            let name = format!("Local\\MissionControl-{hash}");
            let wide = |s: &str| s.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
            let event = wide(&(name.clone() + "-show"));
            unsafe {
                let mutex = CreateMutexW(std::ptr::null(), 0, wide(&name).as_ptr());
                if mutex.is_null() {
                    return Err("Cannot establish workspace instance lock".into());
                }
                if GetLastError() == ERROR_ALREADY_EXISTS {
                    let show = OpenEventW(EVENT_MODIFY_STATE, 0, event.as_ptr());
                    if !show.is_null() {
                        SetEvent(show);
                        CloseHandle(show);
                    }
                    CloseHandle(mutex);
                    return Ok(None);
                }
                let show = CreateEventW(std::ptr::null(), 0, 0, event.as_ptr());
                if show.is_null() {
                    CloseHandle(mutex);
                    return Err("Cannot create dashboard wake event".into());
                }
                Ok(Some(Self {
                    mutex: mutex as usize,
                    show: show as usize,
                    stop: Arc::new(AtomicBool::new(false)),
                    listener: None,
                }))
            }
        }
        pub fn attach(&mut self, app: AppHandle) {
            let show = self.show;
            let stop = self.stop.clone();
            self.listener = Some(std::thread::spawn(move || loop {
                unsafe {
                    WaitForSingleObject(show as _, INFINITE);
                }
                if stop.load(Ordering::Acquire) {
                    break;
                }
                let handle = app.clone();
                let _ = app.run_on_main_thread(move || {
                    let _ = crate::open_dashboard(&handle);
                });
            }));
        }
    }
    impl Drop for Instance {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Release);
            unsafe {
                SetEvent(self.show as _);
            }
            if let Some(listener) = self.listener.take() {
                let _ = listener.join();
            }
            unsafe {
                CloseHandle(self.show as _);
                CloseHandle(self.mutex as _);
            }
        }
    }
}

#[cfg(windows)]
pub use platform::Instance;
