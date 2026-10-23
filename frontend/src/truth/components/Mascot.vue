<script setup>
// ছোট ভাই · Little Brother (§3A.4): a small round CRT telescreen on stubby legs,
// one big friendly eye, a crack across one corner. An original drawing. Its
// poses follow the news; it is aria-hidden and has no text in it (the Zzz are
// strokes). Colours come from truth.css, so both themes work.
defineProps({
  /** calm · alert · worried · relieved · reading · sleeping */
  pose: { type: String, default: 'calm' },
  size: { type: Number, default: 96 },
})
</script>

<template>
  <svg :width="size" :height="size" viewBox="0 0 120 124" aria-hidden="true" focusable="false" class="mascot flex-none" :data-pose="pose">
    <!-- legs -->
    <path d="M44 100v13M76 100v13" class="mk" stroke-width="6" stroke-linecap="round" />
    <ellipse cx="42" cy="116" rx="8" ry="4" class="mi" /><ellipse cx="78" cy="116" rx="8" ry="4" class="mi" />
    <!-- antenna: up when alert, relaxed otherwise -->
    <path v-if="pose === 'alert'" d="M50 26V8M70 26V8" class="mk" stroke-width="3" stroke-linecap="round" />
    <path v-else d="M50 26 40 12M70 26 82 14" class="mk" stroke-width="3" stroke-linecap="round" />
    <circle v-if="pose === 'alert'" cx="50" cy="7" r="3.5" class="ma" /><circle v-if="pose === 'alert'" cx="70" cy="7" r="3.5" class="ma" />
    <circle v-else cx="40" cy="11" r="3.5" class="mi" /><circle v-if="pose !== 'alert'" cx="82" cy="13" r="3.5" class="mi" />
    <!-- casing, screen, the crack -->
    <rect x="14" y="26" width="92" height="76" rx="18" class="mb" />
    <rect x="24" y="36" width="72" height="54" rx="12" class="ms" :class="{ 'flicker-once': pose === 'worried' }" />
    <path d="M96 40 86 47l3 6-8 5M86 47l-6-4" class="mc" />
    <circle cx="98" cy="96" r="2.5" class="mi" />

    <!-- calm: half-closed eye, tea -->
    <g v-if="pose === 'calm'">
      <ellipse cx="60" cy="64" rx="15" ry="11" class="me" /><circle cx="60" cy="67" r="5.5" class="mi" />
      <path d="M44 64a16 13 0 0 1 32 0z" class="ms" /><path d="M45 64h30" class="mk" stroke-width="3" stroke-linecap="round" />
      <path d="M101 74h13v8a6.5 6.5 0 0 1-13 0z" class="me mk" stroke-width="2" /><path d="M114 76a4 4 0 0 1 0 7" class="mk" stroke-width="2" />
      <path d="M105 70q-2-3 0-6M110 70q-2-3 0-6" class="mc" />
    </g>
    <!-- alert: wide eye, a little clock -->
    <g v-else-if="pose === 'alert'">
      <circle cx="60" cy="63" r="16" class="me" /><circle cx="60" cy="63" r="7" class="mi" /><circle cx="63" cy="60" r="2.4" class="me" />
      <circle cx="8" cy="80" r="7.5" class="me mk" stroke-width="2" /><path d="M8 76v4h3" class="mk" stroke-width="1.8" stroke-linecap="round" />
    </g>
    <!-- worried: brow, sweat -->
    <g v-else-if="pose === 'worried'">
      <circle cx="60" cy="65" r="13" class="me" /><circle cx="57" cy="61" r="6" class="mi" />
      <path d="M44 47l20 5" class="mk2" stroke-width="3" stroke-linecap="round" />
      <path d="M21 30q-6 9 0 12q6-3 0-12z" class="mw" />
    </g>
    <!-- relieved: smiling eye, thumbs up -->
    <g v-else-if="pose === 'relieved'">
      <path d="M45 68q15-16 30 0" class="mk2" stroke-width="5" stroke-linecap="round" />
      <rect x="104" y="72" width="11" height="13" rx="3" class="me mk" stroke-width="2" /><path d="M107 72v-8a3 3 0 0 1 6 0v8" class="me mk" stroke-width="2" />
    </g>
    <!-- reading: glasses -->
    <g v-else-if="pose === 'reading'">
      <circle cx="60" cy="64" r="11" class="me" /><circle cx="60" cy="68" r="5" class="mi" />
      <circle cx="60" cy="64" r="15" class="mk2" stroke-width="2.5" /><path d="M24 58h21M75 58h21" class="mk2" stroke-width="2.5" />
    </g>
    <!-- sleeping: closed eye, Zzz -->
    <g v-else-if="pose === 'sleeping'">
      <path d="M46 66q14 8 28 0" class="mk2" stroke-width="4" stroke-linecap="round" />
      <path d="M68 42h7l-7 7h7M78 38h9l-9 9h9" class="mk2" stroke-width="2" stroke-linejoin="round" />
    </g>
  </svg>
</template>
