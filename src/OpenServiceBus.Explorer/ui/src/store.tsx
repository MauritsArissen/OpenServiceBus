import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { explorerApi, type MessageDto, type PingResult, type QueueInfo } from "@/lib/api";
import { dlqAddress, entityAddress, isMainQueue, type Selected } from "@/lib/format";

// localStorage keys preserved from the original explorer.
const LS_CONN = "osb_conn";
const LS_MGMT = "osb_mgmt";
export const LS_THEME = "osb_theme";

const DEFAULT_CONN =
  "Endpoint=sb://127.0.0.1:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
const DEFAULT_MGMT = "http://127.0.0.1:5300";

export type ConnStatus = "disconnected" | "pending" | "ok" | "err";

export type TrackedMessage = MessageDto & { target: string };

/** Stable client-side key for a message (sequence first, lock token fallback). */
export const msgKey = (m: MessageDto) =>
  m.sequenceNumber != null ? `seq:${m.sequenceNumber}` : `lock:${m.lockToken}`;

export type DialogState =
  | { type: "createQueue" }
  | { type: "createTopic" }
  | { type: "createSubscription"; topic?: string }
  | { type: "rule"; topic: string; sub: string; edit?: import("@/lib/api").RuleInfo }
  | { type: "deadletter"; target: string; lockTokens: string[] }
  | { type: "resend"; target: string; sequenceNumbers: number[] }
  | { type: "confirm"; title: string; description: string; destructive?: boolean; action: () => Promise<void> | void }
  | null;

type Store = {
  conn: string;
  mgmt: string;
  setConn: (v: string) => void;
  setMgmt: (v: string) => void;
  status: ConnStatus;
  pingResult: PingResult | null;
  /** True when the connected broker is OpenServiceBus and advertises the purge capability. */
  purgeCapable: boolean;
  connect: () => Promise<void>;

  /** Hosted live demo: connection is locked and a reset countdown is shown. */
  demoMode: boolean;
  /** Reset cadence (seconds) - the environment wipes & reseeds on these boundaries. */
  resetIntervalSeconds: number;
  /** The Explorer's release version (from /api/config); null on local/source runs. */
  version: string | null;

  queues: QueueInfo[];
  topics: QueueInfo[];
  subsByTopic: Record<string, QueueInfo[]>;
  loading: boolean;
  refresh: () => Promise<void>;

  selected: Selected | null;
  select: (s: Selected | null) => void;
  descriptorFor: (s: Selected) => QueueInfo | undefined;
  /** Active count of an entity address's /$DeadLetterQueue sibling (0 if none). */
  dlqCount: (address: string) => number;

  locked: Record<string, Record<string, TrackedMessage>>;
  peeked: Record<string, MessageDto[]>;
  /** Next sequence number a "Peek next" continues from (absent = no browse in progress). */
  peekCursor: Record<string, number>;
  lockedCount: (target: string) => number;
  setPeekedFor: (target: string, msgs: MessageDto[], opts?: { append?: boolean }) => void;
  setPeekCursor: (target: string, next: number | null) => void;
  trackLocked: (target: string, msg: MessageDto) => void;
  untrack: (target: string, key: string) => void;
  untrackMany: (target: string, keys: string[]) => void;
  updateLockedUntil: (target: string, key: string, until: string) => void;
  clearEntityLocal: (address: string) => void;

  dialog: DialogState;
  setDialog: (d: DialogState) => void;
};

const StoreContext = createContext<Store>(null!);
export const useStore = () => useContext(StoreContext);

