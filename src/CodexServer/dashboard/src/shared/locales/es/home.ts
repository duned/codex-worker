import type { home as source } from '../en/home';
export const home = {
  "home.title": "Inicio", "home.summary": "{online} de {workers} Workers en línea · {active} en curso · {queued} en cola.",
  "home.attentionCount": "{n} cosas requieren tu atención.", "home.allGood": "Todo funciona.", "home.restOk": "El resto funciona con normalidad.",
  "home.attention": "Requiere tu atención", "home.issueFailed": "{issue} no terminó en {project}", "home.workerCannotWork": "{worker} no puede trabajar",
  "home.running": "En curso", "home.allExecutions": "Todas las ejecuciones", "home.nothingRunning": "No hay nada en curso. Los Workers recogen la siguiente issue en cola en cuanto quedan libres.",
  "home.finished": "Últimas terminadas", "home.withProblems": "Con problemas"
} satisfies Record<keyof typeof source, string>;
