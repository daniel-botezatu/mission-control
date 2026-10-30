export interface SourceConfig {
  id: string;
  kind: string;
  name: string;
  rootPath: string;
  enabled: boolean;
  environment: string;
}

export interface SourceStatus {
  id: string;
  name: string;
  kind: string;
  enabled: boolean;
  state: string;
  lastSynced: string | null;
  pending: number;
  gap: string | null;
  rootPath: string;
  environment: string;
  progress?: {
    scannedFiles: number;
    pendingFiles: number;
    scannedBytes: number;
    totalBytes: number;
    pendingBytes: number;
    lastCheckpoint: string | null;
  } | null;
}

export interface EvidenceRecord {
  bodyLength?: number | null;
  nextBodyOffset?: number | null;
  id: string;
  sourceId: string;
  streamId: string;
  sessionId: string;
  projectId: string | null;
  sourceKind: string;
  kind: string;
  title: string;
  body: string;
  author: string;
  timestamp: string;
  ingestedAt: string;
  workingDirectory: string | null;
  branch: string | null;
  state: string | null;
  url: string | null;
  dataJson: string | null;
  sourceVersion?: string | null;
  changedAt?: string | null;
}

export interface Correction {
  recordId: string;
  projectId?: string | null;
  task?: string | null;
  dismissed?: boolean;
  snoozedUntil?: string | null;
  note?: string | null;
}

export interface ProjectInfo {
  id: string;
  name: string;
  color: string;
  description: string;
  rootPath: string | null;
  autoDiscovered?: boolean;
  isApproved: boolean;
  approvedAt?: string | null;
  repositories?: ProjectRepository[] | null;
}

export interface ProjectRepository {
  localRoot: string;
  gitHubRepositories: string[];
}

export interface QueryOptions {
  byTimestamp?: boolean;
  sourceId?: string | null;
  search?: string | null;
  projectId?: string | null;
  sourceKind?: string | null;
  kind?: string | null;
  limit?: number;
  offset?: number;
  since?: string | null;
  person?: string | null;
  sessionId?: string | null;
  state?: string | null;
}

export interface UnpushedBranch {
  repository: string;
  checkout: string;
  branch: string;
  state: "ahead" | "never_pushed" | "unverified";
  lastCommitAt: string;
  commits?: number | null;
}

export interface LocalOpenLoops {
  branches: UnpushedBranch[];
  branchesOmitted?: number;
  drafts: EvidenceRecord[];
  checkedAt: string;
}

export interface QueryResult {
  searchNotice?: string | null;
  records: EvidenceRecord[];
  total: number;
}

export interface AppSnapshot {
  isPartial?: boolean;
  mode: string;
  monitoring: string;
  projects: ProjectInfo[];
  records: EvidenceRecord[];
  sources: SourceStatus[];
  corrections: Correction[];
  totalRecords: number;
  sqliteVersion: string;
  lastCheckpoint: string | null;
  databaseBytes: number;
  projectDiscoveryGap?: string | null;
}

export interface Settings {
  lookbackDays?: number;
  storageLimitMb?: number;
  includeSubagents?: boolean;
  retentionDays: number;
  startWithWindows: boolean;
  telemetry: false;
  dataDirectory?: string;
}

export interface CollectionStatistics {
  mode: string;
  monitoring: string;
  savedEntries: number;
  sessions: number;
  userMessages: number;
  finalResponses: number;
  statusEvents: number;
  databaseBytes: number;
  activeDatabaseBytes: number;
  storageLimitBytes: number;
  lastCheckpoint: string | null;
  catchingUp: boolean;
  pendingFiles?: number;
  pendingBytes?: number;
  scannedBytes?: number;
  totalSourceBytes?: number;
}

interface RpcRequest {
  id: string;
  method: string;
  args: unknown;
}

interface RpcResponse {
  id?: string | number;
  result?: unknown;
  error?: unknown;
  event?: string;
  type?: string;
  data?: unknown;
}

interface WebViewBridge {
  postMessage(message: RpcRequest): void;
  addEventListener(
    type: "message",
    listener: (event: MessageEvent<unknown>) => void,
  ): void;
}

interface PendingRequest {
  resolve(value: unknown): void;
  reject(reason: Error): void;
  cancelTimeout(): void;
}

const pending = new Map<string, PendingRequest>();
const changedListeners = new Set<() => void>();
const sessionId = Math.random().toString(36).slice(2);
let nextId = 0;
let attachedWebView: WebViewBridge | undefined;
type TauriApi = {
  core: { invoke: <T>(command: string, args: unknown) => Promise<T> };
  event: {
    listen: (event: string, listener: () => void) => Promise<() => void>;
  };
};
let tauriAttached = false;
function getTauri(): TauriApi | undefined {
  const tauri = (window as Window & { __TAURI__?: TauriApi }).__TAURI__;
  if (tauri && !tauriAttached) {
    tauriAttached = true;
    void tauri.event.listen("workspace-changed", notifyChanged).catch(() => {
      tauriAttached = false;
    });
  }
  return tauri;
}

