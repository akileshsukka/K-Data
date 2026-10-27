import { Outlet } from "react-router";
import { useSession } from "./session";
import { PageHeader } from "../components/PageHeader";

/** Hides a section from users without the permission. The API enforces the same rule on its own. */
export function RequirePermission({ permission }: { permission: string }) {
  const session = useSession();
  if (!session.can(permission)) {
    return <PageHeader title="No permission" description="Your role does not include this section. Ask an administrator if you need it." />;
  }
  return <Outlet />;
}
