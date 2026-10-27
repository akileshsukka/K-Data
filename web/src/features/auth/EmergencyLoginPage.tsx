import { useState, type FormEvent } from "react";
import { api, ApiError } from "../../api/client";
import { Button } from "../../components/Button";
import { Field, TextInput } from "../../components/Field";

/**
 * Shown only when K Data is opened on the server itself through the emergency port.
 * For when internet or Cloudflare is down.
 */
export function EmergencyLoginPage() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      await api("/api/emergency/login", { method: "POST", body: { email, password } });
      window.location.reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not sign in.");
      setBusy(false);
    }
  }

  return (
    <main className="grid min-h-dvh place-items-center bg-ink px-6">
      <form onSubmit={submit} className="w-full max-w-sm rounded-lg bg-panel p-6">
        <h1 className="text-2xl font-semibold">Emergency sign-in</h1>
        <p className="mt-1 text-muted">Works only on this server, for when the internet or Cloudflare is down.</p>
        <div className="mt-5 space-y-4">
          <Field label="Administrator e-mail">
            <TextInput type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <Field label="Emergency password" error={error}>
            <TextInput type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        </div>
        <Button type="submit" busy={busy} className="mt-6 w-full">
          Sign in
        </Button>
      </form>
    </main>
  );
}
