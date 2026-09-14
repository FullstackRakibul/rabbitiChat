// Novelty-cheque PDF export, plus the currency/number helpers the form and the
// template both read from.
//
// The PDF is produced by rasterising the live check face (html2canvas) and
// placing that image on a landscape jsPDF page. Rasterising the real DOM is a
// deliberate choice here: it means the NOVELTY watermark that is visible on
// screen is *the same pixels* in the PDF. There is no second rendering path
// where the disclaimer could be dropped, by accident or on purpose.

import html2canvas from 'html2canvas'
import { jsPDF } from 'jspdf'

/**
 * The amount is a constant, not a form field. The whole joke is "5 lakhs", and
 * making it editable would turn a fixed gag into a blank cheque generator.
 */
export const FIXED_AMOUNT = 500000

/** Wording that must appear on the face and in the PDF. Not configurable. */
export const NOVELTY_NOTICE = {
  en: 'NOT A REAL CHEQUE',
  bn: 'নমুনা — কোনো আর্থিক মূল্য নেই',
  value: 'NOVELTY — NO MONETARY VALUE',
}

/**
 * Currencies offered in the picker.
 *
 * `words` is the major/minor unit naming used when spelling the amount out;
 * `symbol` is what prefixes the numeral on the face.
 */
export const CURRENCIES = {
  // en-IN, not en-BD: both are South Asian, but only en-IN actually groups in
  // lakhs (5,00,000 vs 500,000). Taka is the default here and the module is
  // named after the lakh, so the figures must read the way the words do.
  BDT: { symbol: '৳', code: 'BDT', label: 'Bangladeshi Taka', words: { major: 'Taka', minor: 'Poisha' }, locale: 'en-IN' },
  USD: { symbol: '$', code: 'USD', label: 'US Dollar', words: { major: 'Dollars', minor: 'Cents' }, locale: 'en-US' },
  EUR: { symbol: '€', code: 'EUR', label: 'Euro', words: { major: 'Euros', minor: 'Cents' }, locale: 'en-IE' },
  GBP: { symbol: '£', code: 'GBP', label: 'British Pound', words: { major: 'Pounds', minor: 'Pence' }, locale: 'en-GB' },
  INR: { symbol: '₹', code: 'INR', label: 'Indian Rupee', words: { major: 'Rupees', minor: 'Paise' }, locale: 'en-IN' },
  CAD: { symbol: 'C$', code: 'CAD', label: 'Canadian Dollar', words: { major: 'Dollars', minor: 'Cents' }, locale: 'en-CA' },
  AUD: { symbol: 'A$', code: 'AUD', label: 'Australian Dollar', words: { major: 'Dollars', minor: 'Cents' }, locale: 'en-AU' },
  JPY: { symbol: '¥', code: 'JPY', label: 'Japanese Yen', words: { major: 'Yen', minor: 'Sen' }, locale: 'ja-JP' },
}

export const CURRENCY_CODES = Object.keys(CURRENCIES)

/** Falls back to BDT rather than throwing, so a stale saved code cannot break the view. */
export function getCurrency(code) {
  return CURRENCIES[code] || CURRENCIES.BDT
}

/**
 * The amount as it reads on the cheque face, e.g. "৳ 5,00,000.00".
 *
 * BDT and INR group in the South Asian lakh/crore style (5,00,000), which
 * Intl already knows via their locales — worth getting right, since "5 lakhs"
 * is the entire premise.
 */
export function formatCurrency(amount, code = 'BDT') {
  const currency = getCurrency(code)
  try {
    const formatted = new Intl.NumberFormat(currency.locale, {
      minimumFractionDigits: currency.code === 'JPY' ? 0 : 2,
      maximumFractionDigits: currency.code === 'JPY' ? 0 : 2,
    }).format(amount)
    return `${currency.symbol} ${formatted}`
  } catch {
    // A locale the runtime doesn't carry shouldn't blank the cheque.
    return `${currency.symbol} ${amount.toFixed(2)}`
  }
}

const ONES = ['', 'One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight', 'Nine',
  'Ten', 'Eleven', 'Twelve', 'Thirteen', 'Fourteen', 'Fifteen', 'Sixteen',
  'Seventeen', 'Eighteen', 'Nineteen']
const TENS = ['', '', 'Twenty', 'Thirty', 'Forty', 'Fifty', 'Sixty', 'Seventy', 'Eighty', 'Ninety']

/** 0-999 in words. The building block for both grouping systems below. */
function threeDigits(n) {
  if (n === 0) return ''
  if (n < 20) return ONES[n]
  if (n < 100) {
    const rest = n % 10
    return TENS[Math.floor(n / 10)] + (rest ? `-${ONES[rest]}` : '')
  }
  const rest = n % 100
  return `${ONES[Math.floor(n / 100)]} Hundred${rest ? ` ${threeDigits(rest)}` : ''}`
}

/**
 * A whole number in English words.
 *
 * South Asian currencies are grouped in lakh/crore, so 500000 reads "Five Lakh"
 * rather than "Five Hundred Thousand" — which is the phrase the module is named
 * after, and would look wrong on a Taka cheque.
 */
