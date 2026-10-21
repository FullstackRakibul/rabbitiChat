// Barrel export so callers can `import { … } from '../services'`.
export { http, AuthError, HttpError, onAccessChange } from './http.js'
export {
  uploadMatrixFiles, listMatrixUploads, getMatrixProfile, importMatrixUpload, listMatrixTemplates, updateMatrixTemplate,
  listMatrixDatasets, getMatrixDataset, getMatrixRows, getMatrixQuality, fixMatrixIssue, getMatrixCatalog,
  listMatrixDashboards, getMatrixDashboard, runMatrixDashboard, createMatrixDashboard, saveMatrixDashboard, deleteMatrixDashboard,
  listMatrixKpis, updateMatrixKpi, downloadMatrixExport, saveBlob, getMatrixCard, runMatrixReport, getMatrixSummary, writeMatrixSummary,
  streamMatrixChat, listMatrixConversations, getMatrixConversation, deleteMatrixConversation,
} from './matrix.service.js'
export { getToken, getUsername, setSession, clearSession } from './session.service.js'
export { login, logout, changeSecret } from './auth.service.js'
export { getModels, streamChat } from './chat.service.js'
export { listConversations, getConversation, deleteConversation } from './conversation.service.js'
export {
  search, fetchPage, health,
  listHistory, renameHistory, deleteHistory, restoreHistory,
} from './search.service.js'
export {
  uploadDocument, listDocuments, deleteDocument,
  extractDocument, getExtraction, workspaceChat,
  fetchDocumentFile, confirmExtraction, listBuyerProfiles,
} from './document.service.js'
export {
  listBoards, createBoard, getBoard, renameBoard, deleteBoard,
  addColumn, updateColumn, deleteColumn, reorderColumns,
  addCard, updateCard, deleteCard, moveCard,
} from './todorabbit.service.js'
export {
  listModels, listModelsWithPolicy, listModelsGrouped, saveModelChoice, loadModelChoice,
} from './model.service.js'
export {
  listContextSources, createContextSource, updateContextSource,
  deleteContextSource, reorderContextSources,
} from './context-source.service.js'
export {
  myTabs, myAccess, getCatalog, getRolePresets, saveRolePreset, searchUsers, getUser, setRole, setTabs, listTabs, listRoles,
  listModelPolicies, setModelPolicy,
} from './rbac.service.js'
export {
  parseComponent, deriveFacts, analyse as analyseComponentSplit,
  generate as generateSplit, toPlan as splitPlanFrom,
} from './component-splitter.service.js'
export {
  listNotes, createNote, getNote, updateNote,
  softDeleteNote, restoreNote, permanentDeleteNote,
  shareNote, unshareNote, getPublicNote
} from './notes.service.js'
export {
  gatewayBaseUrl, gatewayUrls, getOdditySettings, saveOdditySettings,
  listOddityKeys, createOddityKey, updateOddityKey, deleteOddityKey,
  listOddityRequests, getOddityRequest,
} from './oddity.service.js'
export {
  listActivityLogs, listAuditLogs, getActivityLog, getCorrelation,
  getSystemInfo, getLogStats, openLogStream, logClientEvent, flushClientEvents
} from './logs.service.js'
