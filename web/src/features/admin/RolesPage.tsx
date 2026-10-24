import { useMemo, useState } from "react";
import { ApiError } from "../../api/client";
import type { Role } from "../../api/types";
import { Button } from "../../components/Button";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "./AdminTabs";
import { usePermissionCatalog, useRoles, useSaveRolePermissions } from "./useAdmin";

const ADMIN = "Admin";

/** What each role can do. Admin always has everything; the other roles can be adjusted. */
export function RolesPage() {
  const roles = useRoles();
  const catalog = usePermissionCatalog();
  const save = useSaveRolePermissions();
  const toast = useToast();
  const [draft, setDraft] = useState<Record<number, Set<string>>>({});

  const current = (role: Role) => draft[role.id] ?? new Set(role.permissions);
  const dirty = useMemo(
    () =>
      (roles.data ?? []).filter((r) => {
        const d = draft[r.id];
        return d && (d.size !== r.permissions.length || r.permissions.some((p) => !d.has(p)));
      }),
    [roles.data, draft],
  );

  function toggle(role: Role, permission: string) {
    const next = new Set(current(role));
    if (next.has(permission)) next.delete(permission);
    else next.add(permission);
    setDraft({ ...draft, [role.id]: next });
  }

  async function saveAll() {
    try {
      for (const role of dirty) await save.mutateAsync({ id: role.id, permissions: [...current(role)] });
      setDraft({});
      toast("Permisos guardados. Se aplican en menos de un minuto.");
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "No se pudieron guardar los permisos.", "error");
    }
  }

  return (
    <div>
      <AdminTabs />
      <PageHeader
        title="Roles y permisos"
        description="Marca lo que puede hacer cada rol. El servidor revisa estos permisos en cada acción."
        actions={
          <>
            {dirty.length > 0 && (
              <Button variant="secondary" onClick={() => setDraft({})}>
                Descartar
              </Button>
            )}
            <Button disabled={dirty.length === 0} busy={save.isPending} onClick={saveAll}>
              Guardar cambios
            </Button>
          </>
        }
      />

      {(roles.isPending || catalog.isPending) && <p className="mt-6 text-muted">Cargando…</p>}

      {roles.data && catalog.data && (
        <div className="mt-6 overflow-x-auto rounded-lg border border-line bg-panel">
          <table className="w-full min-w-[44rem] border-separate border-spacing-0">
            <thead>
              <tr>
                <th className="sticky left-0 border-b border-line bg-panel px-4 py-3 text-left font-display font-semibold text-muted">Permiso</th>
                {roles.data.map((r) => (
                  <th key={r.id} className="border-b border-line px-3 py-3 text-center font-display font-semibold text-ink">
                    {r.name}
                    <span className="block text-sm font-normal text-muted">
                      {r.userCount} {r.userCount === 1 ? "usuario" : "usuarios"}
                    </span>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {catalog.data.map((p) => (
                <tr key={p.key}>
                  <td className="sticky left-0 border-b border-line bg-panel px-4 py-2.5">{p.description}</td>
                  {roles.data.map((r) => {
                    const locked = r.name === ADMIN;
                    return (
                      <td key={r.id} className="border-b border-line px-3 py-2.5 text-center">
                        <input
                          type="checkbox"
                          className="size-5 accent-violet"
                          checked={locked || current(r).has(p.key)}
                          disabled={locked}
                          onChange={() => toggle(r, p.key)}
                          aria-label={`${r.name}: ${p.description}`}
                        />
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
