import { useState, type FormEvent } from "react";
import { api, ApiError } from "../../api/client";
import { Button } from "../../components/Button";
import { Field, TextInput } from "../../components/Field";

/**
 * Shown only when PresApp is opened on the server itself through the emergency port.
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
      setError(err instanceof ApiError ? err.message : "No se pudo iniciar sesión.");
      setBusy(false);
    }
  }

  return (
    <main className="grid min-h-dvh place-items-center bg-ink px-6">
      <form onSubmit={submit} className="w-full max-w-sm rounded-lg bg-panel p-6">
        <h1 className="text-2xl font-semibold">Acceso de emergencia</h1>
        <p className="mt-1 text-muted">Solo funciona desde este servidor, para cuando internet o Cloudflare fallan.</p>
        <div className="mt-5 space-y-4">
          <Field label="Correo del administrador">
            <TextInput type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          <Field label="Contraseña de emergencia" error={error}>
            <TextInput type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        </div>
        <Button type="submit" busy={busy} className="mt-6 w-full">
          Entrar
        </Button>
      </form>
    </main>
  );
}
