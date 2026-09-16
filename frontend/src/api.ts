export type Preferences = {
  narration: string;
  abridgement: string;
  minimumSeeders: number;
  preferM4b: boolean;
};
export type Connection = {
  url: string;
  apiKey: string;
  username: string;
  password: string;
};
export type Settings = {
  prowlarr: Connection;
  qbittorrent: Connection;
  audiobookshelf: Connection;
  libraryId: string;
  libraryRoot: string;
  downloadRoot: string;
  qbitSavePath: string;
  importMode: string;
  pathMappings: { remote: string; local: string }[];
  indexerIds: number[];
  defaults: Preferences;
  searchIntervalMinutes: number;
  automationEnabled: boolean;
};
export type Book = {
  id: string;
  title: string;
  author: string;
  cover: string;
  status: string;
  suspended: boolean;
  progress: number;
  message: string;
  preferences: Preferences;
  selectedRelease?: { title: string };
  importedPath?: string;
};
export type CatalogBook = {
  title: string;
  author: string;
  cover: string;
  catalogId: string;
};
export type Release = {
  release: { title: string; indexer: string; seeders: number; size: number };
  score: number;
  eligible: boolean;
  reasons: string[];
};
export type Activity = {
  id: number;
  at: string;
  title: string;
  message: string;
};
export async function api<T = unknown>(
  path: string,
  method = "GET",
  body?: unknown,
): Promise<T> {
  const response = await fetch("/api" + path, {
    method,
    headers: { "Content-Type": "application/json", "X-Grimarr": "1" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401 && path != "/login")
    window.dispatchEvent(new Event("grimarr-session-expired"));
  if (!response.ok) {
    const result = await response.json().catch(() => null);
    throw new Error(
      result?.error ||
        (response.status === 401
          ? "Incorrect password."
          : response.status === 429
            ? "Too many attempts. Try again in a minute."
            : `Request failed (${response.status}).`),
    );
  }
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}
