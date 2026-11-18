import { Signal, computed, signal } from '@angular/core';
import { ApiError } from './api';

export type LoadStatus = 'idle' | 'loading' | 'ready' | 'empty' | 'error';

export interface Loadable<T> {
  readonly status: Signal<LoadStatus>;
  readonly value: Signal<T | undefined>;
  readonly error: Signal<ApiError | undefined>;
  /** Runs the loader again (retry button, filter change). Older responses that arrive late are ignored. */
  load(): Promise<void>;
}

/**
 * Wraps an async loader in signals so every page renders the same four states: loading, ready,
 * empty (the loader succeeded with nothing to show) and error (with a retry).
 */
export function loadable<T>(loader: () => Promise<T>, isEmpty: (value: T) => boolean = () => false): Loadable<T> {
  const status = signal<LoadStatus>('idle');
  const value = signal<T | undefined>(undefined);
  const error = signal<ApiError | undefined>(undefined);
  let generation = 0;

  return {
    status: status.asReadonly(),
    value: value.asReadonly(),
    error: error.asReadonly(),
    async load() {
      const mine = ++generation;
      status.set('loading');
      error.set(undefined);
      try {
        const result = await loader();
        if (mine !== generation) {
          return;
        }
        value.set(result);
        status.set(isEmpty(result) ? 'empty' : 'ready');
      } catch (e) {
        if (mine !== generation) {
          return;
        }
        error.set(ApiError.from(e));
        status.set('error');
      }
    },
  };
}

/** Convenience for paged lists: empty when there are no items. */
export function isEmptyPage(page: { items: unknown[] }): boolean {
  return page.items.length === 0;
}

export function statusIs(l: Loadable<unknown>, ...statuses: LoadStatus[]): Signal<boolean> {
  return computed(() => statuses.includes(l.status()));
}
