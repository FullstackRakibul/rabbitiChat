<script setup>
/**
 * The 5Lakhs novelty-cheque generator — "ভন্ড চেক".
 *
 * Two panes: the form on the left, a live preview and the download on the right,
 * with a Dashboard sub-view for the history.
 *
 * Two things here are requirements rather than choices, and should survive any
 * later edit: the amount is a constant (a fixed gag, not a blank cheque), and
 * the NOVELTY watermark on CheckTemplate is rendered unconditionally. The PDF is
 * a raster of that same element, so the disclaimer cannot be present on screen
 * and absent from the file.
 */
import { computed, ref } from 'vue'
import { Download, LayoutDashboard, Loader, PenLine } from '@lucide/vue'
import CheckTemplate from './CheckTemplate.vue'
import Dashboard from './Dashboard.vue'
import { useFormState, validators } from '@/composables/useFormState.js'
import { usePulse } from '@/composables/useAnimation.js'
import { useCheckHistory } from '@/composables/useCheckHistory.js'
import {
  CURRENCIES, CURRENCY_CODES, FIXED_AMOUNT, NOVELTY_NOTICE,
  formatCurrency, generateCheckPdf, getCurrencyWords,
} from '@/utils/pdfGenerator.js'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'

/** Fictional by default, and labelled as such — never a real institution. */
const DEFAULT_BANK = 'MAYER DOWA BANK'

