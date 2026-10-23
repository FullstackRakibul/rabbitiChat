<script setup>
// The cracked telescreen glass (§3A.3): thin lines from the frame's top-right
// corner. It draws itself in once a session (300 ms), and is a decoration: it
// never covers text it would make hard to read, and serious mode hides it.
import { crackOnce } from '../lib/theme.js'

const animate = crackOnce()
</script>

<template>
  <svg viewBox="0 0 220 160" aria-hidden="true" focusable="false" data-effect="crack" class="crack" :class="{ 'crack-in': animate }">
    <path pathLength="1" d="M220 0 176 34 158 30 120 62 96 58 70 84 52 82" />
    <path pathLength="1" d="M176 34 170 64 148 88 150 112" />
    <path pathLength="1" d="M158 30 150 8 128 2" />
    <path pathLength="1" d="M120 62 118 92 104 118" />
    <path pathLength="1" d="M200 16 206 52 198 80 204 104" />
    <path pathLength="1" d="M214 6Q204 16 196 18Q186 20 176 34" />
    <path pathLength="1" d="M190 22 184 8M206 52 220 60M148 88 128 96" />
  </svg>
</template>
