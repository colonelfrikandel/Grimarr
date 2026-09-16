// Adapted from Radarr (GPL-3.0), commit a96bf7c3ecb23cb76993770727285dc17a613fbc.
// Grimarr changes: local component imports, green theme, and Grimarr navigation/API callbacks.
// See THIRD_PARTY_NOTICES.md.
import classNames from "classnames";
import React, { Children, useCallback } from "react";
import Icon, { IconName } from "./Icon";
import Link from "./Link";
import styles from "./PageSidebarItem.module.css";

export interface PageSidebarItemProps {
  iconName?: IconName;
  title: string | (() => string);
  to: string;
  isActive?: boolean;
  isActiveParent?: boolean;
  isParentItem?: boolean;
  isChildItem?: boolean;
  statusComponent?: React.ElementType;
  children?: React.ReactNode;
  onPress?: () => void;
}

function PageSidebarItem({
  iconName,
  title,
  to,
  isActive,
  isActiveParent,
  isChildItem = false,
  isParentItem = false,
  statusComponent: StatusComponent,
  children,
  onPress,
}: PageSidebarItemProps) {
  const handlePress = useCallback(() => {
    if (isChildItem || !isParentItem) {
      onPress?.();
    }
  }, [isChildItem, isParentItem, onPress]);

  return (
    <div
      className={classNames(styles.item, isActiveParent && styles.isActiveItem)}
    >
      <Link
        className={classNames(
          isChildItem ? styles.childLink : styles.link,
          isActiveParent && styles.isActiveParentLink,
          isActive && styles.isActiveLink,
        )}
        to={to}
        onPress={handlePress}
      >
        {!!iconName && (
          <span className={styles.iconContainer}>
            <Icon name={iconName} />
          </span>
        )}

        {typeof title === "function" ? title() : title}

        {!!StatusComponent && (
          <span className={styles.status}>
            <StatusComponent />
          </span>
        )}
      </Link>

      {children
        ? Children.map(children, (child) => {
            if (!React.isValidElement(child)) {
              return child;
            }

            const childProps = { isChildItem: true };

            return React.cloneElement(
              child as React.ReactElement<{ isChildItem?: boolean }>,
              childProps,
            );
          })
        : null}
    </div>
  );
}

export default PageSidebarItem;