export function StoreProvider({ children }: { children: React.ReactNode }) {
  const [conn, setConnState] = useState(() => localStorage.getItem(LS_CONN) ?? DEFAULT_CONN);
  const [mgmt, setMgmtState] = useState(() => localStorage.getItem(LS_MGMT) ?? DEFAULT_MGMT);
  const [status, setStatus] = useState<ConnStatus>("disconnected");
  const [pingResult, setPingResult] = useState<Store["pingResult"]>(null);
  const [queues, setQueues] = useState<QueueInfo[]>([]);
  const [topics, setTopics] = useState<QueueInfo[]>([]);
  const [subsByTopic, setSubsByTopic] = useState<Record<string, QueueInfo[]>>({});
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<Selected | null>(null);
  const [locked, setLocked] = useState<Record<string, Record<string, TrackedMessage>>>({});
  const [peeked, setPeeked] = useState<Record<string, MessageDto[]>>({});
  const [peekCursor, setPeekCursorState] = useState<Record<string, number>>({});
  const [dialog, setDialog] = useState<DialogState>(null);
  const [demoMode, setDemoMode] = useState(false);
  const [resetIntervalSeconds, setResetIntervalSeconds] = useState(1800);
  const [version, setVersion] = useState<string | null>(null);

  const setConn = useCallback((v: string) => {
    setConnState(v);
    localStorage.setItem(LS_CONN, v);
  }, []);
  const setMgmt = useCallback((v: string) => {
    setMgmtState(v);
    localStorage.setItem(LS_MGMT, v);
  }, []);

  // Entity CRUD goes through the backend's ServiceBusAdministrationClient, keyed by the
  // broker connection string (the ATOM management API shares the AMQP port).
  const refresh = useCallback(async (connOverride?: string) => {
    const c = connOverride ?? conn;
    setLoading(true);
    try {
      const [qs, ts] = await Promise.all([explorerApi.listQueues(c), explorerApi.listTopics(c)]);
      const subs: Record<string, QueueInfo[]> = {};
      await Promise.all(
        ts.map(async (t) => {
          subs[t.name] = await explorerApi.listSubscriptions(c, t.name);
        }),
      );
      setQueues(qs);
      setTopics(ts);
      setSubsByTopic(subs);
    } catch (e) {
      toast.error("Loading entities failed: " + (e as Error).message);
    } finally {
      setLoading(false);
    }
  }, [conn]);

  const connect = useCallback(async (connOverride?: string, mgmtOverride?: string) => {
    const c = connOverride ?? conn;
    const m = mgmtOverride ?? mgmt;
    setStatus("pending");
    setPingResult(null);
    try {
      const r = await explorerApi.ping(c, m);
      setPingResult(r);
      setStatus("ok");
      await refresh(c);
    } catch (e) {
      setStatus("err");
      setPingResult(null);
      toast.error("Connect failed: " + (e as Error).message);
    }
  }, [conn, mgmt, refresh]);

  // Boot: read /api/config first. In the hosted demo this pins the connection to the
  // co-located broker and enables demo mode; otherwise it's a no-op and the normal
  // localStorage connection is used. Then auto-connect.
  const booted = useRef(false);
  useEffect(() => {
    if (booted.current) return;
    booted.current = true;
    (async () => {
      try {
        const cfg = await fetch("/api/config").then((r) => r.json());
        if (cfg?.version) setVersion(String(cfg.version));
        if (cfg?.demoMode) {
          setDemoMode(true);
          setResetIntervalSeconds(cfg.resetIntervalSeconds ?? 1800);
          if (cfg.connectionString) setConnState(cfg.connectionString);
          if (cfg.managementUrl) setMgmtState(cfg.managementUrl);
          await connect(cfg.connectionString, cfg.managementUrl);
          return;
        }
      } catch {
        /* no config endpoint (older host) - carry on with localStorage values */
      }
      await connect();
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const descriptorFor = useCallback(
    (s: Selected): QueueInfo | undefined => {
      if (s.kind === "queue") return queues.find((q) => q.name === s.name);
      if (s.kind === "topic") return topics.find((t) => t.name === s.name);
      return subsByTopic[s.name]?.find((x) => x.name === s.sub);
    },
    [queues, topics, subsByTopic],
  );

  // A queue/subscription's dead-letter count is the active count of its separate
  // "…/$DeadLetterQueue" entity - which the raw queue list already contains.
  const dlqCount = useCallback(
    (address: string): number =>
      queues.find((q) => q.name.toLowerCase() === (address + "/$DeadLetterQueue").toLowerCase())
        ?.activeMessageCount ?? 0,
    [queues],
  );

  const lockedCount = useCallback(
    (target: string) => Object.keys(locked[target] ?? {}).length,
    [locked],
  );

  const setPeekedFor = useCallback((target: string, msgs: MessageDto[], opts?: { append?: boolean }) => {
    setPeeked((p) => {
      if (!opts?.append) return { ...p, [target]: msgs };
      const existing = p[target] ?? [];
      const seen = new Set(existing.map(msgKey));
      return { ...p, [target]: [...existing, ...msgs.filter((m) => !seen.has(msgKey(m)))] };
    });
  }, []);

  const setPeekCursor = useCallback((target: string, next: number | null) => {
    setPeekCursorState((c) => {
      if (next == null) {
        const rest = { ...c };
        delete rest[target];
        return rest;
      }
      return { ...c, [target]: next };
    });
  }, []);

  const trackLocked = useCallback((target: string, msg: MessageDto) => {
    setLocked((l) => ({
      ...l,
      [target]: { ...(l[target] ?? {}), [msgKey(msg)]: { ...msg, target } },
    }));
  }, []);

  // Settling a message removes it from the view entirely: both the locked entry and any
  // stale copy in the peeked browse snapshot (same msgKey). Without the latter, settling a
  // message that was also peeked earlier resurfaces it as an unlocked "peek" row - and,
  // because selection is keyed by msgKey, it comes back still selected.
  const untrackMany = useCallback((target: string, keys: string[]) => {
    if (keys.length === 0) return;
    const drop = new Set(keys);
    setLocked((l) => {
      const next = { ...(l[target] ?? {}) };
      for (const key of keys) delete next[key];
      return { ...l, [target]: next };
    });
    setPeeked((p) => {
      const cur = p[target];
      if (!cur) return p;
      const next = cur.filter((m) => !drop.has(msgKey(m)));
      return next.length === cur.length ? p : { ...p, [target]: next };
    });
  }, []);

  const untrack = useCallback((target: string, key: string) => {
    untrackMany(target, [key]);
  }, [untrackMany]);

  const updateLockedUntil = useCallback((target: string, key: string, until: string) => {
    setLocked((l) => {
      const entry = l[target]?.[key];
      if (!entry) return l;
      return { ...l, [target]: { ...l[target], [key]: { ...entry, lockedUntil: until } } };
    });
  }, []);

  const clearEntityLocal = useCallback((address: string) => {
    const dlq = address + "/$DeadLetterQueue";
    setLocked((l) => {
      const next = { ...l };
      delete next[address];
      delete next[dlq];
      return next;
    });
    setPeeked((p) => {
      const next = { ...p };
      delete next[address];
      delete next[dlq];
      return next;
    });
    setPeekCursorState((c) => {
      const next = { ...c };
      delete next[address];
      delete next[dlq];
      return next;
    });
  }, []);

  const select = useCallback((s: Selected | null) => setSelected(s), []);

  const value = useMemo<Store>(
    () => ({
      conn, mgmt, setConn, setMgmt, status, pingResult, connect,
      purgeCapable: pingResult?.broker?.capabilities?.includes("purge") ?? false,
      demoMode, resetIntervalSeconds, version,
      queues: queues.filter((q) => isMainQueue(q.name)),
      topics, subsByTopic, loading, refresh,
      selected, select, descriptorFor, dlqCount,
      locked, peeked, peekCursor, lockedCount, setPeekedFor, setPeekCursor, trackLocked, untrack, untrackMany,
      updateLockedUntil, clearEntityLocal,
      dialog, setDialog,
    }),
    [conn, mgmt, setConn, setMgmt, status, pingResult, connect, demoMode, resetIntervalSeconds, version, queues, topics, subsByTopic, loading,
     refresh, selected, select, descriptorFor, dlqCount, locked, peeked, peekCursor, lockedCount, setPeekedFor, setPeekCursor, trackLocked,
     untrack, untrackMany, updateLockedUntil, clearEntityLocal, dialog],
  );

  return <StoreContext.Provider value={value}>{children}</StoreContext.Provider>;
}

export { entityAddress, dlqAddress };
