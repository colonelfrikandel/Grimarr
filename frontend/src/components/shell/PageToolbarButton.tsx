import type { ButtonHTMLAttributes } from "react";
import { LoaderCircle, type LucideIcon } from "lucide-react";
import styles from "./PageToolbarButton.module.css";

interface PageToolbarButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> {
  label: string;
  iconName: LucideIcon;
  spinningName?: LucideIcon;
  isSpinning?: boolean;
  isDisabled?: boolean;
  onPress?: () => void;
}

export default function PageToolbarButton({
  label,
  iconName: Icon,
  spinningName: Spinner = LoaderCircle,
  isSpinning = false,
  isDisabled = false,
  onPress,
  className = "",
  ...buttonProps
}: PageToolbarButtonProps) {
  const DisplayIcon = isSpinning ? Spinner : Icon;
  return (
    <button
      type="button"
      className={`${styles.button} ${className}`.trim()}
      disabled={isDisabled || isSpinning}
      onClick={onPress}
      title={label}
      {...buttonProps}
    >
      <DisplayIcon size={21} aria-hidden="true" className={isSpinning ? "spin" : undefined} />
      <span>{label}</span>
    </button>
  );
}
