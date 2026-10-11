import { Injectable, Signal, signal } from '@angular/core';

export interface JobState {
  percent: number;
  lastEventId: string;
  status: 'connecting' | 'open' | 'reconnecting' | 'done' | 'closed';
  ids: string[];
}

export interface JobWatch {
  readonly state: Signal<JobState>;
  close(): void;
}

const initial: JobState = { percent: 0, lastEventId: '', status: 'connecting', ids: [] };

@Injectable({ providedIn: 'root' })
export class JobProgress {
  watch(url: string, init?: EventSourceInit): JobWatch {
    const state = signal<JobState>(initial);
    const source = new EventSource(url, init);

    // The server names its events "progress", so onmessage would never fire.
    source.addEventListener('progress', (e: MessageEvent<string>) => {
      const { percent } = JSON.parse(e.data) as { percent: number };
      state.update((s) => ({
        percent,
        lastEventId: e.lastEventId,
        status: percent === 100 ? 'done' : 'open',
        ids: [...s.ids, e.lastEventId],
      }));
      // When the response ends, EventSource reconnects. Stop it once the job is done.
      if (percent === 100) source.close();
    });

    // CONNECTING means the browser is retrying; CLOSED means it gave up.
    source.onerror = () =>
      state.update((s) => ({
        ...s,
        status: source.readyState === EventSource.CLOSED ? 'closed' : 'reconnecting',
      }));

    return { state: state.asReadonly(), close: () => source.close() };
  }
}
