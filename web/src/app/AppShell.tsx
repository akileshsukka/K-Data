import { NavLink, Outlet, useParams } from "react-router";
import { useSession } from "./session";
import { lastUnit, useUnit } from "./useCatalog";
import { Perm } from "../api/types";

interface NavItem {
  to: string;
  label: string;
  end?: boolean;
}

/**
 * Frame of every signed-in page: indigo sidebar on wide screens, top bar and bottom tabs on phones.
 * The selected unit sits at the top of the sidebar because almost every screen works inside one unit.
 */
export function AppShell() {
  const session = useSession();
  const params = useParams();
  const unitId = Number(params.unitId) || lastUnit.get();
  const { unit, plantName } = useUnit(unitId);

  const unitItems: NavItem[] = unit ? [{ to: `/u/${unit.id}`, label: "Inicio de la unidad", end: true }] : [];
  const adminItems: NavItem[] = session.can(Perm.usersManage)
    ? [
        { to: "/admin/usuarios", label: "Usuarios" },
        { to: "/admin/roles", label: "Roles y permisos" },
        { to: "/admin/ingresos", label: "Registro de ingresos" },
      ]
    : [];

  return (
    <div className="min-h-dvh lg:grid lg:grid-cols-[16rem_1fr]">
      <aside className="sticky top-0 hidden h-dvh flex-col bg-ink text-white lg:flex">
        <Wordmark />
        <NavLink to="/unidades" className="mx-3 mt-2 block rounded-md px-3 py-3 hover:bg-ink-2" aria-label="Cambiar de unidad">
          {unit ? (
            <>
              <span className="block text-sm text-lilac">{plantName}</span>
              <span className="block font-display text-[2.75rem] leading-none font-bold tracking-tight">{unit.number}</span>
              <span className="mt-1 block text-sm text-white/70">Cambiar de unidad</span>
            </>
          ) : (
            <span className="block py-2 font-display text-xl font-semibold">Elegir unidad</span>
          )}
        </NavLink>
        <nav className="mt-4 flex-1 space-y-6 overflow-y-auto px-3" aria-label="Principal">
          {unitItems.length > 0 && <NavGroup items={unitItems} />}
          {adminItems.length > 0 && <NavGroup title="Administración" items={adminItems} />}
        </nav>
        <div className="border-t border-white/10 px-6 py-4 text-sm">
          <p className="truncate font-semibold">{session.displayName || session.email}</p>
          <p className="text-white/60">{session.role}</p>
        </div>
      </aside>

      <div className="flex min-w-0 flex-col pb-20 lg:pb-0">
        <header className="sticky top-0 z-20 flex items-center justify-between bg-ink px-4 py-2 text-white lg:hidden">
          <Wordmark compact />
          <NavLink to="/unidades" className="rounded-md px-3 py-1.5 font-display text-lg font-semibold hover:bg-ink-2">
            {unit ? unit.name : "Elegir unidad"}
          </NavLink>
        </header>
        <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 sm:px-6 lg:px-10 lg:py-10">
          <Outlet />
        </main>
      </div>

      <MobileTabs unitId={unit?.id} showAdmin={adminItems.length > 0} />
    </div>
  );
}

function Wordmark({ compact = false }: { compact?: boolean }) {
  return (
    <div className={compact ? "flex items-center gap-2" : "flex items-center gap-2 px-6 pt-6 pb-2"}>
      <img src="/favicon.svg" alt="" className="size-7" />
      <span className="font-display text-xl font-semibold tracking-tight">PresApp</span>
      {!compact && <span className="ml-auto text-xs text-white/50">EnerMech</span>}
    </div>
  );
}

function NavGroup({ title, items }: { title?: string; items: NavItem[] }) {
  return (
    <div>
      {title && <p className="px-3 pb-1 text-sm text-white/50">{title}</p>}
      <ul className="space-y-0.5">
        {items.map((item) => (
          <li key={item.to}>
            <NavLink
              to={item.to}
              end={item.end}
              className={({ isActive }) =>
                `block rounded-md px-3 py-2 font-medium ${isActive ? "bg-violet text-white" : "text-white/85 hover:bg-ink-2"}`
              }
            >
              {item.label}
            </NavLink>
          </li>
        ))}
      </ul>
    </div>
  );
}

function MobileTabs({ unitId, showAdmin }: { unitId?: number; showAdmin: boolean }) {
  const tabs: NavItem[] = [
    ...(unitId ? [{ to: `/u/${unitId}`, label: "Unidad", end: true }] : []),
    { to: "/unidades", label: "Unidades" },
    ...(showAdmin ? [{ to: "/admin/usuarios", label: "Admin" }] : []),
  ];
  return (
    <nav
      className="fixed inset-x-0 bottom-0 z-30 grid border-t border-line bg-panel pb-[env(safe-area-inset-bottom)] lg:hidden"
      style={{ gridTemplateColumns: `repeat(${tabs.length}, minmax(0, 1fr))` }}
      aria-label="Principal"
    >
      {tabs.map((t) => (
        <NavLink
          key={t.to}
          to={t.to}
          end={t.end}
          className={({ isActive }) =>
            `py-3 text-center text-[15px] font-semibold ${isActive ? "text-violet" : "text-muted"}`
          }
        >
          {t.label}
        </NavLink>
      ))}
    </nav>
  );
}
