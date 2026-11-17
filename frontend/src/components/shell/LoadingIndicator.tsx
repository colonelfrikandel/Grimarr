import styles from "./LoadingIndicator.module.css";

export default function LoadingIndicator({ size = 48 }: { size?: number }) {
  return (
    <span
      className={styles.spinner}
      style={{ width: size, height: size }}
      aria-hidden="true"
    />
  );
}
