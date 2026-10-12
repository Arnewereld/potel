import type { Expense, ExpenseCategory } from './types'

// Dezelfde categorieën als de server (ExpenseCategories in backend/Potel.Api/Data/Models.cs).
export const expenseCategories: { id: ExpenseCategory; label: string }[] = [
  { id: 'hardware', label: 'Hardware' },
  { id: 'software', label: 'Software en abonnementen' },
  { id: 'hosting', label: 'Hosting en domeinen' },
  { id: 'opleiding', label: 'Opleiding' },
  { id: 'reiskosten', label: 'Reiskosten' },
  { id: 'telefoon', label: 'Telefoon en internet' },
  { id: 'kantoor', label: 'Kantoor' },
  { id: 'overig', label: 'Overig' },
]

export const categoryLabel = (id: string) => expenseCategories.find(c => c.id === id)?.label ?? id

// Btw-tarieven voor een uitgave; null is "geen btw", bijvoorbeeld een treinkaartje van een buitenlandse vervoerder.
export const expenseVatRates: { value: number | null; label: string }[] = [
  { value: 21, label: '21%' },
  { value: 9, label: '9%' },
  { value: 0, label: '0%' },
  { value: null, label: 'Geen btw' },
]

// Zelfde som als Expense.VatFor op de server: btw uit een bedrag inclusief btw, of bij verlegde btw
// bovenop het bedrag. Een halve cent gaat van nul af, net als Money.Round.
export function expenseVat(amount: number, rate: number | null, reverseCharge: boolean) {
  if (!rate || rate <= 0 || !Number.isFinite(amount)) return 0
  const vat = reverseCharge ? amount * rate / 100 : amount * rate / (100 + rate)
  const cents = Number(Math.abs(vat * 100).toPrecision(15))
  return (Math.sign(vat) * Math.round(cents) || 0) / 100
}

// Wat de server als bonnetje aanneemt. De server kijkt zelf ook naar de inhoud.
export const receiptTypes = ['image/png', 'image/jpeg', 'image/webp', 'application/pdf']
export const MaxReceiptBytes = 5 * 1024 * 1024

export const fileSize = (bytes: number) => bytes >= 1024 * 1024
  ? `${(bytes / 1024 / 1024).toLocaleString('nl-NL', { maximumFractionDigits: 1 })} MB`
  : `${Math.max(1, Math.round(bytes / 1024))} KB`

// Wat een uitgave van je winst afhaalt. Kun je de btw niet terugvragen (KOR of vrijgestelde omzet), dan hoort die btw
// bij de kosten, ook de verlegde btw die je zelf afdraagt. Zelfde regel als het jaaroverzicht op de server.
export const costForProfit = (e: Pick<Expense, 'amount' | 'amountExclVat' | 'vatAmount' | 'reverseCharge'>, noDeduction: boolean) =>
  noDeduction ? e.amount + (e.reverseCharge ? e.vatAmount : 0) : e.amountExclVat
