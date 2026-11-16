import type { provisioning as source } from '../en/provisioning';
export const provisioning = {
  "provisioning.reDetect": "Volver a detectar",
  "provisioning.install": "Instalar",
  "provisioning.update": "Actualizar",
  "provisioning.uninstall": "Desinstalar",
  "provisioning.configureAccess": "Configurar acceso",
  "provisioning.prepareAuthentication": "Preparar autenticación",
  "provisioning.startDeviceLogin": "Iniciar sesión con dispositivo",
  "provisioning.removeAuthentication": "Eliminar autenticación",
  "provisioning.checkAuthentication": "Comprobar autenticación",
  "provisioning.checkConfiguration": "Comprobar configuración",
  "provisioning.generateSSHKey": "Generar clave SSH",
  "provisioning.inspectPublicKey": "Inspeccionar clave pública",
  "provisioning.removeSSHKey": "Eliminar clave SSH",
  "provisioning.verifyRepositoryReadAccess": "Verificar acceso de lectura al repositorio"
} satisfies Record<keyof typeof source, string>;
