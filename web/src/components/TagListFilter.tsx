import { useState } from "react";
import { Button } from "./Button";

/**
 * A tag as compared with a pasted list: case, repeated spaces and the kind of dash (– — ‐ −) do not matter,
 * so "541–E-116  b1/b2" pasted from a report finds 541-E-116 B1/B2.
 */
export const tagKey = (tag: string) => tag.trim().replace(/[‐-―−]/g, "-").replace(/\s+/g, " ").toLowerCase();

/** The pasted list as kept in the URL: tag keys, in the pasted order, comma separated. */
export const tagList = (value: string) => (value ? value.split(",").filter(Boolean) : []);

/**
 * Filter by a list of tags pasted from Excel (one per line, or separated by commas / semicolons / tabs), like the bulk edit.
 * Pages match it against the tag or the Equipment ID with {@link tagKey}.
 */
export function TagListFilter({ value, onChange }: { value: string; onChange: (tags: string) => void }) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const count = tagList(value).length;
  function apply() {
    const tags = [...new Set(text.split(/[\n\r,;\t]+/).map(tagKey).filter(Boolean))];
    onChange(tags.join(","));
    setOpen(false);
    setText("");
  }
  if (!open) {
    return (
      <Button variant="secondary" onClick={() => setOpen(true)} className="min-h-10">
        {count > 0 ? `Pasted tags (${count})` : "Paste tags"}
      </Button>
    );
  }
  return (
    <div className="w-full rounded-lg border border-line bg-panel p-3">
      <label className="block">
        <span className="text-sm font-semibold text-ink">Tags to show, one per line (paste a column from Excel)</span>
        <textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} autoFocus placeholder={"39-EM-102-01A\n39-EM-102-01B"}
          className="mt-1 block w-full rounded-md border border-line bg-panel px-3 py-2 text-[15px] focus:border-violet focus:outline-none" />
      </label>
      <div className="mt-2 flex flex-wrap gap-2">
        <Button disabled={!text.trim()} onClick={apply}>Filter by these tags</Button>
        {count > 0 && <Button variant="secondary" onClick={() => { onChange(""); setOpen(false); }}>Remove tag filter</Button>}
        <Button variant="quiet" onClick={() => setOpen(false)}>Cancel</Button>
      </div>
    </div>
  );
}
