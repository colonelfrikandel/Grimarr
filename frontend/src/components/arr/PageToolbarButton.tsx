// Adapted from Radarr (GPL-3.0), commit a96bf7c3ecb23cb76993770727285dc17a613fbc.
// Grimarr changes: local component imports, green theme, and Grimarr navigation/API callbacks.
// See THIRD_PARTY_NOTICES.md.
import classNames from "classnames";
import React from "react";
import Icon, { IconName } from "./Icon";
import Link, { LinkProps } from "./Link";
import { LoaderCircle } from "lucide-react";
import styles from "./PageToolbarButton.module.css";

export interface PageToolbarButtonProps extends LinkProps {
  label: string;
  iconName: IconName;
  spinningName?: IconName;
  isSpinning?: boolean;
  isDisabled?: boolean;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  overflowComponent?: React.ComponentType<any>;
}

function PageToolbarButton({
  label,
  iconName,
  spinningName = LoaderCircle,
  isDisabled = false,
  isSpinning = false,
  overflowComponent,
  ...otherProps
}: PageToolbarButtonProps) {
  return (
    <Link
      className={classNames(
        styles.toolbarButton,
        isDisabled && styles.isDisabled,
      )}
      isDisabled={isDisabled || isSpinning}
      title={label}
      {...otherProps}
    >
      <Icon
        name={isSpinning ? spinningName || iconName : iconName}
        isSpinning={isSpinning}
        size={21}
      />

      <div className={styles.labelContainer}>
        <div className={styles.label}>{label}</div>
      </div>
    </Link>
  );
}

export default PageToolbarButton;
