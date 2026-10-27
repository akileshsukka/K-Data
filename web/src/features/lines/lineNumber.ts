/** Same reading as LineNumber.Parse on the server: unit-number-service-size"-spec (a trailing insulation code is accepted, not kept). Live preview only. */
export interface LineParts {
  unit: number;
  sequence: string;
  service: string;
  sizeIn: number;
  sizeText: string;
  spec: string;
}

const pattern = /^(\d{1,4})-([0-9A-Za-z]+)-([0-9A-Za-z]+)-(\d+(?:[.]\d+)?(?:[- ]\d+\/\d+)?|\d+\/\d+)\s*"\s*-([0-9A-Za-z]+)(?:-([0-9A-Za-z]*(?:\/[0-9A-Za-z]+)?))?$/;

export function parseLineNumber(text: string): LineParts | null {
  const m = pattern.exec(text.trim().replace(/[“”″]/g, '"'));
  if (!m) return null;
  const size = parseSize(m[4]);
  if (size === null) return null;
  return { unit: Number(m[1]), sequence: m[2], service: m[3].toUpperCase(), sizeIn: size, sizeText: m[4], spec: m[5].toUpperCase() };
}

function parseSize(text: string): number | null {
  const frac = (f: string) => { const [a, b] = f.split("/").map(Number); return b ? a / b : null; };
  const mixed = /^(\d+)[- ](\d+\/\d+)$/.exec(text);
  if (mixed) { const f = frac(mixed[2]); return f === null ? null : Number(mixed[1]) + f; }
  if (text.includes("/")) return frac(text);
  const n = Number(text);
  return Number.isFinite(n) ? n : null;
}

export const formatSize = (inches: number | null | undefined) => (inches == null ? "" : `${Number(inches.toFixed(3))}"`);
