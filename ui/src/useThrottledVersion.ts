import { useEffect, useRef, useState } from "react";

// Refresh during continuous indexing instead of waiting forever for quiet.
export function useThrottledVersion(version: number, delay = 1500) {
  const [current, setCurrent] = useState(version);
  const latest = useRef(version);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  latest.current = version;
  useEffect(() => {
    if (version === current || timer.current) return;
    timer.current = setTimeout(() => {
      timer.current = undefined;
      setCurrent(latest.current);
    }, delay);
  }, [version, current, delay]);
  useEffect(() => () => clearTimeout(timer.current), []);
  return current;
}
