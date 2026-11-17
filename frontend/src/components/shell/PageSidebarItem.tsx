import { Children, cloneElement, isValidElement, type ElementType, type ReactElement, type ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import styles from "./PageSidebarItem.module.css";

export interface PageSidebarItemProps {
  iconName?: LucideIcon;
  title: string | (() => string);
  to: string;
  isActive?: boolean;
  isActiveParent?: boolean;
  isChildItem?: boolean;
  statusComponent?: ElementType;
  children?: ReactNode;
  onPress?: () => void;
}

export default function PageSidebarItem({
  iconName: Icon,
  title,
  isActive,
  isActiveParent,
  isChildItem = false,
  statusComponent: Status,
  children,
  onPress,
}: PageSidebarItemProps) {
  const classes = [
    isChildItem ? styles.child : styles.button,
    isActiveParent ? styles.selected : "",
    isActive ? styles.current : "",
  ].filter(Boolean).join(" ");

  return (
    <div className={styles.item}>
      <button type="button" className={classes} onClick={onPress}>
        {Icon && <Icon className={styles.icon} size={18} aria-hidden="true" />}
        <span>{typeof title === "function" ? title() : title}</span>
        {Status && <span className={styles.status}><Status /></span>}
      </button>
      {children && (
        <div className={styles.children}>
          {Children.map(children, (child) =>
            isValidElement(child)
              ? cloneElement(child as ReactElement<PageSidebarItemProps>, { isChildItem: true })
              : child,
          )}
        </div>
      )}
    </div>
  );
}
