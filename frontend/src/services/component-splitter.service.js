// Angular component splitter — the client half of the pipeline.
//
// Reuses http.js for every call, including the streaming one: the analyse
// endpoint speaks the same NDJSON-over-POST the chat stream does, so the only
// thing added here is frame parsing, not a second transport.
//
// The stage separation is preserved on this side too. `analyse` cannot produce
// files and `generate` cannot be reached without a plan, so the human approval
// gate is not something the UI remembers to enforce — it is the shape of the API.
import { http } from './http.js'

/**
 * Stage 1 alone — parse to IR and report what was detected.
 * Called before any model work so a non-Angular paste is rejected without
 * spending a token.
 *
 * @returns {Promise<{className, selector, wiring, uiKit, templateIsInline,
 *                    nodeCount, bindingCount, members, template}>}
 */
export function parseComponent({ componentTs, templateHtml }) {
  return http.post('/component-splitter/parse', { componentTs, templateHtml })
}

/**
 * Stage 2a alone — the deterministic facts, with no model involved.
 * Exposed so the "these are identical on every run" claim can be checked from
 * the UI rather than taken on trust.
 */
export function deriveFacts({ componentTs, templateHtml }) {
  return http.post('/component-splitter/facts', { componentTs, templateHtml })
}

/**
 * Stages 2a ‖ 2b → 3, streamed.
 *
 * Frames arrive as NDJSON. A chunk can split a line anywhere, so the tail is
 * carried between chunks rather than assuming chunk boundaries are line
 * boundaries — the bug that shows up only under slow networks.
 *
 * @param {object} body - { componentTs, templateHtml, model, source }
 * @param {object} handlers
 * @param {(stage: string, status: string, detail?: string) => void} handlers.onStage
 * @param {(component: object) => void} handlers.onComponent
 * @param {(text: string) => void} handlers.onAiDelta  streaming reasoning text
 * @param {(proposal: object) => void} handlers.onProposal
 * @param {(message: string) => void} handlers.onError
 * @param {AbortSignal} [signal]
 */
export async function analyse(body, handlers, signal) {
  let buffer = ''

  const handleLine = (line) => {
    const trimmed = line.trim()
    if (!trimmed) return

    let frame
    try {
      frame = JSON.parse(trimmed)
    } catch {
      return   // a partial line we will see completed in the next chunk
    }

    switch (frame.type) {
      case 'stage': handlers.onStage?.(frame.stage, frame.status, frame.detail); break
      case 'component': handlers.onComponent?.(frame.component); break
      case 'ai': handlers.onAiDelta?.(frame.text); break
      case 'proposal': handlers.onProposal?.(frame.proposal); break
      case 'error': handlers.onError?.(frame.message); break
    }
  }

  await http.stream('/component-splitter/analyse', body, (chunk) => {
    buffer += chunk
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''     // keep the incomplete tail
    lines.forEach(handleLine)
  }, signal)

  if (buffer.trim()) handleLine(buffer)
}

/**
 * Stages 5 + 6 — generate from the APPROVED plan, then validate.
 *
 * The original source goes back with the plan because the server re-parses to a
 * fresh IR rather than trusting anything the client holds: structural facts are
 * ground truth, so they are re-derived server-side every time.
 *
 * @returns {Promise<{files, parentTemplate, validation, generationSucceeded, validationPassed}>}
 */
export function generate({ componentTs, templateHtml, plan }) {
  return http.post('/component-splitter/generate', { componentTs, templateHtml, plan })
}

/**
 * The review UI's edited proposal → the plan the server accepts.
 *
 * Only components the user kept are included; a rejected one is simply absent,
 * which is why rejection needs no flag of its own. Note what is NOT sent: the
 * template block. The server resolves that from the IR by candidateId, so a
 * tampered plan cannot inject markup.
 */
export function toPlan(components) {
  return {
    components: components
      .filter((c) => c.keep && !c.hasBlocker)
      .map((c) => ({
        candidateId: c.candidateId,
        componentName: c.componentName,
        selector: c.selector,
        inputs: c.inputs.map((i) => ({
          name: i.name,
          type: i.type,
          parentExpression: i.parentExpression,
        })),
        outputs: c.outputs.map((o) => ({
          name: o.name,
          payloadType: o.payloadType,
          parentHandler: o.parentHandler,
          handlerArguments: o.handlerArguments || [],
        })),
      })),
  }
}
