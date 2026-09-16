import type { ButtonHTMLAttributes } from "react";
export interface LinkProps extends Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  "onClick"
> {
  to?: string;
  isDisabled?: boolean;
  onPress?: () => void;
}
// Bridge Radarr's onPress/isDisabled interface to Grimarr's local navigation/actions.
export default function Link({
  to: _to,
  isDisabled,
  onPress,
  children,
  ...props
}: LinkProps) {
  return (
    <button type="button" {...props} disabled={isDisabled} onClick={onPress}>
      {children}
    </button>
  );
}
