import { FullPageMessage } from "../../components/FullPageMessage";

export function NoAccessPage({ message }: { message: string }) {
  return <FullPageMessage title="Sin acceso a PresApp" detail={message} />;
}
