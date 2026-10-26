use crate::options::Options;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc, Mutex as SyncMutex,
    },
    time::Duration,
};
use tauri::{AppHandle, Emitter};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    process::{Child, ChildStdin, Command},
    sync::{oneshot, Mutex, Semaphore},
};

type Reply = Result<Value, String>;
type Replies = SyncMutex<HashMap<u64, oneshot::Sender<Reply>>>;

fn response_timeout(_method: &str, priority: bool) -> Option<Duration> {
    Some(Duration::from_secs(if priority { 20 } else { 315 }))
}

async fn await_reply(receiver: oneshot::Receiver<Reply>, timeout: Option<Duration>) -> Reply {
    let response = match timeout {
        Some(timeout) => tokio::time::timeout(timeout, receiver).await.ok(),
        None => Some(receiver.await),
    };
    match response {
        Some(Ok(result)) => result,
        _ => Err("The local request timed out or the collector stopped.".into()),
    }
}

struct PendingReply<'a>(&'a Replies, u64);
impl Drop for PendingReply<'_> {
    fn drop(&mut self) {
        self.0
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .remove(&self.1);
    }
}
pub struct Backend {
    input: Mutex<Option<ChildStdin>>,
    child: Mutex<Child>,
    pending: Replies,
    next: AtomicU64,
    slots: Semaphore,
    control_slots: Semaphore,
    failed: AtomicBool,
    pub quitting: AtomicBool,
}

