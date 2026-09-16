// Adapted from Radarr (GPL-3.0), commit a96bf7c3ecb23cb76993770727285dc17a613fbc.
// Grimarr changes: local component imports, green theme, and Grimarr navigation/API callbacks.
// See THIRD_PARTY_NOTICES.md.
import classNames from "classnames";
import React from "react";
import styles from "./LoadingIndicator.module.css";

interface LoadingIndicatorProps {
  className?: string;
  rippleClassName?: string;
  size?: number;
}

function LoadingIndicator({
  className = styles.loading,
  rippleClassName = styles.ripple,
  size = 50,
}: LoadingIndicatorProps) {
  const sizeInPx = `${size}px`;
  const width = sizeInPx;
  const height = sizeInPx;

  return (
    <div className={className} style={{ height }}>
      <div
        className={classNames(styles.rippleContainer, "followingBalls")}
        style={{ width, height }}
      >
        <div className={rippleClassName} style={{ width, height }} />

        <div className={rippleClassName} style={{ width, height }} />

        <div className={rippleClassName} style={{ width, height }} />
      </div>
    </div>
  );
}

export default LoadingIndicator;
