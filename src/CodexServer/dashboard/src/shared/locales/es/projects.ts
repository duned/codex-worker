import type { projects as source } from '../en/projects';
export const projects = {
  "projects.title": "Proyectos", "projects.description": "Repositorios cuyas issues reparte el servidor entre los Workers.", "projects.add": "Añadir proyecto",
  "projects.search": "Buscar por nombre o repositorio", "projects.active": "Activo", "projects.paused": "Pausado", "projects.working": "Trabajando",
  "projects.project": "Proyecto", "projects.repository": "Repositorio", "projects.running": "En curso", "projects.queued": "En cola",
  "projects.none": "Todavía no hay proyectos. Añade uno para empezar a enviar issues a los Workers.", "projects.edit": "Editar proyecto", "projects.saved": "Proyecto guardado.",
  "projects.created": "Proyecto creado.", "projects.name": "Nombre", "projects.repositoryHint": "Repositorio de GitHub con el formato propietario/repositorio.",
  "projects.baseBranch": "Rama base", "projects.baseBranchHint": "Aquí se integra cada issue terminada.", "projects.parallel": "Issues en paralelo",
  "projects.parallelHint": "Cuántas issues de este proyecto pueden implementarse a la vez.", "projects.validation": "Comandos de validación",
  "projects.validationHint": "Uno por línea. Se ejecutan antes de integrar; si uno falla, no se integra nada. Vacío usa la configuración del Worker.",
  "projects.readyLabel": "Etiqueta de lista", "projects.readyLabelHint": "Las issues con esta etiqueta están listas para implementarse.",
  "projects.automatic": "Recoger automáticamente las issues con la etiqueta {label}", "projects.descriptionField": "Descripción",
  "projects.integratesInto": "se integra en", "projects.pause": "Pausar proyecto", "projects.resume": "Reanudar proyecto",
  "projects.pausedDone": "Proyecto pausado. Lo que está en curso termina; no empieza nada nuevo.", "projects.resumed": "Proyecto reanudado.",
  "projects.configuration": "Configuración", "projects.upTo": "hasta {n}", "projects.workerValidation": "Configuración del Worker",
  "projects.pickup": "Recogida", "projects.pickupAutomatic": "Automática: issues con la etiqueta {label}", "projects.pickupManual": "Manual: solo las que añadas a la cola",
  "projects.anyWorker": "Cualquier Worker apto"
} satisfies Record<keyof typeof source, string>;
