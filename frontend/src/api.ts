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
  audiobookshelfLibraryPath?: string;
  downloadRoot: string;
  qbitSavePath: string;
  importMode: string;
  pathMappings: { remote: string; local: string }[];
  indexerIds: number[];
  defaults: Preferences;
  searchIntervalMinutes: number;
  automationEnabled: boolean;
  manualSelection: boolean;
  automaticFailedDownloadRecovery: boolean;
  stallTimeoutMinutes: number;
};
export type Book = {
  id: string;
  title: string;
  author: string;
  cover: string;
  description?: string;
  publishedDate?: string;
  genre?: string;
  catalogUrl?: string;
  status: string;
  acquisitionMode: "prowlarr" | "quick-add";
  suspended: boolean;
  canRemove: boolean;
  progress: number;
  message: string;
  preferences: Preferences;
  selectedRelease?: { title: string };
  importedPath?: string;
  libraryMatches?: {id:string;title:string;author:string;sameAuthor:boolean}[];
  audiobookshelfItemId?: string;
  verifiedLibraryId?: string;
  failureReason?: string;
  failedReleases: { title: string; reason: string; at: string; blocked: boolean; infoHash?: string }[];
};
export type CatalogBook = {
  title: string;
  author: string;
  cover: string;
  catalogId: string;
  description?: string;
  releaseDate?: string;
  genre?: string;
  storeUrl?: string;
  selectionId?: string;
};
export type Release = {
  selectionId?: string;
  canSelect?: boolean;
  alreadyTracked?: boolean;
  release: { title: string; indexer: string; seeders: number; seedersKnown?: boolean; size: number };
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
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch("/api" + path, {
    method,
    headers: { "Content-Type": "application/json", "X-Grimarr": "1" },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
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
