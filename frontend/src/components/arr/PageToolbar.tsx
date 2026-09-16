// Adapted from Radarr (GPL-3.0), commit a96bf7c3ecb23cb76993770727285dc17a613fbc.
// Grimarr changes: local component imports, green theme, and Grimarr navigation/API callbacks.
// See THIRD_PARTY_NOTICES.md.
import React from "react";
import styles from "./PageToolbar.module.css";

interface PageToolbarProps {
  className?: string;
  children: React.ReactNode;
}

function PageToolbar({
  className = styles.toolbar,
  children,
}: PageToolbarProps) {
  return <div className={className}>{children}</div>;
}

export default PageToolbar;