impl Backend {
    pub async fn spawn(app: AppHandle, options: &Options) -> Result<Arc<Self>, String> {
        let shell = std::env::current_exe().map_err(|_| "Cannot locate desktop executable")?;
        let filename = if cfg!(windows) {
            "mission-control-backend.exe"
        } else {
            "mission-control-backend"
        };
        let backend_path = shell
            .parent()
            .ok_or("Cannot locate application directory")?
            .join(filename);
        let mut command = Command::new(backend_path);
        command
            .arg("--data-dir")
            .arg(&options.data)
            .arg("--shell-path")
            .arg(shell);
        if let Some(mode) = &options.mode {
            command.arg(format!("--{mode}"));
        }
        command
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::null())
            .kill_on_drop(true);
        #[cfg(windows)]
        command.creation_flags(0x08000000);
        let mut child = command.spawn().map_err(|_| "Collector executable is missing or could not start. Reinstall the complete application.")?;
        let input = child.stdin.take().ok_or("Collector input unavailable")?;
        let output = child.stdout.take().ok_or("Collector output unavailable")?;
        let backend = Arc::new(Self {
            input: Mutex::new(Some(input)),
            child: Mutex::new(child),
            pending: SyncMutex::new(HashMap::new()),
            next: AtomicU64::new(1),
            slots: Semaphore::new(14),
            control_slots: Semaphore::new(2),
            failed: AtomicBool::new(false),
            quitting: AtomicBool::new(false),
        });
        let reader = backend.clone();
        let data = options.data.clone();
        tauri::async_runtime::spawn(async move {
            let mut output = BufReader::new(output);
            loop {
                // Read one bounded envelope, even if the backend fails to delimit a response.
                let mut line = Vec::new();
                loop {
                    let available = match output.fill_buf().await {
                        Ok(bytes) if !bytes.is_empty() => bytes,
                        _ => break,
                    };
                    let count = available
                        .iter()
                        .position(|b| *b == b'\n')
                        .map_or(available.len(), |i| i + 1);
                    if line.len() + count > 8 * 1024 * 1024 + 1 {
                        line.clear();
                        break;
                    }
                    line.extend_from_slice(&available[..count]);
                    output.consume(count);
                    if line.last() == Some(&b'\n') {
                        break;
                    }
                }
                if line.is_empty() || line.last() != Some(&b'\n') {
                    break;
                }
                let response: Value = match serde_json::from_slice(&line) {
                    Ok(value) => value,
                    Err(_) => break,
                };
                if response["event"] == "changed" || response["event"] == "ready" {
                    let _ = app.emit("workspace-changed", ());
                    continue;
                }
                if let Some(id) = response["id"].as_str().and_then(|s| s.parse::<u64>().ok()) {
                    if let Some(reply) = reader
                        .pending
                        .lock()
                        .unwrap_or_else(|error| error.into_inner())
                        .remove(&id)
                    {
                        let result = if let Some(error) = response["error"].as_str() {
                            Err(error.to_owned())
                        } else {
                            Ok(response["result"].clone())
                        };
                        let _ = reply.send(result);
                    }
                }
            }
            if !reader.quitting.load(Ordering::Acquire) {
                crate::events::record(&data, "collector stopped unexpectedly");
            }
            reader.invalidate("The local collector stopped. Quit and reopen Mission Control to recover from its last checkpoint.").await;
            let _ = app.emit("workspace-changed", ());
        });
        Ok(backend)
    }

    pub async fn call(&self, method: &str, args: Value) -> Reply {
        if self.failed.load(Ordering::Acquire) {
            return Err(
                "The local collector is unavailable. Quit and reopen Mission Control.".into(),
            );
        }
        let priority = matches!(method, "shutdown" | "monitoring.pause")
            || method == "control" && args["action"] == "pause";
        let _permit = if method == "shutdown" {
            None
        } else {
            Some(
                (if priority {
                    &self.control_slots
                } else {
                    &self.slots
                })
                .try_acquire()
                .map_err(|_| "Too many local requests. Wait for pending work to finish.")?,
            )
        };
        let id = self.next.fetch_add(1, Ordering::Relaxed);
        let mut bytes =
            serde_json::to_vec(&json!({"id":id.to_string(),"method":method,"args":args}))
                .map_err(|_| "Invalid request")?;
        if bytes.len() > 262144 {
            return Err("Request exceeds the local input limit.".into());
        }
        bytes.push(b'\n');
        let (sender, receiver) = oneshot::channel();
        {
            let mut pending = self
                .pending
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            // Admission and invalidation use the same lock, so nothing can be added
            // just after the reader has failed all pending responses.
            if self.failed.load(Ordering::Acquire) {
                return Err(
                    "The local collector is unavailable. Quit and reopen Mission Control.".into(),
                );
            }
            pending.insert(id, sender);
        }
        let _pending = PendingReply(&self.pending, id);
        // Include mutex acquisition and pipe backpressure in the deadline. A partial
        // timed-out frame invalidates the transport, so later calls cannot corrupt it.
        let write = tokio::time::timeout(Duration::from_secs(10), async {
            let mut input = self.input.lock().await;
            // Requests admitted before another writer failed may still be queued here.
            // Never append their frames after a partially written request.
            if self.failed.load(Ordering::Acquire) {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::BrokenPipe,
                    "Collector transport is closed",
                ));
            }
            match input.as_mut() {
                Some(input) => {
                    input.write_all(&bytes).await?;
                    // On Windows, write_all can complete after enqueueing a blocking
                    // pipe write. Flush waits for that write within this deadline.
                    input.flush().await
                }
                None => Err(std::io::Error::new(
                    std::io::ErrorKind::BrokenPipe,
                    "Collector transport is closed",
                )),
            }
        })
        .await;
        if !matches!(write, Ok(Ok(()))) {
            self.invalidate(
                "The local collector stopped accepting requests. Quit and reopen Mission Control.",
            )
            .await;
            return Err(
                "The local collector stopped accepting requests. Quit and reopen Mission Control."
                    .into(),
            );
        }
        await_reply(receiver, response_timeout(method, priority)).await
    }

    fn fail_pending(&self, message: &str) {
        let mut pending = self
            .pending
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        self.failed.store(true, Ordering::Release);
        for (_, reply) in pending.drain() {
            let _ = reply.send(Err(message.to_owned()));
        }
    }

    async fn invalidate(&self, message: &str) {
        self.fail_pending(message);
        // Dropping stdin sends EOF, which asks the backend to stop and dispose cleanly.
        // A blocked Windows I/O worker may retain a pipe handle after this drop, so
        // bound the grace period and reap only this owned child if it cannot finish.
        if let Ok(mut input) = tokio::time::timeout(Duration::from_secs(1), self.input.lock()).await
        {
            input.take();
        }
        if let Ok(mut child) = tokio::time::timeout(Duration::from_secs(1), self.child.lock()).await
        {
            if tokio::time::timeout(Duration::from_secs(2), child.wait())
                .await
                .is_err()
            {
                let _ = child.start_kill();
                let _ = tokio::time::timeout(Duration::from_secs(1), child.wait()).await;
            }
        }
    }

    pub async fn shutdown(&self) {
        let _ =
            tokio::time::timeout(Duration::from_secs(20), self.call("shutdown", json!({}))).await;
        let mut child = self.child.lock().await;
        if tokio::time::timeout(Duration::from_secs(5), child.wait())
            .await
            .is_err()
        {
            let _ = child.kill().await;
            let _ = child.wait().await;
        }
    }
}

#[cfg(test)]
#[path = "backend_tests.rs"]
mod tests;
