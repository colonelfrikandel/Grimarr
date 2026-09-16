// Adapted from Radarr (GPL-3.0), commit a96bf7c3ecb23cb76993770727285dc17a613fbc.
// Grimarr replaces Redux, movie search, and the account menu with local callbacks.
// See THIRD_PARTY_NOTICES.md.
import { BookOpen, LogOut, Search } from "lucide-react";
import styles from "./PageHeader.module.css";

interface PageHeaderProps {
  query: string;
  automationEnabled: boolean;
  onQueryChange: (query: string) => void;
  onHome: () => void;
  onSignOut: () => void;
}

export default function PageHeader({
  query,
  automationEnabled,
  onQueryChange,
  onHome,
  onSignOut,
}: PageHeaderProps) {
  return (
    <header className={styles.header}>
      <div className={styles.logoContainer}>
        <a
          className={styles.logoLink}
          href="#"
          onClick={(event) => {
            event.preventDefault();
            onHome();
          }}
          aria-label="Grimarr home"
        >
          <BookOpen className={styles.logo} />
          <span>GRIMARR</span>
        </a>
      </div>
      <form
        className="header-search"
        onSubmit={(event) => {
          event.preventDefault();
          onHome();
        }}
      >
        <Search size={18} />
        <input
          aria-label="Filter library"
          placeholder="Search"
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
        />
      </form>
      <div className={styles.right}>
        <div className="top-meta">
          <span className={"status-dot " + (!automationEnabled ? "off" : "")} />
          {automationEnabled ? "Automation enabled" : "Automation paused"}
        </div>
        <button
          className="header-logout"
          aria-label="Sign out"
          title="Sign out"
          onClick={onSignOut}
        >
          <LogOut size={18} />
        </button>
      </div>
    </header>
  );
}
