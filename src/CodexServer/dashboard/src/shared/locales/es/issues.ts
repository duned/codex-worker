import type { issues as source } from '../en/issues';
export const issues = {
  "issues.title": "Issues", "issues.scope": "Qué issues", "issues.open": "Abiertas", "issues.all": "Todas", "issues.search": "Buscar por número o título",
  "issues.filterWaiting": "Esperando", "issues.filterRunning": "En curso", "issues.filterDone": "Terminadas", "issues.filterBlocked": "Bloqueadas o reintentadas",
  "issues.waitsFor": "Espera a {list}", "issues.none": "No se encontraron issues en GitHub.", "issues.enqueue": "Añadir issue a la cola",
  "issues.enqueueSubmit": "Añadir a la cola", "issues.enqueueContext": "Proyecto {project} · {repository}", "issues.number": "Número de issue",
  "issues.numberHint": "La issue debe existir y estar abierta en GitHub.", "issues.metadataHint": "El motor, el modelo y el esfuerzo se leen de la propia issue (su sección Worker).",
  "issues.openedBy": "abierta por {author}", "issues.updated": "actualizada", "issues.description": "Descripción", "issues.noDescription": "Esta issue no tiene descripción.",
  "issues.attempts": "Intentos", "issues.attemptOn": "Intento {n} en {worker}", "issues.noAttempts": "Aún no ha empezado. La recogerá el primer Worker libre cuando esté en cola.",
  "issues.status": "Estado", "issues.unassigned": "Sin asignar", "issues.dependsOn": "Depende de", "issues.nothing": "Nada", "issues.eligible": "Puede ejecutarse",
  "issues.yes": "Sí", "issues.no": "No", "issues.actions": "Acciones"
} satisfies Record<keyof typeof source, string>;
