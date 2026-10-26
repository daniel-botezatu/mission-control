export function dashboardVisible() {
  return !document.hidden && !(window as Window & { __MC_MINIMIZED__?: boolean }).__MC_MINIMIZED__;
}

export function onDashboardVisibility(callback: () => void) {
  document.addEventListener("visibilitychange", callback);
  window.addEventListener("mc-visibilitychange", callback);
  return () => {
    document.removeEventListener("visibilitychange", callback);
    window.removeEventListener("mc-visibilitychange", callback);
  };
}
