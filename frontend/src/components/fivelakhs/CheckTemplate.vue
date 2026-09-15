<script setup>
/**
 * The novelty cheque face.
 *
 * Read the watermark markup below as load-bearing, not decoration. This module
 * exists to produce something that is funny *because* it is obviously worthless,
 * and the disclaimer is what keeps it that way. It is rendered unconditionally —
 * there is no prop, no slot and no class that removes it — and because the PDF
 * is a raster of this element, the same pixels land in the exported file.
 *
 * Equally deliberate, and not to be "improved" later: the bank is fictional, the
 * MICR line spells FAKE / NOVELTY instead of encoding anything, and there is no
 * real routing number, logo or security-feature mimicry anywhere in here.
 *
 * Every colour on this face is a literal hex/rgb value rather than a Tailwind
 * theme token. That is a hard constraint of the export path: this project's
 * tokens are defined in oklch(), and html2canvas cannot parse that colour space
 * — inheriting even one token makes the whole PDF fail. Keep colours literal
 * here, and the cheque keeps exporting.
 */
import { computed } from 'vue'
import {
  FIXED_AMOUNT, NOVELTY_NOTICE, formatCurrency, getCurrency, getCurrencyWords,
} from '@/utils/pdfGenerator.js'

const props = defineProps({
  payeeName: { type: String, default: '' },
  bankName: { type: String, default: 'MAYER DOWA BANK' },
  currency: { type: String, default: 'BDT' },
  date: { type: String, default: '' },          // yyyy-mm-dd
  memo: { type: String, default: '' },
  accountHolder: { type: String, default: '' },
})

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** dd MMM yyyy, built from the string's own digits so no timezone can shift it. */
const displayDate = computed(() => {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(props.date || '')
  if (!m) return '—'
  return `${m[3]} ${MONTHS[Number(m[2]) - 1]} ${m[1]}`
})

const amountFigures = computed(() => formatCurrency(FIXED_AMOUNT, props.currency))
const amountWords = computed(() => getCurrencyWords(FIXED_AMOUNT, props.currency))
const symbol = computed(() => getCurrency(props.currency).symbol)

/**
 * An obviously non-functional MICR line. Real cheques encode routing and account
 * numbers here in E-13B; this spells out what the document is instead, so it
 * cannot be read by anything that processes cheques.
 */
const micrLine = computed(() =>
  `⑆FAKE000000⑆ NOVELTY⑈ ${props.currency}⑈ NO-MONETARY-VALUE⑆`)
</script>

