// Small form-state helper: values, touched flags, and per-field validation.
//
// Deliberately not a validation library — the module has five fields and one of
// them is fixed. What it does provide is the touched/error pairing, so a field
// only shows an error once the user has actually been there, rather than a form
// that is red before it has been filled in.

import { computed, reactive, ref } from 'vue'

/**
 * @param {object} initial     starting values, keyed by field name
 * @param {object} validators  fieldName -> (value, values) => string | '' (error message)
 */
export function useFormState(initial = {}, validators = {}) {
  const values = reactive({ ...initial })
  const touched = reactive({})
  const submitted = ref(false)

  /** Every field's error, whether or not it should be shown yet. */
  const errors = computed(() => {
    const out = {}
    for (const [field, validate] of Object.entries(validators)) {
      const message = validate(values[field], values)
      if (message) out[field] = message
    }
    return out
  })

  const isValid = computed(() => Object.keys(errors.value).length === 0)

  /**
   * The error to display for a field: present only once the field has been
   * touched or the form submitted, so the form is quiet until the user acts.
   */
  function getFieldError(field) {
    if (!touched[field] && !submitted.value) return ''
    return errors.value[field] || ''
  }

  /**
   * Everything an input needs, in one spread: value, handlers, and the
   * aria-invalid flag that pairs the error with the control for screen readers.
   */
  function getFieldProps(field) {
    return {
      value: values[field],
      'onUpdate:modelValue': (v) => { values[field] = v },
      onInput: (e) => {
        // Works for both a raw <input> event and a component emitting a value.
        values[field] = e?.target ? e.target.value : e
      },
      onBlur: () => { touched[field] = true },
      'aria-invalid': Boolean(getFieldError(field)) || undefined,
    }
  }

  function markTouched(field) { touched[field] = true }

  function touchAll() {
    for (const field of Object.keys(validators)) touched[field] = true
    submitted.value = true
  }

  function reset(next = initial) {
    for (const key of Object.keys(values)) delete values[key]
    Object.assign(values, next)
    for (const key of Object.keys(touched)) delete touched[key]
    submitted.value = false
  }

  return {
    values, touched, errors, isValid, submitted,
    getFieldProps, getFieldError, markTouched, touchAll, reset,
  }
}

/** Common validators, so each form doesn't restate "this is required". */
export const validators = {
  required: (label) => (value) =>
    String(value ?? '').trim() ? '' : `${label} is required.`,

  maxLength: (label, n) => (value) =>
    String(value ?? '').length <= n ? '' : `${label} must be ${n} characters or fewer.`,

  /** Runs several validators, reporting the first complaint. */
  all: (...fns) => (value, values) => {
    for (const fn of fns) {
      const message = fn(value, values)
      if (message) return message
    }
    return ''
  },
}
