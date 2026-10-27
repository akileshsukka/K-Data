/** Error returned by the PresApp API, with the server's explanation. */
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

/** Sent on every call; the API refuses changes without it (cross-site request protection). */
export const csrf = { "X-Requested-With": "K-Data" };

export async function api<T>(path: string, init: { method?: string; body?: Body; signal?: AbortSignal } = {}): Promise<T> {
  const response = await fetch(path, {
    method: init.method ?? "GET",
    headers: init.body ? { "Content-Type": "application/json", ...csrf } : csrf,
    body: init.body ? JSON.stringify(init.body) : undefined,
    credentials: "include",
    signal: init.signal,
  });
  return read<T>(response);
}

/** Sends one file as multipart form data (field "file"). */
export async function upload<T>(path: string, file: File, method = "POST"): Promise<T> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch(path, { method, headers: csrf, body: form, credentials: "include" });
  return read<T>(response);
}

/** Downloads a file from the API and hands it to the browser with the server's file name. */
export async function download(path: string): Promise<string> {
  const response = await fetch(path, { headers: csrf, credentials: "include" });
  if (!response.ok) await read(response);
  const disposition = response.headers.get("Content-Disposition") ?? "";
  const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition);
  const name = match ? decodeURIComponent(match[1] ?? match[2]) : "download.xlsx";
  const url = URL.createObjectURL(await response.blob());
  const a = Object.assign(document.createElement("a"), { href: url, download: name });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
  return name;
}

async function read<T>(response: Response): Promise<T> {
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
