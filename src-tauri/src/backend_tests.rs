use super::*;
use std::io::Read;
use tokio::io::AsyncReadExt;
use tokio::process::ChildStdout;

fn runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
}

// The helper is this Rust test executable, with only this one test selected.
// It never initializes Tauri, opens a workspace, or reads real credentials.
#[test]
#[ignore = "A subprocess fixture, invoked by the pipe lifecycle tests"]
fn pipe_fixture() {
    let Ok(mode) = std::env::var("MC_PIPE_TEST_FIXTURE") else {
        return;
    };
    if mode == "stall" {
        std::thread::sleep(Duration::from_secs(30));
        return;
    }
    let mut bytes = Vec::new();
    std::io::stdin().read_to_end(&mut bytes).unwrap();
    println!("FRAME_BYTES={}", bytes.len());
}

fn fixture(stalled: bool) -> (Arc<Backend>, ChildStdout) {
    let mut command = Command::new(std::env::current_exe().unwrap());
    command
        .args([
            "--exact",
            "backend::tests::pipe_fixture",
            "--ignored",
            "--nocapture",
        ])
        .env(
            "MC_PIPE_TEST_FIXTURE",
            if stalled { "stall" } else { "read" },
        )
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .kill_on_drop(true);
    #[cfg(windows)]
    command.creation_flags(0x08000000);
    let mut child = command.spawn().unwrap();
    let input = child.stdin.take().unwrap();
    let output = child.stdout.take().unwrap();
    (
        Arc::new(Backend {
            input: Mutex::new(Some(input)),
            child: Mutex::new(child),
            pending: SyncMutex::new(HashMap::new()),
            next: AtomicU64::new(1),
            slots: Semaphore::new(14),
            control_slots: Semaphore::new(2),
            failed: AtomicBool::new(false),
            quitting: AtomicBool::new(false),
        }),
        output,
    )
}

