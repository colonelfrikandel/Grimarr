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
      <button className={styles.brand} onClick={onHome} aria-label="Grimarr home">
        <BookOpen aria-hidden="true" />
        <span>GRIMARR</span>
      </button>

      <form
        className="header-search"
        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          onHome();
        }}
      >
        <Search size={18} aria-hidden="true" />
        <input
          aria-label="Filter library"
          placeholder="Search your library"
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
        />
      </form>

      <div className={styles.actions}>
        <div className="top-meta">
          <span className={`status-dot${automationEnabled ? "" : " off"}`} />
          {automationEnabled ? "Automation enabled" : "Automation paused"}
        </div>
        <button className="header-logout" aria-label="Sign out" title="Sign out" onClick={onSignOut}>
          <LogOut size={18} aria-hidden="true" />
        </button>
      </div>
    </header>
  );
}
