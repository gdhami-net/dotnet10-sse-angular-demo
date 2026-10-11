import { Injectable, signal } from '@angular/core';
import { JobState, JobWatch } from './job-progress';

// What a bearer token costs you: fetch can send the Authorization header, but
// the parsing, the reconnect and Last-Event-ID are now yours to write.
@Injectable({ providedIn: 'root' })
export class FetchJobProgress {
  watch(url: string, token: () => Promise<string>): JobWatch {
    const state = signal<JobState>({ percent: 0, lastEventId: '', status: 'connecting', ids: [] });
    const abort = new AbortController();

    const run = async () => {
      let retryMs = 3000;
      while (!abort.signal.aborted && state().status !== 'done') {
        try {
          const headers: Record<string, string> = { Authorization: `Bearer ${await token()}` };
          if (state().lastEventId) headers['Last-Event-ID'] = state().lastEventId;
          const response = await fetch(url, { headers, signal: abort.signal });
          if (!response.ok) {
            state.update((s) => ({ ...s, status: 'closed' }));
            return;
          }
          state.update((s) => ({ ...s, status: 'open' }));

          let buffer = '';
          const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader();
          for (let r = await reader.read(); !r.done; r = await reader.read()) {
            buffer += r.value;
            let end: number;
            while ((end = buffer.indexOf('\n\n')) >= 0) {
              const fields = parse(buffer.slice(0, end));
              buffer = buffer.slice(end + 2);
              if (fields['retry']) retryMs = Number(fields['retry']);
              if (fields['event'] !== 'progress') continue;
              const { percent } = JSON.parse(fields['data']) as { percent: number };
              state.update((s) => ({
                percent,
                lastEventId: fields['id'] ?? s.lastEventId,
                status: percent === 100 ? 'done' : 'open',
                ids: [...s.ids, fields['id'] ?? ''],
              }));
            }
          }
        } catch {
          if (abort.signal.aborted) return;
        }
        if (state().status === 'done') return;
        state.update((s) => ({ ...s, status: 'reconnecting' }));
        await new Promise((resolve) => setTimeout(resolve, retryMs));
      }
    };

    run();
    return { state: state.asReadonly(), close: () => abort.abort() };
  }
}

// One event block; one-line data fields only, which is all this server sends.
function parse(block: string): Record<string, string> {
  const fields: Record<string, string> = {};
  for (const line of block.split('\n')) {
    const colon = line.indexOf(':');
    if (colon <= 0) continue;
    const value = line.slice(colon + 1);
    fields[line.slice(0, colon)] = value.startsWith(' ') ? value.slice(1) : value;
  }
  return fields;
}
