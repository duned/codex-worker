import type { executions as source } from '../en/executions';
export const executions = {
  "changes.title": "Cambios", "changes.compare": "Comparar en GitHub", "changes.files": "{n} ficheros cambiados", "changes.more": "y {n} ficheros más",
  "usage.title": "Uso del motor", "usage.input": "{tokens} tokens de entrada ({cached} en caché)", "usage.output": "{tokens} de salida", "usage.runs": "{n} ejecuciones",
  "usage.engineTotals": "{input} entrada · {output} salida", "usage.partial": "{n} de {total} intentos informaron del uso",
  "executions.byEngine": "Por motor", "executions.byEngineScope": "Intentos terminados de esta lista",
  "executions.successRate": "Integradas", "executions.medianTime": "Tiempo mediano", "executions.totalTime": "Tiempo total",
  "executions.finishedCount": "{n} ({ok} integradas, {failed} fallidas)",
  "executions.title": "Ejecuciones", "executions.description": "Cada intento de implementar una issue, de la más reciente a la más antigua (últimas 100).",
  "executions.search": "Buscar por issue, proyecto o Worker", "executions.issue": "Issue", "executions.project": "Proyecto", "executions.worker": "Worker",
  "executions.started": "Inicio", "executions.detail": "Ejecución", "executions.attempt": "Intento {n}", "executions.attemptOn": "Intento {n} en {worker}",
  "executions.startedAgo": "empezó", "executions.integratedInto": "Integrada en {branch}", "executions.notIntegratedConflict": "No integrada: conflicto con la rama base",
  "executions.integrating": "Integrando", "executions.pending": "Pendiente", "executions.technicalDetail": "Detalle técnico", "executions.result": "Resultado",
  "executions.implementation": "Implementación", "executions.done": "Completada", "executions.inProgress": "En curso", "executions.validation": "Validación",
  "executions.integration": "Integración", "executions.duration": "Duración", "executions.summary": "Resumen", "executions.engine": "Motor",
  "executions.newerAttempt": "Hay un intento más reciente para esta issue; actúa sobre ese.",
  "executions.openNewerAttempt": "Abrir intento {n}",
  "executions.created": "Encolada", "executions.finished": "Terminada", "executions.id": "Identificador", "executions.retryOf": "Reintento de",
  "executions.prioritized": "Primera en cola", "executions.view": "Vista", "executions.viewCurrent": "Actuales", "executions.viewArchived": "Archivadas", "executions.archived": "Archivada",
  "executions.archivedAt": "Archivada", "executions.archivedDescription": "Ejecuciones terminadas ocultas de las listas. Restaura una para volver a mostrarla.",
  "executions.noArchived": "No hay ejecuciones archivadas que coincidan.", "executions.select": "Seleccionar {name}", "executions.selectAll": "Seleccionar todas las mostradas"
} satisfies Record<keyof typeof source, string>;
