import { useId, useRef, useState, type KeyboardEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import type { SearchHit } from "../api/types";
import { useDebounced } from "./useDebounced";

/**
 * Type-ahead asset search: starts at 2 characters, waits 300 ms after the last key, no Enter needed.
 * Matches any part of the tag, Equipment ID or description.
 */
export function AssetSearch({
  onPick,
  unitId,
  placeholder = "Find by tag, ID or description",
  dark = false,
  excludeIds,
}: {
  onPick: (hit: SearchHit) => void;
  unitId?: number;
  placeholder?: string;
  dark?: boolean;
  excludeIds?: Set<string>;
}) {
  const [text, setText] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const term = useDebounced(text.trim(), 300);
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);

  const query = useQuery({
    queryKey: ["search", term, unitId],
    queryFn: ({ signal }) =>
      api<SearchHit[]>(`/api/assets/search?q=${encodeURIComponent(term)}${unitId ? `&unitId=${unitId}` : ""}&take=12`, { signal }),
    enabled: term.length >= 2,
    staleTime: 30_000,
  });
  const hits = (query.data ?? []).filter((h) => !excludeIds?.has(h.id));
  const showList = open && term.length >= 2;

  function pick(hit: SearchHit) {
    onPick(hit);
    setText("");
    setOpen(false);
    inputRef.current?.focus();
  }

  function onKey(e: KeyboardEvent<HTMLInputElement>) {
    if (!showList || hits.length === 0) return;
    if (e.key === "ArrowDown") { e.preventDefault(); setActive((a) => Math.min(a + 1, hits.length - 1)); }
    if (e.key === "ArrowUp") { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
    if (e.key === "Enter") { e.preventDefault(); pick(hits[active]); }
    if (e.key === "Escape") setOpen(false);
  }

  return (
    <div className="relative">
      <input
        ref={inputRef}
        type="search"
        role="combobox"
        aria-expanded={showList}
        aria-controls={listId}
        aria-autocomplete="list"
        placeholder={placeholder}
        value={text}
        onChange={(e) => { setText(e.target.value); setOpen(true); setActive(0); }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        onKeyDown={onKey}
        className={`block min-h-10 w-full rounded-md border px-3 text-[15px] focus:outline-none ${
          dark ? "border-white/15 bg-ink-2 text-white placeholder:text-white/50 focus:border-lilac" : "border-line bg-panel placeholder:text-muted/70 focus:border-violet"
        }`}
      />
      {showList && (
        <ul id={listId} role="listbox" className="absolute z-40 mt-1 max-h-80 w-full min-w-72 overflow-y-auto rounded-md border border-line bg-panel py-1 text-text shadow-lg">
          {query.isFetching && hits.length === 0 && <li className="px-3 py-2 text-muted">Searching…</li>}
          {!query.isFetching && hits.length === 0 && <li className="px-3 py-2 text-muted">Nothing matches “{term}”.</li>}
          {hits.map((h, i) => (
            <li
              key={h.id}
              role="option"
              aria-selected={i === active}
              onMouseDown={(e) => { e.preventDefault(); pick(h); }}
              onMouseEnter={() => setActive(i)}
              className={`cursor-pointer px-3 py-2 ${i === active ? "bg-violet-soft" : ""}`}
            >
              <span className="font-semibold text-ink">{h.tag ?? h.code}</span>
              <span className="ml-2 text-sm text-muted">{h.code}{!unitId && ` · ${h.unit}`}{!h.isActive && " · inactive"}</span>
              {h.description && <span className="block truncate text-sm text-muted">{h.description}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
