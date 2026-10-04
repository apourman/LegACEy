export type MmdAmountCheck = { ok: true; amount: number } | { ok: false; reason: 'whole' | 'positive' | 'balance' };

export function checkMmdAmount(text: string, balance: number): MmdAmountCheck {
  if (!/^\d+$/.test(text)) return { ok: false, reason: 'whole' };
  const amount = Number(text);
  if (!Number.isSafeInteger(amount) || amount < 1) return { ok: false, reason: 'positive' };
  if (amount > balance) return { ok: false, reason: 'balance' };
  return { ok: true, amount };
}
