import { Outlet } from "react-router";
import { useSession } from "./session";
import { PageHeader } from "../components/PageHeader";

/** Hides a section from users without the permission. The API enforces the same rule on its own. */
export function RequirePermission({ permission }: { permission: string }) {
  const session = useSession();
  if (!session.can(permission)) {
    return <PageHeader title="Sin permiso" description="Tu rol no incluye esta sección. Si la necesitas, pídela a un administrador." />;
  }
  return <Outlet />;
}
