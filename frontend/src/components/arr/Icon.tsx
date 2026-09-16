import type { LucideIcon } from "lucide-react";
export type IconName = LucideIcon;
export default function Icon({
  name: Name,
  size = 18,
  isSpinning = false,
}: {
  name: IconName;
  size?: number;
  isSpinning?: boolean;
}) {
  return (
    <Name
      size={size}
      className={isSpinning ? "spin" : undefined}
      aria-hidden="true"
    />
  );
}
