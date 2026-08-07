// Deterministic identity swatch from a wallet address — a stable two-hue gradient
// so the same address always renders the same avatar. No dependency, no network.
export function avatarGradient(address: string): string {
  let h = 0;
  for (let i = 0; i < address.length; i++) h = (Math.imul(h, 31) + address.charCodeAt(i)) >>> 0;
  const a = h % 360;
  const b = (a + 130) % 360;
  return `linear-gradient(135deg, hsl(${a} 65% 55%), hsl(${b} 60% 45%))`;
}
