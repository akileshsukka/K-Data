/** Error returned by the PresApp API, with the server's explanation in Spanish. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string | undefined,
    message: string,
    public readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
  }
}

type Body = Record<string, unknown> | unknown[] | undefined;

export async function api<T>(path: string, init: { method?: string; body?: Body; signal?: AbortSignal } = {}): Promise<T> {
  const response = await fetch(path, {
    method: init.method ?? "GET",
    headers: init.body ? { "Content-Type": "application/json" } : undefined,
    body: init.body ? JSON.stringify(init.body) : undefined,
    credentials: "include",
    signal: init.signal,
  });

  if (response.status === 204) return undefined as T;
  const text = await response.text();
  const data = text ? safeJson(text) : undefined;

  if (!response.ok) {
    const d = (data ?? {}) as { code?: string; detail?: string; title?: string; errors?: Record<string, string[]> };
    const message = d.detail ?? firstError(d.errors) ?? d.title ?? `Error ${response.status}`;
    throw new ApiError(response.status, d.code, message, d.errors ?? {});
  }
  return data as T;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

function firstError(errors?: Record<string, string[]>): string | undefined {
  if (!errors) return undefined;
  return Object.values(errors).flat()[0];
}
