import { Button } from "./Button";

/** Whole-screen state used before the app shell exists (loading, no access, errors). */
export function FullPageMessage({
  title,
  detail,
  action,
}: {
  title: string;
  detail?: string;
  action?: { label: string; onClick: () => void };
}) {
  return (
    <main className="grid min-h-dvh place-items-center bg-ink px-6 text-white">
      <div className="max-w-md">
        <p className="font-display text-lg font-semibold text-lilac">PresApp</p>
        <h1 className="mt-2 font-display text-3xl font-semibold text-white">{title}</h1>
        {detail && <p className="mt-3 text-white/80">{detail}</p>}
        {action && (
          <Button className="mt-6" onClick={action.onClick}>
            {action.label}
          </Button>
        )}
      </div>
    </main>
  );
}