async fn wait_for_pending(backend: &Backend, expected: usize) {
    tokio::time::timeout(Duration::from_secs(3), async {
        loop {
            if backend.pending.lock().unwrap().len() == expected {
                return;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("requests did not enter the pending map");
}

async fn stop_fixture(backend: &Backend) {
    backend.input.lock().await.take();
    let mut child = backend.child.lock().await;
    if tokio::time::timeout(Duration::from_secs(3), child.wait())
        .await
        .is_err()
    {
        if child.try_wait().unwrap().is_none() {
            child.kill().await.unwrap();
            child.wait().await.unwrap();
        }
    }
}

#[test]
fn cancelled_request_drops_pending_reply_and_releases_its_slot() {
    runtime().block_on(async {
        let (backend, _output) = fixture(false);
        let input = backend.input.lock().await;
        for method in ["snapshot", "query", "sessions"] {
            let expired =
                tokio::time::timeout(Duration::from_millis(25), backend.call(method, json!({})))
                    .await;
            assert!(expired.is_err());
            assert!(backend.pending.lock().unwrap().is_empty());
            assert_eq!(backend.slots.available_permits(), 14);
        }
        drop(input);
        stop_fixture(&backend).await;
    });
}

#[test]
fn memory_responses_have_bounded_deadlines_and_wait_for_replies() {
    runtime().block_on(async {
        assert_eq!(
            response_timeout("snapshot", false),
            Some(Duration::from_secs(315))
        );
        assert_eq!(
            response_timeout("monitoring.pause", true),
            Some(Duration::from_secs(20))
        );
        for method in ["query", "sessions"] {
            assert_eq!(
                response_timeout(method, false),
                Some(Duration::from_secs(315))
            );
            let (sender, receiver) = oneshot::channel();
            let waiter = tokio::spawn(await_reply(receiver, response_timeout(method, false)));
            // A short equivalent of an ordinary RPC deadline expires while the
            // memory response remains pending and can still complete normally.
            tokio::time::sleep(Duration::from_millis(30)).await;
            assert!(!waiter.is_finished());
            sender.send(Ok(json!({"complete": true}))).unwrap();
            assert_eq!(waiter.await.unwrap().unwrap()["complete"], true);
        }
        let (_sender, receiver) = oneshot::channel();
        let result = await_reply(receiver, Some(Duration::from_millis(10))).await;
        assert!(result.is_err());
    });
}

#[test]
fn backend_failure_resolves_pending_memory_reads() {
    runtime().block_on(async {
        let (backend, _output) = fixture(false);
        let mut requests = Vec::new();
        for method in ["query", "sessions"] {
            let caller = backend.clone();
            requests.push(tokio::spawn(
                async move { caller.call(method, json!({})).await },
            ));
        }
        wait_for_pending(&backend, 2).await;
        // Await the input mutex so each short frame has been written before
        // simulating the reader observing collector termination.
        drop(backend.input.lock().await);
        backend.fail_pending("Synthetic collector exit");
        for request in requests {
            assert_eq!(
                tokio::time::timeout(Duration::from_secs(3), request)
                    .await
                    .unwrap()
                    .unwrap()
                    .unwrap_err(),
                "Synthetic collector exit"
            );
        }
        assert!(backend.pending.lock().unwrap().is_empty());
        assert_eq!(backend.slots.available_permits(), 14);
        stop_fixture(&backend).await;
    });
}

#[test]
fn invalidation_fails_waiters_and_prevents_queued_writes() {
    runtime().block_on(async {
        let (backend, mut output) = fixture(false);
        let input = backend.input.lock().await;
        let one = backend.clone();
        let first = tokio::spawn(async move { one.call("snapshot", json!({})).await });
        let two = backend.clone();
        let second = tokio::spawn(async move { two.call("settings", json!({})).await });
        wait_for_pending(&backend, 2).await;
        backend.fail_pending("Synthetic transport failure");
        assert!(backend.pending.lock().unwrap().is_empty());
        drop(input);
        assert!(tokio::time::timeout(Duration::from_secs(3), first)
            .await
            .unwrap()
            .unwrap()
            .is_err());
        assert!(tokio::time::timeout(Duration::from_secs(3), second)
            .await
            .unwrap()
            .unwrap()
            .is_err());
        assert!(backend.input.lock().await.is_none());
        assert_eq!(backend.slots.available_permits(), 14);
        stop_fixture(&backend).await;
        let mut observed = String::new();
        output.read_to_string(&mut observed).await.unwrap();
        assert!(
            observed.contains("FRAME_BYTES=0"),
            "a queued request wrote after invalidation: {observed}"
        );
    });
}

#[test]
fn blocked_pipe_write_times_out_and_invalidates_pending_requests() {
    runtime().block_on(async {
        let (backend, _output) = fixture(true);
        let payload = json!({"search": "x".repeat(260_000)});
        let started = std::time::Instant::now();
        let result =
            tokio::time::timeout(Duration::from_secs(14), backend.call("query", payload)).await;
        assert!(result.expect("pipe write escaped its deadline").is_err());
        assert!(
            started.elapsed() >= Duration::from_secs(9),
            "fixture exited before exercising pipe backpressure"
        );
        assert!(started.elapsed() < Duration::from_secs(14));
        assert!(backend.failed.load(Ordering::Acquire));
        assert!(backend.pending.lock().unwrap().is_empty());
        assert_eq!(backend.slots.available_permits(), 14);
        // This fixture intentionally ignores EOF; invalidation must reap the owned child.
        let mut child = backend.child.lock().await;
        assert!(child.try_wait().unwrap().is_some());
    });
}

#[test]
fn shutdown_dispatches_even_when_control_slots_are_full() {
    runtime().block_on(async {
        let (backend, _output) = fixture(false);
        let occupied = backend.control_slots.acquire_many(2).await.unwrap();
        let caller = backend.clone();
        let request = tokio::spawn(async move { caller.call("shutdown", json!({})).await });
        wait_for_pending(&backend, 1).await;
        let reply = backend.pending.lock().unwrap().drain().next().unwrap().1;
        reply.send(Ok(json!({"stopped":true}))).unwrap();
        let response = tokio::time::timeout(Duration::from_secs(3), request)
            .await
            .unwrap()
            .unwrap()
            .unwrap();
        assert_eq!(response["stopped"], true);
        assert_eq!(backend.control_slots.available_permits(), 0);
        drop(occupied);
        stop_fixture(&backend).await;
    });
}
