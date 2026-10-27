import { FullPageMessage } from "../../components/FullPageMessage";

export function NoAccessPage({ message }: { message: string }) {
  return <FullPageMessage title="No access to K Data" detail={message} />;
}
