import { useEffect } from "react";
import { NavLink, Outlet, useNavigate, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import { AssetSearch } from "../components/AssetSearch";
import { useSession } from "./session";
import { lastUnit, useUnit } from "./useCatalog";
import { Perm } from "../api/types";

interface NavItem {
  to: string;
  label: string;
  short?: string;
  end?: boolean;
  badge?: number;
}

/** Administration pages the signed-in user may open (sidebar, phone tabs). */
export function useAdminItems(): NavItem[] {
  const session = useSession();
  const issues = useQuery({
    queryKey: ["issues", "count"],
    queryFn: ({ signal }) => api<{ open: number }>("/api/import-issues/count", { signal }),
    enabled: session.can(Perm.excelImport),
    refetchInterval: 5 * 60_000,
  });
  return [
    ...(session.can(Perm.excelImport) ? [{ to: "/admin/import-issues", label: "Import issues", short: "Issues", badge: issues.data?.open }] : []),
    ...(session.can(Perm.simproSync) ? [{ to: "/admin/simpro", label: "Simpro sync", short: "Simpro" }] : []),
    ...(session.can(Perm.catalogManage)
      ? [
          { to: "/admin/excel-templates", label: "Excel templates", short: "Templates" },
          { to: "/admin/lists", label: "Lists", short: "Lists" },
          { to: "/admin/custom-fields", label: "Custom fields", short: "Fields" },
          { to: "/admin/pipe-specs", label: "Pipe specs", short: "Pipe specs" },
          { to: "/admin/line-codes", label: "Line codes", short: "Line codes" },
        ]
      : []),
    ...(session.can(Perm.usersManage)
      ? [
          { to: "/admin/users", label: "Users" },
          { to: "/admin/roles", label: "Roles and permissions", short: "Roles" },
          { to: "/admin/sign-ins", label: "Sign-in log", short: "Sign-ins" },
        ]
      : []),
  ];
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
  // Any unit page (register, Excel, history…) makes that unit the one shown on asset pages and at start.
  useEffect(() => { if (unit && Number(params.unitId) === unit.id) lastUnit.set(unit.id); }, [unit, params.unitId]);

  const navigate = useNavigate();
  const unitItems: NavItem[] = unit
    ? [
        { to: `/u/${unit.id}`, label: "Unit overview", end: true },
        { to: `/u/${unit.id}/equipment`, label: "Equipment Register" },
        { to: `/u/${unit.id}/lines`, label: "Pipeline Register" },
        { to: `/u/${unit.id}/ei`, label: "E&I Register" },
        { to: `/u/${unit.id}/monitoring`, label: "Monitoring" },
        { to: `/u/${unit.id}/groups`, label: "Workpacks" },
        ...(session.can(Perm.bulkEdit) ? [{ to: `/u/${unit.id}/bulk`, label: "Bulk edit" }] : []),
        ...(session.can(Perm.excelPage) ? [{ to: `/u/${unit.id}/excel`, label: "Excel" }] : []),
        ...(session.can(Perm.categoriesManage) ? [{ to: `/u/${unit.id}/categories`, label: "Dashboard categories" }] : []),
        { to: `/u/${unit.id}/history`, label: "History" },
      ]
    : [];
  const adminItems = useAdminItems();

  return (
    <div className="min-h-dvh lg:grid lg:grid-cols-[16rem_1fr]">
      <aside className="sticky top-0 hidden h-dvh flex-col bg-ink text-white lg:flex">
        <Wordmark />
        <NavLink to="/units" className="mx-3 mt-2 block rounded-md px-3 py-3 hover:bg-ink-2" aria-label="Change unit">
          {unit ? (
            <>
              <span className="block text-sm text-lilac">{plantName}</span>
              <span className="block font-display text-[2.75rem] leading-none font-bold tracking-tight">{unit.number}</span>
              <span className="mt-1 block text-sm text-white/70">Change unit</span>
            </>
          ) : (
            <span className="block py-2 font-display text-xl font-semibold">Choose a unit</span>
          )}
        </NavLink>
        <div className="mx-3 mt-3">
          <AssetSearch dark placeholder="Find any equipment" onPick={(h) => navigate(`/assets/${h.id}`)} />
        </div>
        <nav className="mt-4 flex-1 space-y-6 overflow-y-auto px-3" aria-label="Main">
          {unitItems.length > 0 && <NavGroup items={unitItems} />}
          {adminItems.length > 0 && <NavGroup title="Administration" items={adminItems} />}
        </nav>
        <div className="border-t border-white/10 px-6 py-4 text-sm">
          <p className="truncate font-semibold">{session.displayName || session.email}</p>
          <p className="text-white/60">{session.role}</p>
          <CompanyLogo />
        </div>
      </aside>

      <div className="flex min-w-0 flex-col pb-20 lg:pb-0">
        <header className="sticky top-0 z-20 flex items-center justify-between bg-ink px-4 py-2 text-white lg:hidden">
          <span className="flex min-w-0 items-center gap-3">
            <Wordmark compact />
            <CompanyLogo small />
          </span>
          <NavLink to="/units" className="shrink-0 rounded-md px-3 py-1.5 font-display text-lg font-semibold hover:bg-ink-2">
            {unit ? unit.name : "Choose a unit"}
          </NavLink>
        </header>
        <div className="bg-ink px-4 pb-3 lg:hidden">
          <AssetSearch dark placeholder="Find any equipment" onPick={(h) => navigate(`/assets/${h.id}`)} />
        </div>
        <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 sm:px-6 lg:px-10 lg:py-10">
          <Outlet />
        </main>
      </div>

      <MobileTabs unitId={unit?.id} adminHome={adminItems[0]?.to} />
    </div>
  );
}

function Wordmark({ compact = false }: { compact?: boolean }) {
  return (
    <div className={compact ? "flex items-center gap-2" : "flex items-center gap-2 px-6 pt-6 pb-2"}>
      <img src="/favicon.svg" alt="" className="size-7" />
      <span className="font-display text-xl font-semibold tracking-tight">K Data</span>
    </div>
  );
}

/** EnerMech brand mark on a white chip so its blue and green read on the dark sidebar. */
function CompanyLogo({ small = false }: { small?: boolean }) {
  return small ? (
    <span className="inline-flex shrink-0 items-center rounded bg-white px-1.5 py-0.5">
      <img src="/enermech-logo.png" alt="EnerMech" className="h-5 w-auto" />
    </span>
  ) : (
    <span className="mt-4 inline-block rounded-md bg-white px-3 py-1.5">
      <img src="/enermech-logo.png" alt="EnerMech" className="h-9 w-auto" />
    </span>
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
              <span className="flex items-center justify-between gap-2">
                {item.label}
                {!!item.badge && <span className="rounded-full bg-hold px-2 text-sm font-semibold text-white tabular-nums">{item.badge}</span>}
              </span>
            </NavLink>
          </li>
        ))}
      </ul>
    </div>
  );
}

function MobileTabs({ unitId, adminHome }: { unitId?: number; adminHome?: string }) {
  const tabs: NavItem[] = [
    ...(unitId ? [{ to: `/u/${unitId}`, label: "Unit", end: true }, { to: `/u/${unitId}/equipment`, label: "Register" }] : []),
    { to: "/units", label: "Units" },
    ...(adminHome ? [{ to: adminHome, label: "Admin" }] : []),
  ];
  return (
    <nav
      className="fixed inset-x-0 bottom-0 z-30 grid border-t border-line bg-panel pb-[env(safe-area-inset-bottom)] lg:hidden"
      style={{ gridTemplateColumns: `repeat(${tabs.length}, minmax(0, 1fr))` }}
      aria-label="Main"
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