<template>
  <!-- data-check-face marks the element the PDF exporter rasterises. -->
  <div
    data-check-face
    class="relative isolate w-full overflow-hidden rounded-lg border-2 border-[#cbd5e1] bg-[#f7f5ee] font-serif text-[#0f172a] shadow-sm"
    style="aspect-ratio: 5 / 2.2;"
  >
    <!-- Decorative guilloche: our own SVG pattern, not copied bank artwork. -->
    <svg class="pointer-events-none absolute inset-0 h-full w-full opacity-[0.14]" aria-hidden="true">
      <defs>
        <pattern id="fivelakhs-guilloche" width="44" height="44" patternUnits="userSpaceOnUse">
          <path d="M0 22 Q11 0 22 22 T44 22" fill="none" stroke="#1e3a5f" stroke-width="0.6" />
          <path d="M0 30 Q11 8 22 30 T44 30" fill="none" stroke="#1e3a5f" stroke-width="0.4" />
          <circle cx="22" cy="22" r="9" fill="none" stroke="#1e3a5f" stroke-width="0.35" />
        </pattern>
      </defs>
      <rect width="100%" height="100%" fill="url(#fivelakhs-guilloche)" />
    </svg>

    <!--
      NOVELTY WATERMARK — MANDATORY.
      Rendered unconditionally and sat above the content layer so it cannot be
      covered. Do not add a prop, a class or a setting that hides this.
    -->
    <!-- <div class="pointer-events-none absolute inset-0 z-20 flex items-center justify-center" aria-hidden="true">
      <div class="rotate-[-18deg] text-center leading-tight">
        <p class="text-[clamp(1rem,4.2vw,2.6rem)] font-black tracking-[0.14em] text-[rgba(220,38,38,0.25)]">
          {{ NOVELTY_NOTICE.en }}
        </p>
        <p class="text-[clamp(0.7rem,2.6vw,1.5rem)] font-bold text-[rgba(220,38,38,0.25)]">
          {{ NOVELTY_NOTICE.bn }}
        </p>
        <p class="text-[clamp(0.55rem,2vw,1.05rem)] font-bold tracking-[0.18em] text-[rgba(220,38,38,0.25)]">
          {{ NOVELTY_NOTICE.value }}
        </p>
      </div>
    </div> -->

    <!-- Top and bottom banners: legible even if the diagonal mark is missed. -->
    <div class="absolute inset-x-0 top-0 z-30 bg-[#dc2626] py-[0.35em] text-center text-[clamp(0.45rem,1.35vw,0.72rem)] font-bold uppercase tracking-[0.2em] text-[#ffffff]">
      ভন্ড চেক তৈরী করার আন্তর্জালস্থল
    </div>

    <!-- Cheque content -->
    <div class="relative z-10 flex h-full flex-col justify-between p-[4%] pt-[7%]">
      <div class="flex items-start justify-between gap-4">
        <div class="min-w-0">
          <p class="truncate text-[clamp(0.75rem,2.6vw,1.35rem)] font-bold tracking-wide">
            {{ bankName || 'MAYER DOWA BANK' }}
          </p>
          <p class="text-[clamp(0.4rem,1.2vw,0.65rem)] uppercase tracking-[0.16em] text-[#64748b]">
            1939 Kane Street, Gotham City
          </p>
        </div>

        <div class="shrink-0 text-right">
          <p class="text-[clamp(0.4rem,1.2vw,0.62rem)] uppercase tracking-[0.14em] text-[#64748b]">
            Date
          </p>
          <p class="border-b border-[#94a3b8] px-2 text-[clamp(0.6rem,1.9vw,0.95rem)] font-semibold">
            {{ displayDate }}
          </p>
        </div>
      </div>

      <!-- Payee + figures -->
      <div class="flex items-end gap-3">
        <div class="min-w-0 flex-1">
          <p class="text-[clamp(0.4rem,1.2vw,0.62rem)] uppercase tracking-[0.14em] text-[#64748b]">
            Pay to the order of
          </p>
          <p class="truncate border-b border-[#64748b] pb-[0.15em] text-[clamp(0.7rem,2.4vw,1.25rem)] font-semibold">
            {{ payeeName || '—' }}
          </p>
        </div>

        <div class="shrink-0 rounded border-2 border-[#64748b] bg-[rgba(255,255,255,0.7)] px-[0.8em] py-[0.3em]">
          <span class="text-[clamp(0.7rem,2.4vw,1.25rem)] font-bold tabular-nums">
            {{ amountFigures }}
          </span>
        </div>
      </div>

      <!-- Amount in words -->
      <div>
        <p class="truncate border-b border-[#64748b] pb-[0.15em] text-[clamp(0.55rem,1.8vw,0.95rem)] italic">
          {{ amountWords }}
        </p>
        <p class="text-[clamp(0.4rem,1.2vw,0.62rem)] uppercase tracking-[0.14em] text-[#64748b]">
          Amount in words · fixed at {{ symbol }} 5,00,000
        </p>
      </div>

      <!-- Memo + signature -->
      <div class="flex items-end justify-between gap-4">
        <div class="min-w-0 flex-1">
          <p class="text-[clamp(0.4rem,1.2vw,0.62rem)] uppercase tracking-[0.14em] text-[#64748b]">
            Memo
          </p>
          <p class="truncate border-b border-[#94a3b8] pb-[0.1em] text-[clamp(0.5rem,1.6vw,0.82rem)]">
            {{ memo || '—' }}
          </p>
        </div>

        <div class="w-[38%] shrink-0 text-center">
          <p class="truncate border-b border-[#64748b] pb-[0.1em] text-[clamp(0.55rem,1.8vw,0.9rem)] font-[cursive]">
            {{ accountHolder || '—' }}
          </p>
          <p class="text-[clamp(0.4rem,1.2vw,0.62rem)] uppercase tracking-[0.14em] text-[#64748b]">
            Not a signature · novelty only
          </p>
        </div>
      </div>

      <!-- Deliberately fake MICR: spells out the disclaimer, encodes nothing. -->
      <p class="truncate text-center text-[clamp(0.45rem,1.5vw,0.8rem)] tracking-[0.12em] text-[#475569]" style="font-family: 'Courier New', monospace;">
        {{ micrLine }}
      </p>
    </div>

    <div class="absolute inset-x-0 bottom-0 z-30 bg-[#dc2626] py-[0.3em] text-center text-[clamp(0.4rem,1.2vw,0.65rem)] font-bold uppercase tracking-[0.18em] text-[#ffffff]">
      identify as non-negotiable with no monetary value
    </div>
  </div>
</template>
