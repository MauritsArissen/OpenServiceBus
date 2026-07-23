import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { explorerApi, type MessageDto, type QueueInfo } from "@/lib/api";
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
  | { type: "deadletter"; target: string; lockToken: string }
  | { type: "confirm"; title: string; description: string; destructive?: boolean; action: () => Promise<void> | void }
  | null;

type Store = {
  conn: string;
  mgmt: string;
  setConn: (v: string) => void;
  setMgmt: (v: string) => void;
  status: ConnStatus;
  pingResult: { management: string; serviceBus: string } | null;
  connect: () => Promise<void>;

  queues: QueueInfo[];
  topics: QueueInfo[];
  subsByTopic: Record<string, QueueInfo[]>;
  loading: boolean;
  refresh: () => Promise<void>;

  selected: Selected | null;
  select: (s: Selected | null) => void;
  descriptorFor: (s: Selected) => QueueInfo | undefined;

  locked: Record<string, Record<string, TrackedMessage>>;
  peeked: Record<string, MessageDto[]>;
  lockedCount: (target: string) => number;
  setPeekedFor: (target: string, msgs: MessageDto[]) => void;
  trackLocked: (target: string, msg: MessageDto) => void;
  untrack: (target: string, key: string) => void;
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
  const [dialog, setDialog] = useState<DialogState>(null);

  const setConn = useCallback((v: string) => {
    setConnState(v);
    localStorage.setItem(LS_CONN, v);
  }, []);
  const setMgmt = useCallback((v: string) => {
    setMgmtState(v);
    localStorage.setItem(LS_MGMT, v);
  }, []);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const [qs, ts] = await Promise.all([explorerApi.listQueues(mgmt), explorerApi.listTopics(mgmt)]);
      const subs: Record<string, QueueInfo[]> = {};
      await Promise.all(
        ts.map(async (t) => {
          subs[t.name] = await explorerApi.listSubscriptions(mgmt, t.name);
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
  }, [mgmt]);

  const connect = useCallback(async () => {
    setStatus("pending");
    setPingResult(null);
    try {
      const r = await explorerApi.ping(conn, mgmt);
      setPingResult(r);
      setStatus("ok");
      await refresh();
    } catch (e) {
      setStatus("err");
      setPingResult(null);
      toast.error("Connect failed: " + (e as Error).message);
    }
  }, [conn, mgmt, refresh]);

  // Auto-connect at boot, like the original.
  const booted = useRef(false);
  useEffect(() => {
    if (!booted.current) {
      booted.current = true;
      void connect();
    }
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

  const lockedCount = useCallback(
    (target: string) => Object.keys(locked[target] ?? {}).length,
    [locked],
  );

  const setPeekedFor = useCallback((target: string, msgs: MessageDto[]) => {
    setPeeked((p) => ({ ...p, [target]: msgs }));
  }, []);

  const trackLocked = useCallback((target: string, msg: MessageDto) => {
    setLocked((l) => ({
      ...l,
      [target]: { ...(l[target] ?? {}), [msgKey(msg)]: { ...msg, target } },
    }));
  }, []);

  const untrack = useCallback((target: string, key: string) => {
    setLocked((l) => {
      const next = { ...(l[target] ?? {}) };
      delete next[key];
      return { ...l, [target]: next };
    });
  }, []);

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
  }, []);

  const select = useCallback((s: Selected | null) => setSelected(s), []);

  const value = useMemo<Store>(
    () => ({
      conn, mgmt, setConn, setMgmt, status, pingResult, connect,
      queues: queues.filter((q) => isMainQueue(q.name)),
      topics, subsByTopic, loading, refresh,
      selected, select, descriptorFor,
      locked, peeked, lockedCount, setPeekedFor, trackLocked, untrack, updateLockedUntil, clearEntityLocal,
      dialog, setDialog,
    }),
    [conn, mgmt, setConn, setMgmt, status, pingResult, connect, queues, topics, subsByTopic, loading,
     refresh, selected, select, descriptorFor, locked, peeked, lockedCount, setPeekedFor, trackLocked,
     untrack, updateLockedUntil, clearEntityLocal, dialog],
  );

  return <StoreContext.Provider value={value}>{children}</StoreContext.Provider>;
}

export { entityAddress, dlqAddress };
