/** A line number code with its meaning underneath; an unknown code says so instead of guessing. */
export function CodeMeaning({ code, meaning, empty = "" }: { code: string | null | undefined; meaning: string | undefined; empty?: string }) {
  if (!code) return <span className="text-muted">{empty}</span>;
  return (
    <span className="block min-w-0">
      <span className="font-semibold text-ink">{code}</span>
      <span className={`block text-sm leading-snug ${meaning ? "text-muted" : "text-ongoing"}`}>{meaning ?? "Not in the line codes"}</span>
    </span>
  );
}
