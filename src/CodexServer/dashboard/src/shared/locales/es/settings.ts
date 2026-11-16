import type { settings as source } from '../en/settings';
export const settings = {
  "settings.title": "Ajustes", "settings.description": "Servidor, herramientas y credenciales.", "settings.server": "Servidor", "settings.address": "Dirección",
  "settings.version": "Versión", "settings.startedAt": "En marcha desde", "settings.serverTools": "Herramientas del servidor", "settings.credentials": "Credenciales",
  "settings.addCredential": "Añadir credencial", "settings.editCredential": "Editar credencial", "settings.service": "Servicio", "settings.kind": "Tipo",
  "settings.worker": "Worker que puede usarla", "settings.workerHint": "Las credenciales solo se entregan al Worker asignado.",
  "settings.unassigned": "Sin asignar", "settings.noCredentials": "No hay credenciales guardadas.", "settings.value": "Valor",
  "settings.valueHint": "Se guarda cifrado. No se vuelve a mostrar.", "settings.newValue": "Nuevo valor", "settings.newValueHint": "Déjalo vacío para conservar el actual.",
  "settings.secretNotice": "Los valores nunca se muestran ni se registran.", "settings.revoke": "Revocar", "settings.credentialSaved": "Credencial guardada.",
  "settings.credentialAssigned": "Credencial asignada.", "settings.credentialRevoked": "Credencial revocada."
} satisfies Record<keyof typeof source, string>;
