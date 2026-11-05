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

/** What could be read from a line number, standard or not. Same rules as LineNumber.Read on the server (live preview only). */
export interface LineReading {
  unit: number | null;
  sequence: string | null;
  service: string | null;
  sizeIn: number | null;
  spec: string | null;
  standard: boolean;
  /** Why the number is not standard (shown when the format check is on). */
  problem: string | null;
  missing: string[];
  ignored: string[];
}

export interface LineCatalogue {
  isSpec: (code: string) => boolean;
  isInsulation: (code: string) => boolean;
  isService: (code: string) => boolean;
}

const sizeAnchor = /^(.*?)-\s*(\d+(?:[.]\d+)?(?:[- ]\d+\/\d+)?|\d+\/\d+)\s*"(.*)$/;
const tokens = (text: string) => text.split("-").map((t) => t.trim()).filter(Boolean);

export function readLineNumber(text: string, catalogue: LineCatalogue): LineReading {
  const clean = (text.split("\n").map((l) => l.trim()).find(Boolean) ?? "").replace(/[“”″]/g, '"');
  const strict = parseLineNumber(clean);
  if (strict && !catalogue.isInsulation(strict.spec))
    return { unit: strict.unit, sequence: strict.sequence.toUpperCase(), service: strict.service, sizeIn: strict.sizeIn, spec: strict.spec, standard: true, problem: null, missing: [], ignored: [] };

  let head: string[];
  let tail: string[];
  let sizeIn: number | null = null;
  const m = sizeAnchor.exec(clean);
  const size = m ? parseSize(m[2].trim()) : null;
  if (m && size !== null && size > 0) {
    sizeIn = size;
    head = tokens(m[1]);
    tail = tokens(m[3]);
  } else {
    const all = tokens(clean);
    head = all.slice(0, 3);
    tail = all.slice(3);
  }
  const code = /^[0-9A-Za-z]+$/;
  const unit = head.length > 0 && /^\d{1,4}$/.test(head[0]) ? Number(head[0]) : null;
  const sequence = head.length > 1 && code.test(head[1]) ? head[1].toUpperCase() : null;
  const service = head.length > 2 && code.test(head[2]) && (head.length === 3 || catalogue.isService(head[2].toUpperCase())) ? head[2].toUpperCase() : null;
  const ignored = head.slice(service === null ? 2 : 3);
  if (unit === null && head.length > 0) ignored.unshift(head[0]);
  let spec: string | null = null;
  for (const part of tail) {
    const c = part.toUpperCase();
    if (spec === null && catalogue.isSpec(c)) spec = c;
    else if (!catalogue.isInsulation(c)) ignored.push(part);
  }
  const missing = [unit === null && "unit", sequence === null && "sequence", service === null && "service", sizeIn === null && "size", spec === null && "pipe spec"].filter((x): x is string => !!x);
  const problem = strict
    ? `After the size comes ${strict.spec}, an insulation code: this line number has no pipe spec.`
    : 'Not a line number like 39-0218-LS-16"-61055-P (size and spec cannot be read).';
  return { unit, sequence, service, sizeIn, spec, standard: false, problem, missing, ignored };
}

/** "Not read: pipe spec · Ignored: ST(1x1/2)", as stored on the line. */
export function readingNote(r: LineReading): string | null {
  if (r.standard) return null;
  const parts = [r.missing.length ? `Not read: ${r.missing.join(", ")}` : null, r.ignored.length ? `Ignored: ${r.ignored.join(", ")}` : null].filter(Boolean);
  return parts.length ? parts.join(" · ") : "Not in the standard format";
}

export const formatSize =(inches: number | null | undefined) => (inches == null ? "" : `${Number(inches.toFixed(3))}"`);
