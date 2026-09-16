import {
  useEffect,
  useState,
  useId,
  cloneElement,
  isValidElement,
  type FormEvent,
  type ReactNode,
} from "react";
import { createRoot } from "react-dom/client";
import {
  BookOpen,
  Library,
  ListMusic,
  Activity as ActivityIcon,
  Settings as SettingsIcon,
  Plus,
  Search,
  ArrowUpRight,
  Headphones,
  X,
  Check,
  Radio,
  Pause,
  RotateCw,
  ChevronRight,
  LogOut,
  SlidersHorizontal,
  AlertCircle,
  LoaderCircle,
  LayoutGrid,
  List,
} from "lucide-react";
import {
  api,
  type Book,
  type Settings,
  type Preferences,
  type CatalogBook,
  type Release,
  type Activity,
} from "./api";
import "./style.css";
import PageHeader from "./components/arr/PageHeader";
import PageSidebarItem from "./components/arr/PageSidebarItem";
import PageToolbar from "./components/arr/PageToolbar";
import PageToolbarButton from "./components/arr/PageToolbarButton";
import LoadingIndicator from "./components/arr/LoadingIndicator";

type Page = "library" | "queue" | "activity" | "settings";
function App() {
  const [session, setSession] = useState<boolean | null>(null),
    [page, setPage] = useState<Page>("library"),
    [books, setBooks] = useState<Book[]>([]),
    [settings, setSettings] = useState<Settings | null>(null),
    [error, setError] = useState(""),
    [add, setAdd] = useState(false),
    [selected, setSelected] = useState<Book | null>(null),
    [query, setQuery] = useState("");
  const [view, setView] = useState<"grid" | "list">(() =>
    localStorage.getItem("grimarr-view") === "list" ? "list" : "grid",
  );
  const [sort, setSort] = useState("title"),
    [filter, setFilter] = useState("all"),
    [refreshing, setRefreshing] = useState(false);
  const refresh = async () => {
    const [b, s] = await Promise.all([
      api<Book[]>("/books"),
      api<{ settings: Settings }>("/settings"),
    ]);
    setBooks(b);
    setSettings(s.settings);
  };
  useEffect(() => {
    api("/session")
      .then(() => setSession(true))
      .catch(() => setSession(false));
    const expired = () => setSession(false);
    window.addEventListener("grimarr-session-expired", expired);
    return () => window.removeEventListener("grimarr-session-expired", expired);
  }, []);
  useEffect(() => {
    if (!session) return;
    const poll = () => refresh().catch((e) => setError(e.message));
    poll();
    const timer = setInterval(poll, 10000);
    return () => clearInterval(timer);
  }, [session]);
  useEffect(() => {
    localStorage.setItem("grimarr-view", view);
  }, [view]);
  if (session === null)
    return (
      <div className="loading" role="status" aria-label="Loading Grimarr">
        <LoadingIndicator />
      </div>
    );
  if (!session) return <Login onLogin={() => setSession(true)} />;
  const downloading = books.filter((b) =>
    ["queued", "downloading", "scan-pending"].includes(b.status),
  );
  const visible = books
    .filter(
      (b) =>
        (page !== "queue" || b.status !== "available") &&
        (b.title + " " + b.author)
          .toLowerCase()
          .includes(query.toLowerCase()) &&
        (filter === "all" ||
          (filter === "monitored"
            ? !b.suspended
            : filter === "available"
              ? b.status === "available"
              : ["queued", "downloading", "scan-pending"].includes(b.status))),
    )
    .sort((a, b) =>
      sort === "author"
        ? a.author.localeCompare(b.author) || a.title.localeCompare(b.title)
        : sort === "status"
          ? a.status.localeCompare(b.status) || a.title.localeCompare(b.title)
          : a.title.localeCompare(b.title),
    );
  return (
    <div className="app">
      <PageHeader
        query={query}
        automationEnabled={!!settings?.automationEnabled}
        onQueryChange={setQuery}
        onHome={() => setPage("library")}
        onSignOut={() => {
          api("/logout", "POST")
            .then(() => setSession(false))
            .catch((e) => setError(e.message));
        }}
      />
      <aside className="sidebar">
        <nav>
          {(
            [
              ["library", Library, "Library"],
              ["queue", ListMusic, "Download queue"],
              ["activity", ActivityIcon, "Activity"],
              ["settings", SettingsIcon, "Settings"],
            ] as const
          ).map(([key, Icon, label]) => (
            <PageSidebarItem
              key={key}
              title={label}
              iconName={Icon}
              to={"#" + key}
              isActive={page === key}
              isActiveParent={page === key}
              onPress={() => {
                setPage(key);
                setFilter("all");
              }}
              statusComponent={
                key === "queue" && downloading.length > 0
                  ? () => (
                      <span className="nav-count">{downloading.length}</span>
                    )
                  : undefined
              }
            >
              {key === "library" && page === "library" ? (
                <PageSidebarItem
                  title="Add New"
                  to="#add"
                  onPress={() => setAdd(true)}
                />
              ) : null}
              {key === "settings" && page === "settings"
                ? [
                    ["connections", "Connections"],
                    ["storage", "Media Management"],
                    ["profiles", "Profiles"],
                  ].map(([id, title]) => (
                    <PageSidebarItem
                      key={id}
                      title={title}
                      to={"#" + id}
                      onPress={() =>
                        document
                          .getElementById(id)
                          ?.scrollIntoView({
                            behavior: "smooth",
                            block: "start",
                          })
                      }
                    />
                  ))
                : null}
            </PageSidebarItem>
          ))}
        </nav>
        <div className="sidebar-bottom">
          Grimarr 0.1 <span>Alpha</span>
        </div>
      </aside>
      <main>
        <PageToolbar>
          {page === "library" || page === "queue" ? (
            <>
              <PageToolbarButton
                label="Refresh"
                iconName={RotateCw}
                isSpinning={refreshing}
                onPress={async () => {
                  setRefreshing(true);
                  try {
                    await refresh();
                  } catch (e) {
                    setError((e as Error).message);
                  } finally {
                    setRefreshing(false);
                  }
                }}
              />
              <PageToolbarButton
                label="Add New"
                aria-label="Add audiobook"
                iconName={Plus}
                onPress={() => setAdd(true)}
              />
              <div className="toolbar-spacer" />
              <div
                className="view-buttons"
                role="group"
                aria-label="Library view"
              >
                <PageToolbarButton
                  label="Posters"
                  aria-label="Poster view"
                  aria-pressed={view === "grid"}
                  iconName={LayoutGrid}
                  onPress={() => setView("grid")}
                />
                <PageToolbarButton
                  label="List"
                  aria-label="List view"
                  aria-pressed={view === "list"}
                  iconName={List}
                  onPress={() => setView("list")}
                />
              </div>
              <label className="toolbar-select">
                <span>Sort</span>
                <select
                  aria-label="Sort audiobooks"
                  value={sort}
                  onChange={(e) => setSort(e.target.value)}
                >
                  <option value="title">Title</option>
                  <option value="author">Author</option>
                  <option value="status">Status</option>
                </select>
              </label>
              <label className="toolbar-select">
                <span>Filter</span>
                <select
                  aria-label="Filter audiobooks"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                >
                  <option value="all">All</option>
                  <option value="monitored">Monitored</option>
                  <option value="available">Available</option>
                  <option value="downloading">Downloading</option>
                </select>
              </label>
            </>
          ) : (
            <div className="toolbar-title">
              {page === "settings" ? (
                <SettingsIcon size={22} />
              ) : (
                <ActivityIcon size={22} />
              )}{" "}
              {page === "settings" ? "Settings" : "Activity"}
            </div>
          )}
        </PageToolbar>
        <div className="content">
          {error && <Notice text={error} close={() => setError("")} />}
          {page === "settings" ? (
            <SettingsPage onSaved={refresh} />
          ) : page === "activity" ? (
            <ActivityPage />
          ) : (
            <>
              {!settings?.automationEnabled && (
                <div className="setup-banner">
                  <AlertCircle size={17} />
                  <span>
                    Automation is paused. Configure your connections and enable
                    automation in Settings.
                  </span>
                  <button
                    className="text-button"
                    onClick={() => setPage("settings")}
                  >
                    Settings
                  </button>
                </div>
              )}
              <div className="section-toolbar">
                <h1>{page === "queue" ? "Download queue" : "Audiobooks"}</h1>
                <span>
                  {visible.length} of {books.length} audiobooks
                </span>
              </div>
              {visible.length === 0 ? (
                <div className="empty">
                  <BookOpen size={38} strokeWidth={1.5} />
                  <h2>
                    {query || filter !== "all"
                      ? "No matching audiobooks"
                      : page === "queue"
                        ? "No queued audiobooks"
                        : "No audiobooks added"}
                  </h2>
                  <p>
                    {query || filter !== "all"
                      ? "Change the search or filter to see more audiobooks."
                      : "Use Add New to search for an audiobook or enter a title and author."}
                  </p>
                  <button className="primary" onClick={() => setAdd(true)}>
                    <Plus size={16} />
                    Add audiobook
                  </button>
                </div>
              ) : (
                <div className={view === "list" ? "book-list" : "book-grid"}>
                  {visible.map((book) => (
                    <button
                      className={"book-card " + book.status}
                      key={book.id}
                      onClick={() => setSelected(book)}
                    >
                      <Cover book={book} />
                      <div className="book-info">
                        <h3>{book.title}</h3>
                        <p className="book-author">{book.author}</p>
                        <span className={"badge " + book.status}>
                          {book.suspended
                            ? "Monitoring paused"
                            : statusLabel(book.status)}
                        </span>
                        <p className="list-message">{book.message}</p>
                        <div className="list-preferences">
                          <span>English</span>
                          <span>
                            {book.preferences.narration === "either"
                              ? "Any narration"
                              : book.preferences.narration === "standard"
                                ? "Standard narration"
                                : "Dramatized / full cast"}
                          </span>
                          <span>
                            {book.preferences.abridgement === "either"
                              ? "Any length"
                              : book.preferences.abridgement}
                          </span>
                        </div>
                        {book.status === "downloading" && (
                          <div className="progress">
                            <span
                              style={{ width: book.progress * 100 + "%" }}
                            />
                          </div>
                        )}
                      </div>
                    </button>
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      </main>
      {add && settings && (
        <AddModal
          defaults={settings.defaults}
          close={() => setAdd(false)}
          added={async () => {
            setAdd(false);
            await refresh();
          }}
        />
      )}
      {selected && settings && (
        <BookModal
          book={books.find((b) => b.id === selected.id) || selected}
          settings={settings}
          close={() => setSelected(null)}
          changed={refresh}
        />
      )}
    </div>
  );
}
function statusLabel(status: string) {
  return (
    (
      {
        wanted: "Monitored",
        queued: "Queued",
        downloading: "Downloading",
        "scan-pending": "Import complete",
        available: "On your shelf",
      } as Record<string, string>
    )[status] || status
  );
}
function Cover({
  book,
}: {
  book: { title: string; author: string; cover: string };
}) {
  return (
    <div className="cover">
      {book.cover ? (
        <img
          src={book.cover}
          alt=""
          loading="lazy"
          onError={(e) => {
            e.currentTarget.style.display = "none";
          }}
        />
      ) : null}
      <div className="cover-fallback">
        <BookOpen size={32} />
        <strong>{book.title}</strong>
        <small>{book.author}</small>
      </div>
    </div>
  );
}
function Notice({ text, close }: { text: string; close?: () => void }) {
  return (
    <div className="notice" role="alert">
      <AlertCircle size={18} />
      <span>{text}</span>
      {close && (
        <button
          className="icon-button"
          aria-label="Dismiss message"
          onClick={close}
        >
          <X size={17} />
        </button>
      )}
    </div>
  );
}
function Login({ onLogin }: { onLogin: () => void }) {
  const [password, setPassword] = useState(""),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  return (
    <div className="login">
      <div className="login-art">
        <BookOpen size={48} />
        <h1>
          Your stories,
          <br />
          within reach.
        </h1>
        <p>A personal home for the books you can’t wait to hear.</p>
      </div>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          try {
            await api("/login", "POST", { password });
            onLogin();
          } catch (e) {
            setError((e as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <div className="brand">
          <BookOpen />
          grimarr.
        </div>
        <h2>Sign in to Grimarr</h2>
        <p>Sign in with your Grimarr server password.</p>
        {error && <Notice text={error} />}
        <Field label="Password">
          <input
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            autoFocus
          />
        </Field>
        <button className="primary" disabled={busy}>
          {busy ? "Signing in…" : "Open my library"}
          <ArrowUpRight size={17} />
        </button>
        <small>
          Set your password with GRIMARR_PASSWORD in Docker Compose.
        </small>
      </form>
    </div>
  );
}
function Field({
  label,
  help,
  children,
}: {
  label: string;
  help?: string;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {isValidElement(children)
        ? cloneElement(
            children as React.ReactElement<{
              id: string;
              "aria-describedby"?: string;
            }>,
            { id, "aria-describedby": help ? id + "-help" : undefined },
          )
        : children}
      {help && <small id={id + "-help"}>{help}</small>}
    </div>
  );
}
function PrefFields({
  value,
  onChange,
}: {
  value: Preferences;
  onChange: (value: Preferences) => void;
}) {
  return (
    <div className="form-grid">
      <Field label="Narration">
        <select
          value={value.narration}
          onChange={(e) => onChange({ ...value, narration: e.target.value })}
        >
          <option value="either">Any narration</option>
          <option value="standard">Standard narration</option>
          <option value="dramatized">Dramatized / full cast</option>
        </select>
      </Field>
      <Field label="Edition length">
        <select
          value={value.abridgement}
          onChange={(e) => onChange({ ...value, abridgement: e.target.value })}
        >
          <option value="unabridged">Unabridged</option>
          <option value="abridged">Abridged</option>
          <option value="either">Either</option>
        </select>
      </Field>
      <Field label="Minimum seeders">
        <input
          type="number"
          min="0"
          max="100000"
          value={value.minimumSeeders}
          onChange={(e) =>
            onChange({ ...value, minimumSeeders: Number(e.target.value) })
          }
        />
      </Field>
      <label className="check-field">
        <input
          type="checkbox"
          checked={value.preferM4b}
          onChange={(e) => onChange({ ...value, preferM4b: e.target.checked })}
        />
        Prefer M4B when comparable
      </label>
    </div>
  );
}
function Modal({
  title,
  close,
  children,
}: {
  title: string;
  close: () => void;
  children: ReactNode;
}) {
  useEffect(() => {
    const listener = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("keydown", listener);
    const old = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", listener);
      document.body.style.overflow = old;
    };
  }, [close]);
  return (
    <div
      className="modal-backdrop"
      onClick={(e) => {
        if (e.target === e.currentTarget) close();
      }}
    >
      <section
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label={title}
      >
        <div className="modal-heading">
          <h2>{title}</h2>
          <button
            className="icon-button"
            aria-label="Close dialog"
            onClick={close}
          >
            <X />
          </button>
        </div>
        {children}
      </section>
    </div>
  );
}
function AddModal({
  defaults,
  close,
  added,
}: {
  defaults: Preferences;
  close: () => void;
  added: () => Promise<void>;
}) {
  const [term, setTerm] = useState(""),
    [results, setResults] = useState<CatalogBook[]>([]),
    [book, setBook] = useState<CatalogBook | null>(null),
    [prefs, setPrefs] = useState(defaults),
    [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [searched, setSearched] = useState(false);
  const search = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      setResults(
        await api<CatalogBook[]>("/catalog?q=" + encodeURIComponent(term)),
      );
      setSearched(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal title="Add audiobook" close={close}>
      {error && <Notice text={error} />}
      <p className="muted">
        Search the Apple Books catalog, or add a title yourself.
      </p>
      <form className="catalog-search" onSubmit={search}>
        <label className="search">
          <Search size={18} />
          <input
            autoFocus
            value={term}
            onChange={(e) => setTerm(e.target.value)}
            placeholder="Book title or author"
            aria-label="Search audiobook catalog"
            required
            minLength={2}
          />
        </label>
        <button className="primary" disabled={busy}>
          {busy ? <LoaderCircle className="spin" size={18} /> : "Search"}
        </button>
      </form>
      {!book && (
        <>
          <div className="catalog-results">
            {results.map((r, i) => (
              <button
                key={r.catalogId + i}
                className="catalog-item"
                onClick={() =>
                  setBook({
                    ...r,
                    title: r.title.replace(/\s*\(Unabridged\)\s*$/i, ""),
                  })
                }
              >
                <Cover book={r} />
                <span>
                  <strong>{r.title}</strong>
                  <small>{r.author}</small>
                </span>
                <Plus size={19} />
              </button>
            ))}
          </div>
          {searched && results.length === 0 && (
            <p>
              No catalog results. You can still add the book by title and
              author.
            </p>
          )}
          <button
            className="secondary"
            onClick={() =>
              setBook({ title: term, author: "", cover: "", catalogId: "" })
            }
          >
            Add by title and author
          </button>
        </>
      )}
      {book && (
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            setBusy(true);
            setError("");
            try {
              await api("/books", "POST", { ...book, preferences: prefs });
              await added();
            } catch (e) {
              setError((e as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          <div className="form-grid">
            <Field label="Book title">
              <input
                required
                maxLength={250}
                value={book.title}
                onChange={(e) => setBook({ ...book, title: e.target.value })}
              />
            </Field>
            <Field label="Author">
              <input
                required
                maxLength={200}
                value={book.author}
                onChange={(e) => setBook({ ...book, author: e.target.value })}
              />
            </Field>
          </div>
          <h3>Your edition preferences</h3>
          <PrefFields value={prefs} onChange={setPrefs} />
          <p className="helper">
            English only. Recording ratings are currently unavailable from this
            catalog; release matching and download quality determine selection.
          </p>
          <div className="modal-actions">
            <button
              type="button"
              className="secondary"
              onClick={() => setBook(null)}
            >
              Back to results
            </button>
            <button className="primary" disabled={busy}>
              <Plus size={17} />
              {busy ? "Adding…" : "Add & download"}
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
function BookModal({
  book,
  settings,
  close,
  changed,
}: {
  book: Book;
  settings: Settings;
  close: () => void;
  changed: () => Promise<void>;
}) {
  const [releases, setReleases] = useState<Release[] | null>(null),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false),
    [prefs, setPrefs] = useState(book.preferences);
  const act = async (path: string, method = "POST", body?: unknown) => {
    setBusy(true);
    setError("");
    try {
      await api(path, method, body);
      await changed();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal title={book.title} close={close}>
      <p className="muted">{book.author}</p>
      {error && <Notice text={error} />}
      <div className="detail-state">
        <span className={"badge " + book.status}>
          {book.suspended ? "Monitoring paused" : statusLabel(book.status)}
        </span>
        <p>{book.message}</p>
        {book.selectedRelease && <small>{book.selectedRelease.title}</small>}
      </div>
      {book.status === "wanted" && (
        <>
          <h3>Edition preferences</h3>
          <PrefFields value={prefs} onChange={setPrefs} />
          <button
            className="secondary"
            disabled={busy}
            onClick={() => act(`/books/${book.id}/preferences`, "PUT", prefs)}
          >
            Save preferences
          </button>
        </>
      )}
      <div className="modal-actions">
        <button
          className="secondary"
          disabled={busy}
          onClick={() =>
            act(`/books/${book.id}/${book.suspended ? "retry" : "pause"}`)
          }
        >
          {book.suspended ? <RotateCw size={16} /> : <Pause size={16} />}{" "}
          {book.suspended ? "Resume monitoring" : "Pause monitoring"}
        </button>
        {book.status !== "available" && (
          <button
            className="secondary"
            disabled={busy}
            onClick={() => act(`/books/${book.id}/retry`)}
          >
            <RotateCw size={16} />
            Retry now
          </button>
        )}
        {book.status === "available" && (
          <a
            className="primary"
            href={settings.audiobookshelf.url}
            target="_blank"
            rel="noreferrer"
          >
            Open Audiobookshelf
            <ArrowUpRight size={17} />
          </a>
        )}
      </div>
      <button
        className="text-button"
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError("");
          try {
            setReleases(await api<Release[]>(`/books/${book.id}/releases`));
          } catch (e) {
            setError((e as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        {busy ? "Working…" : "Search releases & inspect ranking"}
        <Search size={16} />
      </button>
      {releases && (
        <div className="release-list">
          {releases.length === 0 ? (
            <p>No releases found.</p>
          ) : (
            releases.map((r, i) => (
              <div className="release" key={i}>
                <strong>{r.release.title}</strong>
                <span
                  className={"badge " + (r.eligible ? "available" : "wanted")}
                >
                  {r.eligible ? `Eligible · ${r.score}` : "Rejected"}
                </span>
                <small>
                  {r.release.indexer} ·{" "}
                  {(r.release.size / 1024 / 1024).toFixed(0)} MB ·{" "}
                  {r.release.seeders} seeders
                </small>
                <p>{r.reasons.join(" · ")}</p>
              </div>
            ))
          )}
        </div>
      )}
    </Modal>
  );
}
function SettingsPage({ onSaved }: { onSaved: () => Promise<void> }) {
  const [value, setValue] = useState<Settings | null>(null),
    [secrets, setSecrets] = useState<Record<string, boolean>>({}),
    [libraries, setLibraries] = useState<{ id: string; name: string }[]>([]),
    [message, setMessage] = useState(""),
    [busy, setBusy] = useState("");
  useEffect(() => {
    api<{ settings: Settings; secrets: Record<string, boolean> }>("/settings")
      .then((r) => {
        setValue(r.settings);
        setSecrets(r.secrets);
      })
      .catch((e) => setMessage(e.message));
  }, []);
  if (!value) return <p>Loading settings…</p>;
  const change = <K extends keyof Settings>(key: K, v: Settings[K]) =>
    setValue({ ...value, [key]: v });
  const test = async (key: string) => {
    setBusy(key);
    setMessage("");
    try {
      const r = await api<{
        message: string;
        libraries?: { id: string; name: string; mediaType: string }[];
      }>(`/connections/${key}/test`, "POST", value);
      setMessage(`${key}: ${r.message}`);
      if (r.libraries)
        setLibraries(r.libraries.filter((l) => l.mediaType === "book"));
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy("");
    }
  };
  return (
    <>
      <div className="page-heading">
        <div className="eyebrow">MAKE IT YOURS</div>
        <h1>Settings</h1>
        <p>Your connections, your folders, your preferred editions.</p>
      </div>
      {message && <Notice text={message} close={() => setMessage("")} />}
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy("save");
          try {
            const r = await api<{
              settings: Settings;
              secrets: Record<string, boolean>;
            }>("/settings", "PUT", value);
            setValue(r.settings);
            setSecrets(r.secrets);
            setMessage("Settings saved.");
            await onSaved();
          } catch (e) {
            setMessage((e as Error).message);
          } finally {
            setBusy("");
          }
        }}
      >
        <section className="settings-section" id="connections">
          <div className="section-title">
            <span>01</span>
            <div>
              <h2>Connections</h2>
              <p>
                Use a local IP, Docker hostname, or Tailscale address. Ports are
                editable.
              </p>
            </div>
          </div>
          {(["prowlarr", "qbittorrent", "audiobookshelf"] as const).map(
            (key) => (
              <div className="connection" key={key}>
                <div className="connection-heading">
                  <h3>
                    {
                      {
                        prowlarr: "Prowlarr",
                        qbittorrent: "qBittorrent",
                        audiobookshelf: "Audiobookshelf",
                      }[key]
                    }
                  </h3>
                  <button
                    type="button"
                    className="secondary small"
                    disabled={!!busy}
                    onClick={() => test(key)}
                  >
                    {busy === key ? "Testing…" : "Test connection"}
                  </button>
                </div>
                <div className="form-grid">
                  <Field
                    label="Server URL"
                    help={
                      key === "audiobookshelf"
                        ? "Example: http://192.168.1.10:13378. Docker-internal port is usually 80."
                        : "Include http:// or https:// and the port."
                    }
                  >
                    <input
                      required
                      type="url"
                      value={value[key].url}
                      onChange={(e) =>
                        change(key, { ...value[key], url: e.target.value })
                      }
                    />
                  </Field>
                  {key === "qbittorrent" ? (
                    <>
                      <Field label="Username">
                        <input
                          autoComplete="off"
                          value={value[key].username}
                          onChange={(e) =>
                            change(key, {
                              ...value[key],
                              username: e.target.value,
                            })
                          }
                        />
                      </Field>
                      <Field
                        label="Password"
                        help={
                          secrets[key]
                            ? "Saved. Leave blank to keep existing password."
                            : undefined
                        }
                      >
                        <input
                          type="password"
                          autoComplete="new-password"
                          value={value[key].password}
                          onChange={(e) =>
                            change(key, {
                              ...value[key],
                              password: e.target.value,
                            })
                          }
                        />
                      </Field>
                    </>
                  ) : (
                    <Field
                      label="API key / token"
                      help={
                        secrets[key]
                          ? "Saved. Leave blank to keep existing key."
                          : undefined
                      }
                    >
                      <input
                        type="password"
                        autoComplete="new-password"
                        value={value[key].apiKey}
                        onChange={(e) =>
                          change(key, { ...value[key], apiKey: e.target.value })
                        }
                      />
                    </Field>
                  )}
                </div>
              </div>
            ),
          )}
          <div className="form-grid">
            <Field
              label="Audiobookshelf library"
              help="Test Audiobookshelf above to load libraries, or enter a library ID."
            >
              {libraries.length ? (
                <select
                  value={value.libraryId}
                  onChange={(e) => change("libraryId", e.target.value)}
                >
                  <option value="">Select your audiobook library</option>
                  {libraries.map((l) => (
                    <option key={l.id} value={l.id}>
                      {l.name}
                    </option>
                  ))}
                </select>
              ) : (
                <input
                  value={value.libraryId}
                  onChange={(e) => change("libraryId", e.target.value)}
                  placeholder="Library ID"
                />
              )}
            </Field>
            <Field
              label="Prowlarr indexer IDs"
              help="Comma-separated. Leave empty to search all enabled indexers."
            >
              <input
                defaultValue={value.indexerIds.join(", ")}
                pattern="[0-9, ]*"
                title="Enter comma-separated numeric indexer IDs."
                onBlur={(e) => {
                  const parts = e.target.value
                    .split(",")
                    .map((x) => x.trim())
                    .filter(Boolean);
                  if (parts.some((x) => !/^\d+$/.test(x))) {
                    setMessage("Indexer IDs must be comma-separated numbers.");
                    return;
                  }
                  change("indexerIds", parts.map(Number));
                }}
              />
            </Field>
          </div>
        </section>
        <section className="settings-section" id="storage">
          <div className="section-title">
            <span>02</span>
            <div>
              <h2>Media Management</h2>
              <p>
                These paths are inside Grimarr’s container. Mount the host
                folders in Docker Compose first.
              </p>
            </div>
          </div>
          <div className="form-grid">
            <Field label="Download root">
              <input
                required
                value={value.downloadRoot}
                onChange={(e) => change("downloadRoot", e.target.value)}
              />
            </Field>
            <Field label="Audiobook library root">
              <input
                required
                value={value.libraryRoot}
                onChange={(e) => change("libraryRoot", e.target.value)}
              />
            </Field>
            <Field
              label="qBittorrent save path"
              help="As seen by qBittorrent. Leave blank to use its default."
            >
              <input
                value={value.qbitSavePath}
                onChange={(e) => change("qbitSavePath", e.target.value)}
              />
            </Field>
            <Field label="Import method">
              <select
                value={value.importMode}
                onChange={(e) => change("importMode", e.target.value)}
              >
                <option value="hardlink-or-copy">
                  Hardlink, with copy fallback
                </option>
                <option value="copy">Always copy</option>
              </select>
            </Field>
          </div>
          <h3>Remote path mappings</h3>
          <p className="helper">
            Only needed when qBittorrent and Grimarr see the same folder at
            different paths.
          </p>
          {value.pathMappings.map((m, i) => (
            <div className="mapping" key={i}>
              <input
                aria-label="qBittorrent path"
                placeholder="qBittorrent: /downloads"
                value={m.remote}
                onChange={(e) =>
                  change(
                    "pathMappings",
                    value.pathMappings.map((x, j) =>
                      i === j ? { ...x, remote: e.target.value } : x,
                    ),
                  )
                }
              />
              <ChevronRight size={16} />
              <input
                aria-label="Grimarr path"
                placeholder="Grimarr: /data/downloads"
                value={m.local}
                onChange={(e) =>
                  change(
                    "pathMappings",
                    value.pathMappings.map((x, j) =>
                      i === j ? { ...x, local: e.target.value } : x,
                    ),
                  )
                }
              />
              <button
                className="icon-button"
                type="button"
                aria-label="Remove mapping"
                onClick={() =>
                  change(
                    "pathMappings",
                    value.pathMappings.filter((_, j) => j !== i),
                  )
                }
              >
                <X size={17} />
              </button>
            </div>
          ))}
          <button
            type="button"
            className="secondary small"
            onClick={() =>
              change("pathMappings", [
                ...value.pathMappings,
                { remote: "", local: "" },
              ])
            }
          >
            <Plus size={16} />
            Add mapping
          </button>
          <p className="helper">
            Imports use Author / Title [Grimarr ID]. Originals are retained for
            seeding. The library’s parent folder must be writable for staged
            imports.
          </p>
        </section>
        <section className="settings-section" id="profiles">
          <div className="section-title">
            <span>03</span>
            <div>
              <h2>Profiles</h2>
              <p>
                Defaults for new books. Override these on any monitored book.
              </p>
            </div>
          </div>
          <PrefFields
            value={value.defaults}
            onChange={(v) => change("defaults", v)}
          />
          <p className="helper">
            Language: English. Ambiguous titles, unknown language, and
            unconfirmed edition lengths stay monitored rather than downloading
            automatically. Listener-rating integration is not yet available.
          </p>
          <Field
            label="Search interval (minutes)"
            help="For books with no eligible release. Downloads are checked approximately every 15 seconds."
          >
            <input
              type="number"
              min="15"
              max="10080"
              value={value.searchIntervalMinutes}
              onChange={(e) =>
                change("searchIntervalMinutes", Number(e.target.value))
              }
            />
          </Field>
          <label className="automation-toggle">
            <input
              type="checkbox"
              checked={value.automationEnabled}
              onChange={(e) => change("automationEnabled", e.target.checked)}
            />
            <span>
              <strong>Enable automatic searching and importing</strong>
              <small>
                Adding a book will trigger selection and download on the next
                worker cycle.
              </small>
            </span>
          </label>
        </section>
        <div className="save-bar">
          <span>Settings stay on this server.</span>
          <button className="primary" disabled={!!busy}>
            <Check size={18} />
            {busy === "save" ? "Saving…" : "Save settings"}
          </button>
        </div>
      </form>
    </>
  );
}
function ActivityPage() {
  const [items, setItems] = useState<Activity[]>([]),
    [error, setError] = useState("");
  useEffect(() => {
    const load = () =>
      api<Activity[]>("/activity")
        .then(setItems)
        .catch((e) => setError(e.message));
    load();
    const timer = setInterval(load, 10000);
    return () => clearInterval(timer);
  }, []);
  return (
    <>
      <div className="page-heading">
        <div className="eyebrow">BEHIND THE SHELVES</div>
        <h1>Activity</h1>
        <p>Searches, downloads, and imports from your last 100 events.</p>
      </div>
      {error && <Notice text={error} />}
      <div className="activity-list">
        {items.length === 0 ? (
          <div className="empty">
            <ActivityIcon size={36} />
            <h2>No activity yet.</h2>
            <p>Your audiobook activity will appear here.</p>
          </div>
        ) : (
          items.map((item) => (
            <div className="activity-item" key={item.id}>
              <span className="activity-icon">
                <ActivityIcon size={17} />
              </span>
              <div>
                <strong>{item.title}</strong>
                <p>{item.message}</p>
              </div>
              <time>{new Date(item.at).toLocaleString()}</time>
            </div>
          ))
        )}
      </div>
    </>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
