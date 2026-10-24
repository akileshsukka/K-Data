import { NavLink } from "react-router";

const tabs = [
  { to: "/admin/usuarios", label: "Usuarios" },
  { to: "/admin/roles", label: "Roles y permisos" },
  { to: "/admin/ingresos", label: "Ingresos" },
];

/** Section switcher for phones, where the sidebar is hidden. */
export function AdminTabs() {
  return (
    <nav className="-mx-4 mb-6 flex gap-1 overflow-x-auto px-4 lg:hidden" aria-label="Administración">
      {tabs.map((t) => (
        <NavLink
          key={t.to}
          to={t.to}
          className={({ isActive }) =>
            `shrink-0 rounded-md px-3 py-1.5 font-semibold ${isActive ? "bg-violet text-white" : "bg-panel text-ink border border-line"}`
          }
        >
          {t.label}
        </NavLink>
      ))}
    </nav>
  );
}
