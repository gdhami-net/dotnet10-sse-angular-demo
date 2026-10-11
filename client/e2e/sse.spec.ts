import { APIRequestContext, expect, Page, test } from '@playwright/test';

interface Connection {
  number: number;
  path: string;
  lastEventId: string | null;
  hadAuthorizationHeader: boolean;
  hadCookie: boolean;
  status: number | null;
  droppedByServer: boolean;
  aborted: boolean;
  completed: boolean;
}

// Every test gets its own job id, so repeated runs never share log entries.
let counter = 0;
const newJob = (name: string) => `${name}-${Date.now()}-${counter++}`;
const all = Array.from({ length: 10 }, (_, i) => String(i + 1)).join(',');

async function connections(request: APIRequestContext, job: string): Promise<Connection[]> {
  const list = (await (await request.get('/debug/connections')).json()) as Connection[];
  return list.filter((c) => c.path.includes(`/jobs/${job}/`));
}

const status = (page: Page) => page.getByTestId('status');
const ids = (page: Page) => page.getByTestId('ids');

test.use({ testIdAttribute: 'data-test' });

test('EventSource reconnects after a drop, sends Last-Event-ID, and the stream carries on', async ({ page, request }) => {
  const job = newJob('r');
  await page.goto(`/?mode=open&drop=4&job=${job}`);

  await expect(status(page)).toHaveText('done', { timeout: 15_000 });
  await expect(ids(page)).toHaveText(all);

  // Event 4 is sometimes lost in the cut; then the browser resumes from 3 and
  // gets 4 again. Either way the page saw each id exactly once (above).
  const seen = await connections(request, job);
  expect(seen.map((c) => c.droppedByServer)).toEqual([true, false]);
  expect(seen[0].lastEventId).toBeNull();
  expect(seen[1].lastEventId).toMatch(/^[34]$/);

  // The service closed the EventSource on the last event, so nothing reconnects.
  await page.waitForTimeout(2_500);
  expect(await connections(request, job)).toHaveLength(2);
});

test('an EventSource nobody closes reconnects after the response ends normally', async ({ page, request }) => {
  const job = newJob('unclosed');
  await page.goto('/index.html');
  await page.evaluate((url) => {
    (window as any).es = new EventSource(url);
  }, `/jobs/${job}/progress`);

  await expect.poll(async () => (await connections(request, job)).length, { timeout: 10_000 }).toBeGreaterThan(1);
  const seen = await connections(request, job);
  expect(seen[0].completed).toBe(true);
  expect(seen[1].lastEventId).toBe('10');
  await page.evaluate(() => (window as any).es.close());
});

test('against a bearer-protected endpoint, EventSource gets a 401 once and gives up', async ({ page, request }) => {
  const job = newJob('b');
  await page.goto(`/?mode=bearer&job=${job}`);

  await expect(status(page)).toHaveText('closed');

  await page.waitForTimeout(4_000); // four times the 1 s retry the server would ask for
  const seen = await connections(request, job);
  expect(seen).toHaveLength(1);
  expect(seen[0].status).toBe(401);
  expect(seen[0].hadAuthorizationHeader).toBe(false);
});

test('a fetch-based reader sends the bearer token and resumes with Last-Event-ID', async ({ page, request }) => {
  const job = newJob('f');
  await page.goto(`/?mode=fetch&drop=4&job=${job}`);

  await expect(status(page)).toHaveText('done', { timeout: 15_000 });
  // No gap and no duplicate, whatever id the reader resumed from.
  await expect(ids(page)).toHaveText(all);

  const seen = await connections(request, job);
  expect(seen.map((c) => [c.hadAuthorizationHeader, c.status])).toEqual([
    [true, 200],
    [true, 200],
  ]);
  expect(seen[1].lastEventId).toMatch(/^[34]$/);
});

test('a same-origin EventSource sends the auth cookie without withCredentials', async ({ page, request }) => {
  const job = newJob('c1');
  await page.goto(`/?mode=cookie&job=${job}`);

  await expect(status(page)).toHaveText('done', { timeout: 15_000 });
  const seen = await connections(request, job);
  expect([seen[0].hadCookie, seen[0].status]).toEqual([true, 200]);
});

test('cross-origin, the cookie only goes with withCredentials (and CORS allowing credentials)', async ({ page, request }) => {
  const without = newJob('c2');
  await page.goto(`/?mode=cookie&job=${without}&api=http://localhost:5101`);
  await expect(status(page)).toHaveText('closed');

  const withCreds = newJob('c3');
  await page.goto(`/?mode=cookie&job=${withCreds}&api=http://localhost:5101&creds=1`);
  await expect(status(page)).toHaveText('done', { timeout: 15_000 });

  const a = (await connections(request, without))[0];
  const b = (await connections(request, withCreds))[0];
  expect([a.hadCookie, a.status]).toEqual([false, 401]);
  expect([b.hadCookie, b.status]).toEqual([true, 200]);
});

test('closing the tab fires the request cancellation token on the server', async ({ page, request }) => {
  const job = newJob('t');
  await page.goto(`/?mode=open&job=${job}`);
  await expect(ids(page)).toHaveText(/^1,2/);

  await page.close();

  await expect.poll(async () => (await connections(request, job))[0].aborted, { timeout: 10_000 }).toBe(true);
  const [c] = await connections(request, job);
  expect(c.completed).toBe(false);
});