function today() {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const view = ref('dashboard')      // 'generator' | 'dashboard'
const busy = ref(false)
const notice = ref('')
const faceEl = ref(null)

const { add } = useCheckHistory()
const { active: pulsing, pulse } = usePulse()

// Note what is absent: there is no `amount` field. The value is a module
// constant, so there is nothing here for a user to edit.
const form = useFormState(
  {
    payeeName: '',
    bankName: DEFAULT_BANK,
    currency: 'BDT',
    date: today(),
    memo: '',
    accountHolder: '',
  },
  {
    payeeName: validators.all(
      validators.required('Payee'),
      validators.maxLength('Payee', 60),
    ),
    bankName: validators.all(
      validators.required('Bank name'),
      validators.maxLength('Bank name', 40),
    ),
    accountHolder: validators.all(
      validators.required('Account holder'),
      validators.maxLength('Account holder', 40),
    ),
  },
)

const { values } = form

const amountFigures = computed(() => formatCurrency(FIXED_AMOUNT, values.currency))
const amountWords = computed(() => getCurrencyWords(FIXED_AMOUNT, values.currency))

const noticeIsError = ref(false)

function flash(message, isError = false) {
  notice.value = message
  noticeIsError.value = isError
  setTimeout(() => { notice.value = '' }, isError ? 8000 : 4000)
}

async function download(details = values) {
  if (busy.value) return

  // Only guard the live form; a history replay has already been validated once.
  if (details === values) {
    form.touchAll()
    if (!form.isValid.value) {
      flash('Fill in the required fields first.', true)
      return
    }
  }

  busy.value = true
  try {
    // A <script setup> child ref is an instance proxy, and $el is only a real
    // node once the component has mounted and flushed. Query the marked element
    // instead of trusting the unwrap, so the export never rasterises undefined.
    const target = faceEl.value?.$el instanceof HTMLElement
      ? faceEl.value.$el
      : document.querySelector('[data-check-face]')

    if (!target) {
      flash('The preview is not ready yet — try again in a moment.', true)
      return
    }

    const result = await generateCheckPdf(target, {
      payeeName: details.payeeName,
      currency: details.currency,
      amount: FIXED_AMOUNT,
      date: details.date,
    })

    if (!result.ok) {
      flash(result.error || 'Could not generate the PDF.', true)
      return
    }

    if (details === values) {
      add({ ...values, amount: FIXED_AMOUNT })
    }
    pulse()
    flash(`Downloaded ${result.filename}`)
  } finally {
    busy.value = false
  }
}

/**
 * Re-download a cheque from the history: load its values back into the form so
 * the preview shows what is about to be exported, then render that.
 */
async function regenerate(entry) {
  Object.assign(values, {
    payeeName: entry.payeeName,
    bankName: entry.bankName || DEFAULT_BANK,
    currency: entry.currency || 'BDT',
    date: entry.date || today(),
    memo: entry.memo || '',
    accountHolder: entry.accountHolder || '',
  })
  view.value = 'generator'
  // Let the preview re-render with the restored values before rasterising it.
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))
  await download(entry)
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Sub-view switch -->
    <div class="flex shrink-0 items-center gap-2 border-b px-4 py-2.5">
      <div class="flex items-center rounded-md border p-0.5">
        <button
          type="button"
          :class="cn(
            'flex items-center gap-1.5 rounded px-2.5 py-1 text-xs transition-colors',
            view === 'generator' ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
          )"
          @click="view = 'generator'"
        >
          <PenLine class="h-3.5 w-3.5" />
          Generator
        </button>
        <button
          type="button"
          :class="cn(
            'flex items-center gap-1.5 rounded px-2.5 py-1 text-xs transition-colors',
            view === 'dashboard' ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
          )"
          @click="view = 'dashboard'"
        >
          <LayoutDashboard class="h-3.5 w-3.5" />
          Dashboard
        </button>
      </div>

      <span class="ml-auto hidden text-[11px] text-muted-foreground sm:inline">
        Runs entirely in your browser · nothing is uploaded
      </span>
    </div>

    <!-- Sits outside the scrolling pane so a failed export is never silent. -->
    <p
      v-if="notice"
      :class="cn(
        'shrink-0 border-b px-4 py-1.5 text-xs font-medium',
        noticeIsError ? 'bg-destructive/10 text-destructive' : 'bg-muted/40',
      )"
      role="status"
    >
      {{ notice }}
    </p>

    <div class="min-h-0 flex-1 overflow-y-auto p-4">
      <!-- ---------------- Generator ---------------- -->
      <div v-show="view === 'generator'" class="grid gap-4 lg:grid-cols-2">
        <!-- Left: form -->
        <div class="flex flex-col gap-3 rounded-xl border bg-card p-4">
          <div>
            <h2 class="text-sm font-semibold">Check Details</h2>
            <p class="text-xs text-muted-foreground">
              A joke cheque. The amount is fixed and the result has no value.
            </p>
          </div>

          <div class="flex flex-col gap-1">
            <Label for="fl-payee">Pay to the Order of <span class="text-destructive">*</span></Label>
            <Input
              id="fl-payee"
              v-model="values.payeeName"
              placeholder="Who is the joke for?"
              @blur="form.markTouched('payeeName')"
            />
            <p v-if="form.getFieldError('payeeName')" class="text-xs text-destructive">
              {{ form.getFieldError('payeeName') }}
            </p>
          </div>

          <div class="flex flex-col gap-1">
            <Label for="fl-bank">Bank Name <span class="text-destructive">*</span></Label>
            <Input
              id="fl-bank"
              v-model="values.bankName"
              :placeholder="DEFAULT_BANK"
              @blur="form.markTouched('bankName')"
            />
            <p class="text-[11px] text-muted-foreground">
              Fictional names only — never a real bank.
              
            </p>
            <p v-if="form.getFieldError('bankName')" class="text-xs text-destructive">
              {{ form.getFieldError('bankName') }}
            </p>
          </div>

          <!-- Amount: shown, never editable. -->
          <div class="flex flex-col gap-1">
            <Label>Amount</Label>
            <div class="flex items-center gap-2">
              <div class="flex-1 rounded-md border border-dashed bg-muted/40 px-3 py-2">
                <p class="text-sm font-semibold tabular-nums">{{ amountFigures }}</p>
                <p class="truncate text-[11px] italic text-muted-foreground">{{ amountWords }}</p>
              </div>

              <div class="w-28 shrink-0">
                <Select v-model="values.currency">
                  <SelectTrigger class="h-9 text-xs">
                    <SelectValue placeholder="Currency" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem v-for="code in CURRENCY_CODES" :key="code" :value="code">
                      {{ CURRENCIES[code].symbol }} {{ code }}
                    </SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>
            <p class="text-[11px] text-muted-foreground">
              Fixed at five lakhs — only the currency changes.
            </p>
          </div>

          <div class="grid gap-3 sm:grid-cols-2">
            <div class="flex flex-col gap-1">
              <Label for="fl-date">Date</Label>
              <Input id="fl-date" v-model="values.date" type="date" />
            </div>
            <div class="flex flex-col gap-1">
              <Label for="fl-holder">Account Holder <span class="text-destructive">*</span></Label>
              <Input
                id="fl-holder"
                v-model="values.accountHolder"
                placeholder="Name on the cheque"
                @blur="form.markTouched('accountHolder')"
              />
              <p v-if="form.getFieldError('accountHolder')" class="text-xs text-destructive">
                {{ form.getFieldError('accountHolder') }}
              </p>
            </div>
          </div>

          <div class="flex flex-col gap-1">
            <Label for="fl-memo">Memo</Label>
            <Input id="fl-memo" v-model="values.memo" placeholder="For services rendered 😌" />
          </div>
        </div>

        <!-- Right: preview + download -->
        <div class="flex flex-col gap-3">
          <div
            :class="cn(
              'rounded-xl border bg-card p-4 transition-shadow',
              pulsing && 'ring-2 ring-primary',
            )"
          >
            <div class="mb-2 flex items-center justify-between gap-2">
              <h2 class="text-sm font-semibold">Live preview</h2>
              <span class="text-[11px] font-semibold uppercase tracking-wide text-red-600 dark:text-red-400">
                {{ NOVELTY_NOTICE.en }}
              </span>
            </div>

            <CheckTemplate
              ref="faceEl"
              :payee-name="values.payeeName"
              :bank-name="values.bankName"
              :currency="values.currency"
              :date="values.date"
              :memo="values.memo"
              :account-holder="values.accountHolder"
            />
          </div>

          <Button class="w-full" :disabled="busy" @click="download()">
            <Loader v-if="busy" class="h-4 w-4 animate-spin" />
            <Download v-else class="h-4 w-4" />
            {{ busy ? 'Generating…' : 'Download PDF' }}
          </Button>

          <p class="text-center text-[11px] leading-relaxed text-muted-foreground">
            {{ NOVELTY_NOTICE.bn }}<br>
            The watermark is part of the document and cannot be switched off.
          </p>
        </div>
      </div>

      <!-- ---------------- Dashboard ---------------- -->
      <div v-show="view === 'dashboard'">
        <Dashboard @regenerate="regenerate" />
      </div>
    </div>
  </div>
</template>
