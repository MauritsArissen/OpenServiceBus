// Typed client for the Explorer backend (/api/*). The contract mirrors the original
// vanilla-JS explorer exactly: camelCase JSON bodies, managementUrl as a query-string
// parameter on GET/DELETE proxies and a body field on PUT proxies.

export type QueueInfo = {
  name: string;
  activeMessageCount?: number;
  deadLetterMessageCount?: number;
  maxDeliveryCount?: number;
  lockDuration?: string;
  defaultMessageTimeToLive?: string | null;
  deadLetteringOnMessageExpiration?: boolean;
  requiresSession?: boolean;
  requiresDuplicateDetection?: boolean;
  duplicateDetectionHistoryTimeWindow?: string | null;
  forwardTo?: string | null;
  forwardDeadLetteredMessagesTo?: string | null;
  backingQueueName?: string;
  topicName?: string;
  status?: string;
  autoDeleteOnIdle?: string | null;
  maxSizeInMegabytes?: number;
  maxMessageSizeInKilobytes?: number;
  sizeInBytes?: number | null;
};

export type RuleInfo = {
  name: string;
  filterType?: string;
  sqlExpression?: string | null;
  sqlActionExpression?: string | null;
  messageId?: string | null;
  correlationId?: string | null;
  subject?: string | null;
  to?: string | null;
  replyTo?: string | null;
  replyToSessionId?: string | null;
  sessionId?: string | null;
  contentType?: string | null;
  properties?: Record<string, string> | null;
};

export type MessageDto = {
  received: boolean;
  sequenceNumber: number | null;
  messageId: string | null;
  correlationId: string | null;
  subject: string | null;
  contentType: string | null;
  enqueuedTime: string | null;
  lockedUntil: string | null;
  expiresAt: string | null;
  timeToLive: string | null;
  deliveryCount: number | null;
  lockToken: string | null;
  body: string;
  applicationProperties: Record<string, string | null> | null;
  deadLetterReason: string | null;
  deadLetterErrorDescription: string | null;
  deadLetterSource: string | null;
};

export type SendPayload = {
  connectionString: string;
  queue: string;
  body: string | null;
  messageId: string | null;
  correlationId: string | null;
  subject: string | null;
  contentType: string | null;
  replyTo: string | null;
  to: string | null;
  sessionId: string | null;
  partitionKey: string | null;
  timeToLiveSeconds: number | null;
  scheduledEnqueueTime: string | null;
  properties: Record<string, string> | null;
  count: number;
  strategy: string;
};

async function api<T = unknown>(path: string, options: RequestInit = {}): Promise<T> {
  const res = await fetch(path, {
    headers: options.body ? { "Content-Type": "application/json" } : undefined,
    ...options,
  });
  let json: Record<string, unknown> = {};
  try {
    json = await res.json();
  } catch {
    /* non-JSON body */
  }
  if (!res.ok) {
    throw new Error(
      (json.error as string) || (json.title as string) || res.status + " " + res.statusText,
    );
  }
  return json as T;
}

// Entity CRUD is served by the backend's ServiceBusAdministrationClient, which needs the
// broker CONNECTION STRING (the ATOM management API lives on the AMQP port), not the
// JSON management URL - that one is still used for /health pings and metrics sampling.
const cs = (connectionString: string) => "?connectionString=" + encodeURIComponent(connectionString);
const post = (body: unknown): RequestInit => ({ method: "POST", body: JSON.stringify(body) });
const put = (body: unknown): RequestInit => ({ method: "PUT", body: JSON.stringify(body) });

export type BrokerInfo = {
  name?: string | null;
  version?: string | null;
  capabilities?: string[] | null;
};

export type PingResult = {
  management: string;
  serviceBus: string;
  atomManagement: string;
  broker?: BrokerInfo | null;
};

