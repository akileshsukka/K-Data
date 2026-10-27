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

/** Who can enter K Data and with which role. The e-mail is the login: Cloudflare sends the code there. */
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
        title="Users"
        description="People sign in with their e-mail: Cloudflare sends them a code and K Data applies their role."
        actions={<Button onClick={() => setEditing("new")}>Add user</Button>}
      />

      <div className="mt-6 max-w-sm">
        <TextInput type="search" placeholder="Search by name, e-mail or role" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search users" />
      </div>

      {users.isPending && <p className="mt-6 text-muted">Loading users…</p>}
      {users.error && <p className="mt-6 text-hold">{users.error.message}</p>}
      {users.data && filtered.length === 0 && <p className="mt-6 text-muted">No user matches “{search}”.</p>}

      {filtered.length > 0 && (
        <>
          <table className="mt-6 hidden w-full border-separate border-spacing-0 overflow-hidden rounded-lg border border-line bg-panel md:table">
            <thead>
              <tr className="text-left font-display text-[15px] text-muted">
                <th className="border-b border-line px-4 py-3 font-semibold">Name</th>
                <th className="border-b border-line px-4 py-3 font-semibold">Role</th>
                <th className="border-b border-line px-4 py-3 font-semibold">Last sign-in</th>
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
                    {!u.isActive && <span className="ml-2 rounded bg-surface px-1.5 py-0.5 text-sm">Inactive</span>}
                  </td>
                  <td className="border-b border-line px-4 py-3 text-sm">{u.lastLoginAtUtc ? formatDateTime(u.lastLoginAtUtc) : "Never"}</td>
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
                  {!u.isActive && " — inactive"}
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
    if (!active && !window.confirm(`Deactivate ${user.displayName}? They lose access immediately.`)) return;
    try {
      const result = await setActive.mutateAsync({ id: user.id, active });
      toast(active ? `${user.displayName} can sign in again` : `${user.displayName} can no longer sign in`);
      reportCloudflare(toast, result.cloudflare);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "The user could not be changed.", "error");
    }
  }

  return (
    <div className="flex flex-wrap justify-end gap-2">
      <Button variant="secondary" onClick={onEdit}>
        Edit
      </Button>
      {!isSelf && (
        <Button variant={user.isActive ? "danger" : "secondary"} busy={setActive.isPending} onClick={toggle}>
          {user.isActive ? "Deactivate" : "Reactivate"}
        </Button>
      )}
    </div>
  );
}

function UserDialog({ user, onClose }: { user: AppUser | "new" | null; onClose: () => void }) {
  const isNew = user === "new";
  const existing = user && user !== "new" ? user : undefined;
  return (
    <Dialog open={user !== null} title={isNew ? "Add user" : "Edit user"} onClose={onClose}>
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
      toast(existing ? "User saved" : `${result.user.displayName} added`);
      reportCloudflare(toast, result.cloudflare);
      onDone();
    } catch (err) {
      if (err instanceof ApiError && Object.keys(err.fieldErrors).length > 0) {
        setErrors(Object.fromEntries(Object.entries(err.fieldErrors).map(([k, v]) => [k, v[0]])));
      } else {
        toast(err instanceof ApiError ? err.message : "Could not save.", "error");
      }
    }
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="E-mail" error={errors.email} hint="The address that receives the sign-in code.">
        <TextInput type="email" required autoFocus value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
      </Field>
      <Field label="Name" error={errors.displayName}>
        <TextInput required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
      </Field>
      <Field label="Role" error={errors.roleId}>
        <SelectInput required value={form.roleId || ""} onChange={(e) => setForm({ ...form, roleId: Number(e.target.value) })}>
          <option value="" disabled>
            Choose a role
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
          Cancel
        </Button>
        <Button type="submit" busy={save.isPending}>
          {existing ? "Save" : "Add user"}
        </Button>
      </div>
    </form>
  );
}

function reportCloudflare(toast: ReturnType<typeof useToast>, result: CloudflareResult) {
  if (!result.configured) toast(result.message, "info");
  else if (!result.success) toast(result.message, "error");
}
