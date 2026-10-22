/** The package's Management API (ManagementApi/ in the package), for the dashboard. */

interface ApiConfiguration {
  baseUrl: string;
  credentials: RequestCredentials;
  token?: string | (() => Promise<string | undefined>);
}

let configuration: ApiConfiguration = { baseUrl: "", credentials: "same-origin" };

export const configureApi = (next: ApiConfiguration): void => {
  configuration = next;
};

export interface NamedKey {
  key: string;
  name: string | null;
}

export interface ResponseSummary {
  key: string;
  path: string;
  query: string;
  culture: string;
  seenUtc: string;
}

export interface KeyLookup {
  key: string;
  kind: string;
  name: string | null;
  response: {
    path: string;
    query: string;
    culture: string;
    segment: string;
    startItem: string;
    renderedUtc: string;
    seenUtc: string;
    keys: NamedKey[];
  } | null;
  responseCount: number;
  responses: ResponseSummary[];
}

export interface SearchResult {
  items: { key: string; name: string; kind: "content" | "media" }[];
  keys: NamedKey[];
  responses: ResponseSummary[];
}

export type PurgeState = "Pending" | "Sent" | "Dead" | "Discarded";

export interface Purge {
  id: string;
  changeId: string;
  target: string;
  keys: string[];
  state: PurgeState;
  attempts: number;
  notBeforeUtc: string;
  sentUtc: string | null;
  lastError: string | null;
}

export interface ChangeLookup {
  id: string;
  createdUtc: string;
  kind: string;
  server: string;
  status: "Pending" | "Indexing" | "Released";
  releasedUtc: string | null;
  acknowledgedBy: string[];
  notAcknowledgedBy: string[];
  indexedBy: string[];
  change: { reason: string | null; items: { key: string; typeAlias: string }[]; keys: string[] | null; waitFor: string | null };
  plan: { keys: string[]; everything: boolean; cultures: string[] | null };
  responses: string[];
  purges: Purge[];
}

/** A failed call, with the message the API gave. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string
  ) {
    super(message);
  }
}

const request = async <T>(method: "GET" | "POST", path: string, body?: unknown): Promise<T> => {
  const token = typeof configuration.token === "function" ? await configuration.token() : configuration.token;
  const response = await fetch(`${configuration.baseUrl}/umbraco/management/api/v1/cache-tags/${path}`, {
    method,
    credentials: configuration.credentials,
    headers: {
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const json = text ? JSON.parse(text) : null;
  if (!response.ok) {
    throw new ApiError(response.status, json?.error ?? `The server answered ${response.status}.`);
  }
  return json as T;
};

export const api = {
  /** What a key stands for: a response-key's request and keys, or the responses another key reaches. Null when nothing has it. */
  lookup: async (key: string): Promise<KeyLookup | null> => {
    try {
      return await request<KeyLookup>("GET", `lookup?key=${encodeURIComponent(key)}`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        return null;
      }
      throw error;
    }
  },
  /** Documents and media by name, and recorded keys and responses whose key, path or query contain the text. */
  search: (text: string) => request<SearchResult>("GET", `search?q=${encodeURIComponent(text)}`),
  changes: (take = 20) => request<ChangeLookup[]>("GET", `changes?take=${take}`),
  purges: (state: PurgeState, take = 50) => request<Purge[]>("GET", `purges?state=${state}&take=${take}`),
  purge: (keys: string[]) => request<{ changeId: string }>("POST", "purge", keys),
  retry: (id: string) => request<null>("POST", `purges/${id}/retry`),
  discard: (id: string) => request<null>("POST", `purges/${id}/discard`),
};
