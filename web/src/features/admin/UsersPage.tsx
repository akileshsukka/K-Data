import { useMemo, useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import type { AppUser, CloudflareResult } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { formatDateTime } from "../../components/dates";
import { AdminTabs } from "./AdminTabs";
import { useRoles, useSaveUser, useSetUserActive, useUsers, type SaveUser } from "./useAdmin";

/** Who can enter PresApp and with which role. The e-mail is the login: Cloudflare sends the code there. */
export function UsersPage() {
  const users = useUsers();
  const [search, setSearch] = useState("");
  const [editing, setEditing] = useState<AppUser | "new" | null>(null);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    const list = users.data ?? [];
    return q ? list.filter((u) => `${u.displayName} ${u.email} ${u.role}`.toLowerCase().includes(q)) : list;
  }, [users.data, search]);

  return (
    <div>
      <AdminTabs />
      <PageHeader
        title="Usuarios"
        description="Cada persona entra con su correo: Cloudflare le envía un código y PresApp le aplica su rol."
        actions={<Button onClick={() => setEditing("new")}>Agregar usuario</Button>}
      />

      <div className="mt-6 max-w-sm">
        <TextInput type="search" placeholder="Buscar por nombre, correo o rol" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Buscar usuarios" />
      </div>

      {users.isPending && <p className="mt-6 text-muted">Cargando usuarios…</p>}
      {users.error && <p className="mt-6 text-hold">{users.error.message}</p>}
      {users.data && filtered.length === 0 && <p className="mt-6 text-muted">Ningún usuario coincide con «{search}».</p>}

      {filtered.length > 0 && (
        <>
          <table className="mt-6 hidden w-full border-separate border-spacing-0 overflow-hidden rounded-lg border border-line bg-panel md:table">
            <thead>
              <tr className="text-left font-display text-[15px] text-muted">
                <th className="border-b border-line px-4 py-3 font-semibold">Nombre</th>
                <th className="border-b border-line px-4 py-3 font-semibold">Rol</th>
                <th className="border-b border-line px-4 py-3 font-semibold">Último ingreso</th>
                <th className="border-b border-line px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {filtered.map((u) => (
                <tr key={u.id} className={u.isActive ? "" : "text-muted"}>
                  <td className="border-b border-line px-4 py-3">
                    <span className="block font-semibold text-ink">{u.displayName}</span>
                    <span className="text-sm text-muted">{u.email}</span>
                  </td>
                  <td className="border-b border-line px-4 py-3">
                    {u.role}
                    {!u.isActive && <span className="ml-2 rounded bg-surface px-1.5 py-0.5 text-sm">Inactivo</span>}
                  </td>
                  <td className="border-b border-line px-4 py-3 text-sm">{u.lastLoginAtUtc ? formatDateTime(u.lastLoginAtUtc) : "Nunca"}</td>
                  <td className="border-b border-line px-4 py-3 text-right">
                    <UserActions user={u} onEdit={() => setEditing(u)} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <ul className="mt-6 space-y-3 md:hidden">
            {filtered.map((u) => (
              <li key={u.id} className="rounded-lg border border-line bg-panel p-4">
                <p className="font-semibold text-ink">{u.displayName}</p>
                <p className="text-sm break-all text-muted">{u.email}</p>
                <p className="mt-1 text-sm">
                  {u.role}
                  {!u.isActive && " — inactivo"}
                </p>
                <div className="mt-3">
                  <UserActions user={u} onEdit={() => setEditing(u)} />
                </div>
              </li>
            ))}
          </ul>
        </>
      )}

      <UserDialog user={editing} onClose={() => setEditing(null)} />
    </div>
  );
}

function UserActions({ user, onEdit }: { user: AppUser; onEdit: () => void }) {
  const session = useSession();
  const setActive = useSetUserActive();
  const toast = useToast();
  const isSelf = user.email === session.email;

  async function toggle() {
    const active = !user.isActive;
    if (!active && !window.confirm(`¿Desactivar a ${user.displayName}? Perderá el acceso de inmediato.`)) return;
    try {
      const result = await setActive.mutateAsync({ id: user.id, active });
      toast(active ? `${user.displayName} puede volver a entrar` : `${user.displayName} ya no puede entrar`);
      reportCloudflare(toast, result.cloudflare);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "No se pudo cambiar el usuario.", "error");
    }
  }

  return (
    <div className="flex flex-wrap justify-end gap-2">
      <Button variant="secondary" onClick={onEdit}>
        Editar
      </Button>
      {!isSelf && (
        <Button variant={user.isActive ? "danger" : "secondary"} busy={setActive.isPending} onClick={toggle}>
          {user.isActive ? "Desactivar" : "Reactivar"}
        </Button>
      )}
    </div>
  );
}

function UserDialog({ user, onClose }: { user: AppUser | "new" | null; onClose: () => void }) {
  const isNew = user === "new";
  const existing = user && user !== "new" ? user : undefined;
  return (
    <Dialog open={user !== null} title={isNew ? "Agregar usuario" : "Editar usuario"} onClose={onClose}>
      {user !== null && <UserForm key={existing?.id ?? "new"} existing={existing} onDone={onClose} />}
    </Dialog>
  );
}

function UserForm({ existing, onDone }: { existing?: AppUser; onDone: () => void }) {
  const roles = useRoles();
  const save = useSaveUser();
  const toast = useToast();
  const [form, setForm] = useState<SaveUser>({
    email: existing?.email ?? "",
    displayName: existing?.displayName ?? "",
    roleId: existing?.roleId ?? 0,
  });
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function submit(e: FormEvent) {
    e.preventDefault();
    setErrors({});
    try {
      const result = await save.mutateAsync({ id: existing?.id, data: form });
      toast(existing ? "Usuario guardado" : `${result.user.displayName} agregado`);
      reportCloudflare(toast, result.cloudflare);
      onDone();
    } catch (err) {
      if (err instanceof ApiError && Object.keys(err.fieldErrors).length > 0) {
        setErrors(Object.fromEntries(Object.entries(err.fieldErrors).map(([k, v]) => [k, v[0]])));
      } else {
        toast(err instanceof ApiError ? err.message : "No se pudo guardar.", "error");
      }
    }
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Correo" error={errors.email} hint="Es el que usará para recibir el código de acceso.">
        <TextInput type="email" required autoFocus value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
      </Field>
      <Field label="Nombre" error={errors.displayName}>
        <TextInput required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
      </Field>
      <Field label="Rol" error={errors.roleId}>
        <SelectInput required value={form.roleId || ""} onChange={(e) => setForm({ ...form, roleId: Number(e.target.value) })}>
          <option value="" disabled>
            Elige un rol
          </option>
          {roles.data?.map((r) => (
            <option key={r.id} value={r.id}>
              {r.name} — {r.description}
            </option>
          ))}
        </SelectInput>
      </Field>
      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="secondary" onClick={onDone}>
          Cancelar
        </Button>
        <Button type="submit" busy={save.isPending}>
          {existing ? "Guardar" : "Agregar usuario"}
        </Button>
      </div>
    </form>
  );
}

function reportCloudflare(toast: ReturnType<typeof useToast>, result: CloudflareResult) {
  if (!result.configured) toast(result.message, "info");
  else if (!result.success) toast(result.message, "error");
}
