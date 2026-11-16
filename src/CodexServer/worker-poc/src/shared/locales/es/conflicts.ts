import type { conflicts as source } from '../en/conflicts';
export const conflicts = {
  "conflicts.title": "Reintentar conflicto de integración preservado",
  "conflicts.description": "Inspeccione esta ejecución en su Worker original y solicite un intento limitado con la implementación preservada. Los límites automáticos siguen vigentes.",
  "conflicts.inspect": "Inspeccionar / actualizar elegibilidad",
  "conflicts.request": "Solicitar un intento de resolución",
  "conflicts.unavailable": "Vista previa no disponible.",
  "conflicts.parentEvidence": "Historial nativo del padre en GitHub",
  "conflicts.parentReview": "Revise el repositorio, la identidad del Issue, la fecha de ejecución y los eventos ordenados del padre. Confirmar registra esta evidencia sin cambiar la ejecución original.",
  "conflicts.parentExplicit": "La reconciliación del padre requiere aprobación explícita.",
  "conflicts.evidence": "Evidencia de la ejecución preservada",
  "conflicts.worker": "Worker original",
  "conflicts.execution": "Ejecución preservada",
  "conflicts.commit": "Commit exacto de implementación",
  "conflicts.base": "Base de integración revisada",
  "conflicts.exhausted": "El reintento automático está agotado para esta base.",
  "conflicts.automaticAvailable": "La recuperación automática podría seguir siendo elegible.",
  "conflicts.refreshResult": "Inspeccione de nuevo para conciliar la solicitud antes de reintentar.",
  "conflicts.result": "Inspeccionar intento y resultado",
  "conflicts.confirm": "Poner en cola un intento limitado para el Worker, ejecución y commit revisados. El Worker comprobará de nuevo la propiedad y la intención del Issue. No se repetirá la implementación."
} satisfies Record<keyof typeof source, string>;
