import type { home as source } from '../en/home';
export const home = {
  "home.title": "Inicio", "home.summary": "{online} de {workers} Workers en línea · {active} en curso · {queued} en cola.",
  "home.attentionCount": "{n} cosas requieren tu atención.", "home.allGood": "Todo funciona.", "home.restOk": "El resto funciona con normalidad.",
  "home.attention": "Requiere tu atención", "home.issueFailed": "{issue} no terminó en {project}", "home.workerCannotWork": "{worker} no puede trabajar",
  "home.group.paused": "Proyectos pausados en un Worker", "home.group.maintenance": "Reinicios y actualizaciones sin resolver", "home.group.clock": "Workers con el reloj desajustado",
  "home.group.uncertain": "Ejecuciones con resultado desconocido", "home.group.ineligible": "En cola pero sin poder asignarse",
  "home.group.failed": "Issues que no terminaron", "home.group.workers": "Workers que no pueden coger trabajo",
  "home.projectPaused": "{project} está pausado en {worker}", "home.maintenanceUnresolved": "{worker}: un reinicio o una actualización no terminó",
  "home.executionUncertain": "{issue} en {project}: el Worker dejó de informar", "home.executionUncertainDetail": "Ocupa una plaza del proyecto hasta que lo resuelvas; con una sola plaza, todo el proyecto espera.",
  "home.queuedBlocked": "{issue} está en cola en {project} pero no se puede asignar", "home.queuedBlockedDetail": "La issue no cumple las condiciones para ejecutarse.",
  "home.running": "En curso", "home.allExecutions": "Todas las ejecuciones", "home.nothingRunning": "No hay nada en curso. Los Workers recogen la siguiente issue en cola en cuanto quedan libres.",
  "home.finished": "Últimas terminadas", "home.withProblems": "Con problemas"
} satisfies Record<keyof typeof source, string>;
