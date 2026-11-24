import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";

/**
 * Rows drawn on demand (24-nov: E&I registers with 2,000+ instruments froze the page when every row was drawn).
 * Draws the first <paramref name="page"/> items and adds another page when the end of the list scrolls into view;
 * back to the first page whenever the list itself changes (a filter, another register). Filters, counts and
 * "select all" still work on the whole list: only the drawing is limited.
 */
export function useProgressiveList<T>(items: T[], page = 150) {
  // The pages drawn belong to one list: a new list (new filter) starts again from the first page. Derived while
  // drawing, never set during the render (an empty list made anew on every render would otherwise loop).
  const [drawn, setDrawn] = useState<{ items: T[]; limit: number }>({ items, limit: page });
  const limit = drawn.items === items ? drawn.limit : page;
  const more = useCallback(() => setDrawn({ items, limit: limit + page }), [items, limit, page]);
  const hasMore = limit < items.length;

  // The sentinel after the last drawn row: when it comes near the screen, draw the next page.
  const observer = useRef<IntersectionObserver | null>(null);
  const sentinel = useCallback(
    (node: HTMLElement | null) => {
      observer.current?.disconnect();
      if (!node || !hasMore) return;
      observer.current = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && more(), { rootMargin: "600px 0px" });
      observer.current.observe(node);
    },
    [hasMore, more],
  );
  useEffect(() => () => observer.current?.disconnect(), []);

  return { visible: hasMore ? items.slice(0, limit) : items, hasMore, more, sentinel, total: items.length };
}

/** True while the media query matches (e.g. "(min-width: 48rem)"), following window resizes. */
export function useMediaQuery(query: string) {
  return useSyncExternalStore(
    (onChange) => {
      const m = window.matchMedia(query);
      m.addEventListener("change", onChange);
      return () => m.removeEventListener("change", onChange);
    },
    () => window.matchMedia(query).matches,
    () => true,
  );
}
