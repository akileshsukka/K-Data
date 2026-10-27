const dateTime = new Intl.DateTimeFormat("en-GB", {
  timeZone: "Asia/Dubai",
  day: "2-digit",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

const dateOnly = new Intl.DateTimeFormat("en-GB", { day: "2-digit", month: "short", year: "numeric", timeZone: "UTC" });

function asUtc(value: string): Date {
  return new Date(value.endsWith("Z") || value.includes("+") ? value : `${value}Z`);
}

/** UTC timestamp from the API shown in Dubai time, e.g. "24 Oct 2026, 15:31". */
export function formatDateTime(utc: string | null | undefined): string {
  return utc ? dateTime.format(asUtc(utc)) : "—";
}

/** A calendar date "2026-10-24" shown as "24 Oct 2026". */
export function formatDate(day: string | null | undefined): string {
  return day ? dateOnly.format(new Date(`${day.slice(0, 10)}T00:00:00Z`)) : "—";
}

/** Today in Dubai as yyyy-MM-dd. */
export function todayDubai(): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Dubai" }).format(new Date());
}

/** Now in Dubai formatted for <input type="datetime-local"> (yyyy-MM-ddTHH:mm). */
export function nowDubaiLocal(): string {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Asia/Dubai", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  }).formatToParts(new Date());
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? "00";
  return `${get("year")}-${get("month")}-${get("day")}T${get("hour")}:${get("minute")}`;
}

/** A Dubai local "yyyy-MM-ddTHH:mm" turned into a UTC ISO string (Dubai is UTC+4, no daylight saving). */
export function dubaiLocalToUtc(local: string): string {
  return new Date(`${local}:00+04:00`).toISOString();
}