export const explorerApi = {
  ping: (connectionString: string, managementUrl: string) =>
    api<PingResult>(
      "/api/ping",
      post({ connectionString, managementUrl }),
    ),

  // OpenServiceBus-native: purge messages while keeping topology. Routed through the
  // backend to the broker's JSON management API; only enabled when the ping's broker
  // info advertises the "purge" capability.
  purge: (
    managementUrl: string,
    target: { queue?: string; topic?: string; subscription?: string; deadLetterOnly?: boolean },
  ) => api<{ purged: number; entities?: number }>("/api/purge", post({ managementUrl, ...target })),

  listQueues: (conn: string) => api<QueueInfo[]>("/api/queues" + cs(conn)),
  setStatus: (conn: string, kind: string, name: string, subscription: string | null, status: string) =>
    api<{ status: string }>("/api/status", put({ connectionString: conn, kind, name, subscription, status })),
  createQueue: (conn: string, name: string, options: Record<string, unknown>) =>
    api("/api/queues/" + encodeURIComponent(name), put({ connectionString: conn, options })),
  deleteQueue: (conn: string, name: string) =>
    api("/api/queues/" + encodeURIComponent(name) + cs(conn), { method: "DELETE" }),

  listTopics: (conn: string) => api<QueueInfo[]>("/api/topics" + cs(conn)),
  createTopic: (conn: string, name: string, options: Record<string, unknown>) =>
    api("/api/topics/" + encodeURIComponent(name), put({ connectionString: conn, options })),
  deleteTopic: (conn: string, name: string) =>
    api("/api/topics/" + encodeURIComponent(name) + cs(conn), { method: "DELETE" }),

  listSubscriptions: (conn: string, topic: string) =>
    api<QueueInfo[]>(`/api/topics/${encodeURIComponent(topic)}/subscriptions` + cs(conn)),
  createSubscription: (conn: string, topic: string, name: string, options: Record<string, unknown>) =>
    api(
      `/api/topics/${encodeURIComponent(topic)}/subscriptions/${encodeURIComponent(name)}`,
      put({ connectionString: conn, options }),
    ),
  deleteSubscription: (conn: string, topic: string, name: string) =>
    api(`/api/topics/${encodeURIComponent(topic)}/subscriptions/${encodeURIComponent(name)}` + cs(conn), {
      method: "DELETE",
    }),

  listRules: (conn: string, topic: string, sub: string) =>
    api<RuleInfo[]>(
      `/api/topics/${encodeURIComponent(topic)}/subscriptions/${encodeURIComponent(sub)}/rules` + cs(conn),
    ),
  putRule: (conn: string, topic: string, sub: string, name: string, rule: Record<string, unknown>) =>
    api(
      `/api/topics/${encodeURIComponent(topic)}/subscriptions/${encodeURIComponent(sub)}/rules/${encodeURIComponent(name)}`,
      put({ connectionString: conn, rule }),
    ),
  deleteRule: (conn: string, topic: string, sub: string, name: string) =>
    api(
      `/api/topics/${encodeURIComponent(topic)}/subscriptions/${encodeURIComponent(sub)}/rules/${encodeURIComponent(name)}` +
        cs(conn),
      { method: "DELETE" },
    ),

  send: (payload: SendPayload) =>
    api<{ sent: boolean; sentCount: number; strategy: string; messageId: string; scheduledFor: string | null }>(
      "/api/send",
      post(payload),
    ),
  peek: (connectionString: string, queue: string, maxMessages: number) =>
    api<{ count: number; messages: MessageDto[] }>("/api/peek", post({ connectionString, queue, maxMessages })),
  receive: (connectionString: string, queue: string, sessionId: string | null) =>
    api<MessageDto | { received: false }>(
      "/api/receive",
      post({ connectionString, queue, timeoutSeconds: 2, sessionId }),
    ),
  complete: (connectionString: string, queue: string, lockToken: string) =>
    api("/api/complete", post({ connectionString, queue, lockToken })),
  abandon: (connectionString: string, queue: string, lockToken: string) =>
    api("/api/abandon", post({ connectionString, queue, lockToken })),
  deadletter: (connectionString: string, queue: string, lockToken: string, reason: string | null, description: string | null) =>
    api("/api/deadletter", post({ connectionString, queue, lockToken, reason, description })),
  renew: (connectionString: string, queue: string, lockToken: string) =>
    api<{ renewedUntil: string }>("/api/renew", post({ connectionString, queue, lockToken })),
  defer: (connectionString: string, queue: string, lockToken: string) =>
    api<{ deferred: boolean; sequenceNumber: number }>("/api/defer", post({ connectionString, queue, lockToken })),
  requeue: (connectionString: string, queue: string, lockToken: string) =>
    api<{ requeued: boolean; target: string; messageId: string }>("/api/requeue", post({ connectionString, queue, lockToken })),

  metrics: (entity: string, windowSeconds: number) =>
    api<{ entity: MetricSample[]; dlq: MetricSample[] }>(
      `/api/metrics?entity=${encodeURIComponent(entity)}&windowSeconds=${windowSeconds}`,
    ),
};

export type MetricSample = { t: number; active: number; enqueued: number; completed: number };