export function numberToWords(value, system = 'international') {
  const n = Math.floor(Math.abs(Number(value) || 0))
  if (n === 0) return 'Zero'

  if (system === 'indian') {
    const parts = []
    const crore = Math.floor(n / 10000000)
    const lakh = Math.floor((n % 10000000) / 100000)
    const thousand = Math.floor((n % 100000) / 1000)
    const rest = n % 1000

    if (crore) parts.push(`${numberToWords(crore, 'indian')} Crore`)
    if (lakh) parts.push(`${threeDigits(lakh)} Lakh`)
    if (thousand) parts.push(`${threeDigits(thousand)} Thousand`)
    if (rest) parts.push(threeDigits(rest))
    return parts.join(' ')
  }

  const parts = []
  const scales = [[1e9, 'Billion'], [1e6, 'Million'], [1e3, 'Thousand']]
  let remaining = n
  for (const [size, name] of scales) {
    const count = Math.floor(remaining / size)
    if (count) {
      parts.push(`${threeDigits(count)} ${name}`)
      remaining %= size
    }
  }
  if (remaining) parts.push(threeDigits(remaining))
  return parts.join(' ')
}

/**
 * The full spelled-out line, e.g. "Five Lakh Taka Only".
 *
 * The trailing "Only" is the convention on a real cheque and reads as deliberate
 * here; the novelty framing comes from the watermark, not from mangling this.
 */
export function getCurrencyWords(amount, code = 'BDT') {
  const currency = getCurrency(code)
  const system = code === 'BDT' || code === 'INR' ? 'indian' : 'international'

  const whole = Math.floor(Math.abs(Number(amount) || 0))
  const fraction = Math.round((Math.abs(Number(amount) || 0) - whole) * 100)

  let words = `${numberToWords(whole, system)} ${currency.words.major}`
  if (fraction > 0) {
    words += ` and ${numberToWords(fraction, system)} ${currency.words.minor}`
  }
  return `${words} Only`
}

/**
 * A filename-safe version of a payee name.
 *
 * Bengali and other non-Latin names are the normal case here, and a naive
 * [^a-z0-9] strip reduces them to an empty string — which would yield a file
 * called ".pdf". Transliterable characters are kept where the runtime can
 * decompose them, and anything left is replaced rather than dropped, with a
 * final fallback so the result is never empty.
 */
export function sanitizeFilename(name, fallback = 'novelty_check') {
  const raw = String(name ?? '').trim()
  if (!raw) return fallback

  // Latin accents are folded (José -> Jose) because they transliterate cleanly.
  // Non-Latin scripts are left composed: decomposing them would strip the vowel
  // marks that carry the meaning, which is the opposite of helpful.
  const decomposed = raw.normalize
    ? raw.normalize('NFD').replace(/[̀-ͯ]/g, '').normalize('NFC')
    : raw

  // \p{M} matters as much as \p{L} here: Bengali, Devanagari and Arabic write
  // vowels as combining marks, so a letters-only class shreds the exact scripts
  // this fallback exists for ("রাকিবুল" -> "র_ক_ব_ল"). Marks are kept attached.
  const cleaned = decomposed
    .replace(/[^\p{L}\p{M}\p{N}]+/gu, '_')   // letters, their marks, and digits
    .replace(/_+/g, '_')
    .replace(/^_|_$/g, '')
    .slice(0, 60)

  if (!cleaned) return fallback

  // A name written entirely in a non-Latin script survives the class above but
  // can still be awkward in a filename on some systems; it is kept, since
  // dropping it would lose the only identifying part of the name.
  return cleaned
}

/**
 * Renders the given element to a landscape, cheque-proportioned PDF.
 *
 * @param {HTMLElement} element  the check face; its watermark rasterises with it
 * @param {object} meta          { payeeName, currency, amount, date }
 * @returns {Promise<{ok: boolean, filename?: string, error?: string}>}
 */
export async function generateCheckPdf(element, meta = {}) {
  if (!element) return { ok: false, error: 'Nothing to render.' }

  try {
    const canvas = await html2canvas(element, {
      scale: 2,                 // legible text without an enormous file
      backgroundColor: '#ffffff',
      logging: false,
      useCORS: true,
    })

    const imageData = canvas.toDataURL('image/png')

    // Cheque proportions rather than A4: the page is sized to the artwork, so
    // the face fills it with no letterboxing.
    const widthMm = 190
    const heightMm = Math.round((canvas.height / canvas.width) * widthMm)

    const pdf = new jsPDF({
      orientation: 'landscape',
      unit: 'mm',
      format: [widthMm, heightMm],
    })

    pdf.addImage(imageData, 'PNG', 0, 0, widthMm, heightMm)

    // Metadata states the nature of the document, so it is declared even when
    // the file is inspected rather than looked at.
    pdf.setProperties({
      title: `Novelty Check — ${meta.payeeName || 'unnamed'}`,
      subject: NOVELTY_NOTICE.value,
      author: 'Mostisko 5Lakhs (novelty generator)',
      keywords: 'novelty, joke, not-negotiable, no-monetary-value',
      creator: 'Mostisko',
    })

    const filename = `${sanitizeFilename(meta.payeeName)}_novelty_check.pdf`
    pdf.save(filename)

    return { ok: true, filename }
  } catch (e) {
    return { ok: false, error: e?.message || 'Could not generate the PDF.' }
  }
}