function errorMessage(error: unknown): string {
  if (typeof error === "string") return error;
  if (error && typeof error === "object" && "message" in error) {
    return String(error.message);
  }
  return "The local service could not complete this request.";
}

function notifyChanged(): void {
  for (const listener of changedListeners) {
    try {
      listener();
    } catch (error) {
      console.error("Mission Control change listener failed.", error);
    }
  }
}

function receiveMessage(event: MessageEvent<unknown>): void {
  let message = event.data;
  if (typeof message === "string") {
    if (message === "changed") {
      notifyChanged();
      return;
    }
    try {
      message = JSON.parse(message) as unknown;
    } catch {
      return;
    }
  }
  if (!message || typeof message !== "object") return;
  const response = message as RpcResponse;
  if (response.event === "changed" || response.type === "changed") {
    notifyChanged();
    return;
  }
  if (response.id === undefined) return;
  const id = String(response.id);
  const request = pending.get(id);
  if (!request) return;
  request.cancelTimeout();
  pending.delete(id);
  if (response.error !== undefined && response.error !== null) {
    request.reject(new Error(errorMessage(response.error)));
  } else {
    request.resolve(response.result);
  }
}

function getWebView(): WebViewBridge | undefined {
  const host = window as Window & { chrome?: { webview?: WebViewBridge } };
  const webview = host.chrome?.webview;
  if (webview && attachedWebView !== webview) {
    webview.addEventListener("message", receiveMessage);
    attachedWebView = webview;
  }
  return webview;
}

// Browsers clamp timers above 2^31-1 ms to a near-immediate callback. Schedule
// long deadlines in chunks so a user-selected timeout never overflows.
function scheduleTimeout(timeoutMs: number, expire: () => void): () => void {
  if (timeoutMs === 0) return () => {};
  const deadline = Date.now() + timeoutMs;
  let timer: ReturnType<typeof setTimeout>;
  const tick = () => {
    const remaining = deadline - Date.now();
    if (remaining <= 0) expire();
    else timer = setTimeout(tick, Math.min(remaining, 2_147_483_647));
  };
  tick();
  return () => clearTimeout(timer);
}

/** Call the native service. A timeout of zero waits until completion or cancellation. */
function timeoutMessage(method: string, args: unknown): string {
  const action =
    method === "control" && args && typeof args === "object" && "action" in args
      ? String(args.action)
      : method;
  const operation =
    (
      {
        refresh: "refreshing local history",
        resume: "resuming collection",
        pause: "pausing collection",
      } as Record<string, string>
    )[action] || "completing this request";
  return `The local service took too long while ${operation}. Check Collection freshness on Overview; the request may still be running.`;
}

export async function rpc<T>(
  method: string,
  args: unknown = {},
  timeoutMs = 90_000,
): Promise<T> {
  const request: RpcRequest = { id: `${sessionId}-${++nextId}`, method, args };
  const tauri = getTauri();
  if (tauri) {
    return new Promise<T>((resolve, reject) => {
      const cancelTimeout = scheduleTimeout(timeoutMs, () =>
        reject(new Error(timeoutMessage(method, args))),
      );
      tauri.core
        .invoke<T>("workspace_rpc", { method, args })
        .then(resolve, (error) => reject(new Error(errorMessage(error))))
        .finally(cancelTimeout);
    });
  }
  const webview = getWebView();
  if (webview) {
    return new Promise<T>((resolve, reject) => {
      const cancelTimeout = scheduleTimeout(timeoutMs, () => {
        pending.delete(request.id);
        reject(new Error(timeoutMessage(method, args)));
      });
      pending.set(request.id, {
        resolve: (result) => resolve(result as T),
        reject,
        cancelTimeout,
      });
      try {
        webview.postMessage(request);
      } catch (error) {
        cancelTimeout();
        pending.delete(request.id);
        reject(error instanceof Error ? error : new Error(errorMessage(error)));
      }
    });
  }

  const controller = new AbortController();
  const cancelTimeout = scheduleTimeout(timeoutMs, () => controller.abort());
  try {
    const response = await fetch("/rpc", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Mission-Control-Dev": "local-preview",
      },
      body: JSON.stringify(request),
      signal: controller.signal,
    });
    if (!response.ok) {
      throw new Error(`The local service returned HTTP ${response.status}.`);
    }
    const envelope = (await response.json()) as RpcResponse;
    if (envelope.error !== undefined && envelope.error !== null) {
      throw new Error(errorMessage(envelope.error));
    }
    return envelope.result as T;
  } catch (error) {
    if (controller.signal.aborted) {
      throw new Error(timeoutMessage(method, args));
    }
    throw error;
  } finally {
    cancelTimeout();
  }
}

/** Subscribe to native data changes; the returned function removes the listener. */
export function subscribeChanged(listener: () => void): () => void {
  getTauri();
  getWebView();
  changedListeners.add(listener);
  return () => {
    changedListeners.delete(listener);
  };
}
