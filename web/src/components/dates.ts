const formatter = new Intl.DateTimeFormat("es", {
  timeZone: "Asia/Dubai",
  day: "2-digit",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

/** UTC timestamp from the API shown in Dubai time, e.g. "24 oct 2026, 15:31". */
export function formatDateTime(utc: string | null | undefined): string {
  if (!utc) return "—";
  const iso = utc.endsWith("Z") || utc.includes("+") ? utc : `${utc}Z`;
  return formatter.format(new Date(iso));
}
