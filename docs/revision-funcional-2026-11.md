# Revisión funcional del Server y del Worker gestionado · noviembre 2026

Revisión hecha sobre `main` en la v0.35.19, a partir de los incidentes reales de la VM2 (update atascado, versión de herramientas sin refrescar, issue #107 detenida). La [auditoría anterior](auditoria-2026-11.md) era estructural (tests, dashboard, integración). Esta es funcional: se ha buscado comportamiento roto en los flujos gestionados, sobre todo estados de los que no se puede salir sin SSH o sin reiniciar.

Todo lo corregido pasa las suites completas: 2.272 tests generales, 223 de Git, 19 del dashboard y el lint.

## Patrón de fondo

Casi todos los fallos encontrados son variantes de lo mismo: **una salvaguarda pensada para el modo standalone o para un caso extremo, aplicada sin salida en el modo gestionado**. El Worker se protege pausando el proyecto o la operación, pero:

- la pausa vive en memoria y solo se quita reiniciando;
- el dashboard no la ve, porque el Worker no la informa al Server;
- y no hay ningún botón para levantarla.

En modo gestionado el Server ya es la autoridad que decide qué se asigna y cuándo, así que muchas de esas pausas no añadían seguridad y sí bloqueos.

## Corregido

### Antes de esta revisión (mismo día)

| Problema | Arreglo |
| --- | --- |
| Una actualización o reinicio fallido no se podía resolver si cambiaba cualquier capacidad del Worker (estado de `agent-readiness`, repositorios, protocolos retirados como `archived-feature-maintenance-v1`) | La comprobación compara tipo y nombre, ignora protocolos retirados y muestra qué falta |
| Plazo de 20 min para una operación iniciada | 5 min |
| La versión de una herramienta no se veía hasta recargar | El inventario se publica antes de dar el comando por terminado; el journal registra la redetección y el preflight de Codex |
| Un fallo pasajero de GitHub a mitad del cambio de etiquetas pausaba el proyecto | Un estado intermedio de la misma transición se repite (las dos operaciones son idempotentes) |
| Un «Retry from scratch» de una issue con reconciliación pendiente no podía arrancar nunca | Una asignación nueva del Server para la misma issue resuelve ese registro si no había trabajo que preservar |
| Todos los intentos salían como «Attempt 1» | Cada petición nueva para la misma issue recibe el número siguiente |

### En esta revisión

**Worker gestionado**

1. **Un fallo de GitHub al reclamar una issue ya no pausa el proyecto entero.** El Server ya bloquea esa issue hasta que alguien la reintenta; la pausa solo paraba las demás issues del proyecto hasta un reinicio.
2. **Una reconciliación pendiente de una issue ya no pausa el proyecto al prepararlo.** Mismo motivo. El modo standalone mantiene la pausa, porque allí el Worker sí decide qué coge.
3. **Un fallo de preparación (checkout, etiquetas, inicialización) se reintenta solo**, con espera creciente de 2 a 30 minutos. Antes el proyecto quedaba parado hasta reiniciar el Worker o cambiar su configuración, aunque el fallo fuese una caída de red momentánea.
4. **Un proyecto pausado en el Worker se informa al Server como bloqueado**, en lugar de `ready`, y aparece en «Why it cannot take work».
5. **Tras «Reconciliar manualmente» o la reconciliación de finalización desde el dashboard, el Worker reactiva el proyecto** que esa finalización había pausado. Antes seguía pausado hasta reiniciar.
6. **Un rechazo por preparación se clasifica como `PreparationFailed`**, no como `Blocked`. El dashboard decía «el motor necesita información o una decisión» para algo que era un fallo de checkout.

**Server**

7. **Un lease que caduca mientras el Worker reclama la issue (`Claiming`) se reencola**, igual que en `Preparing`, `Codex` o `Validation`. Antes quedaba «incierto»: reservaba la issue y una plaza del proyecto hasta una reconciliación manual, lo que con `maxParallelTasks: 1` paraba todo el proyecto. En esa etapa no puede haberse integrado nada, y el claim del siguiente intento vuelve a comprobar la issue. *Esto cambia una decisión de diseño que tenía test propio; el test se ha actualizado.*
8. **Una actualización o reinicio que caduca por tiempo se reconcilia sola** si el Worker vuelve inactivo, con la versión esperada (y, en un reinicio, como instancia nueva) y todas las comprobaciones en verde, que es exactamente lo que verifica «Aceptar el estado actual». Un fallo explícito del Worker sigue pidiendo al operador. Cubre el caso de reiniciar el servicio por SSH a mitad de una operación.
9. **«Why it cannot take work» ya no lista proyectos `not-materialized`/`materializing`**, que no bloquean nada: el checkout se hace con la primera asignación.
10. **Una preparación fallida puede reabrirse sola** si se vuelve a poner la etiqueta de lista, como ya pasaba con `Failed` y `Blocked`.

**Dashboard**

11. **Las herramientas y versiones de la página del Worker se refrescan con cada heartbeat** (antes `/api/v1/nodes` solo se leía al entrar en la página).
12. **Códigos internos traducidos**: `worker-operation-unresolved`, `project-capacity`, `project-capacity-not-advertised`, `scheduling-paused`, etc.
13. **Una ejecución en cola bloqueada por elegibilidad muestra los motivos y un botón «Comprobar la issue otra vez»**. El Server ya tenía ese endpoint, pero nada lo usaba, así que una petición bloqueada se quedaba en cola sin explicación.
14. **Explicaciones nuevas para fallo de preparación y fallo de infraestructura.** Antes caían en el genérico «la implementación o su validación falló».
15. **Una acción de herramienta en cola explica que un Worker ocupado la ejecuta al terminar sus issues**, no «en unos segundos».

## Encontrado y no corregido

Riesgos reales que no he cambiado porque tocan decisiones de seguridad o requieren diseño:

| # | Hallazgo | Riesgo | Propuesta |
| --- | --- | --- | --- |
| A | `InitializeProjectsAsync` reactiva **todos** los proyectos pausados, y se ejecuta al cambiar el inventario de herramientas o tras un comando de provisioning | Una pausa de seguridad (finalización ambigua) se levanta en silencio por un evento sin relación | Reactivar solo pausas por capacidades o preparación; dejar las de reconciliación para el operador |
| B | Varios estados inesperados de una asignación (lease inválido, proyecto fuera del registro, proyecto vaciándose, asignación sin ejecución) lanzan una excepción que **termina el proceso** | systemd lo reinicia; si el estado persiste, bucle de caídas cada ~15 min (lo que tarda en caducar el lease) | Rechazar la asignación al Server y seguir, en lugar de abortar el Worker |
| C | El poll de mantenimiento compara el plazo de la operación con el reloj local del Worker y lanza excepción si ya pasó | Con un reloj adelantado más de 5 min, el Worker cae en bucle durante una actualización | Tolerancia de reloj o ignorar la operación en vez de lanzar |
| D | Un fallo de preparación sigue rechazando la asignación como terminal | Una caída de red al clonar consume el intento y obliga a reintentar a mano (ahora con un texto correcto) | Que el Server reencole una vez las asignaciones rechazadas con `PreparationFailed` |
| E | La política de provisioning (`allowedPrivilegedActions`) solo se lee al arrancar | Tras `codex-worker config add` hay que reiniciar el Worker, y el reinicio puede chocar con una operación en curso (lo que pasó en la VM2) | Releer la política en caliente o que `config add` avise al servicio |
| F | Una ejecución `LeaseExpiredUncertain` ocupa plaza de paralelismo del proyecto hasta que se reconcilia | Con `maxParallelTasks: 1` el proyecto entero espera a un humano | Mostrarlo en la bandeja de atención de Inicio como bloqueo del proyecto, no solo en la ejecución |

## Propuestas de mejora

Por orden de impacto:

1. **«Reactivar proyecto en este Worker» desde el dashboard.** Las pausas que quedan son legítimas (finalización ambigua, modo standalone), pero solo se quitan reiniciando o por la API local. Hace falta un comando Server → Worker, como los de mantenimiento, con el motivo de la pausa visible.
2. **«Retry from scratch» que repare la etiqueta.** Hoy exige que la issue tenga la etiqueta de lista en GitHub. Si la perdió (como la #107), el operador tiene que ir a GitHub primero. El Server puede ofrecer «volver a etiquetar y reintentar» en un clic.
3. **Bandeja de atención por causa, no por ejecución.** Agrupar en Inicio: proyectos pausados en un Worker, operaciones de mantenimiento sin resolver, peticiones bloqueadas por elegibilidad y ejecuciones inciertas. Cada una con su botón.
4. **Historial de eventos del Worker en el dashboard.** El Worker ya publica eventos (`project.unavailable`, `codex.ready`, `provisioning.finished`…) pero solo se ven en el journal. Un registro con los últimos 100 evitaría tener que entrar por SSH para diagnosticar.
5. **Un único estado de salud por Worker** con su causa principal, en lugar de varios indicadores (ya lo proponía la auditoría anterior).
6. **Comprobación de reloj en el heartbeat.** El Server conoce su hora; puede avisar si el Worker se desvía más de un minuto, porque leases y plazos dependen de ello.
7. **Alertas de bloqueo por Telegram.** Ya existe la integración; enviar un aviso cuando un proyecto o un Worker deja de aceptar trabajo durante más de N minutos.

## Funcionalidades nuevas que pueden faltar

- **Reintentos automáticos acotados por clase de fallo** (preparación, red, GitHub transitorio) con un máximo por issue, visibles en el historial de intentos.
- **Pausa y reanudación de un proyecto desde el Server** para todos los Workers a la vez (hoy solo existe activar/desactivar el proyecto, que cambia su revisión).
- **Prioridad en la cola** (subir una issue al principio) y **dependencias visibles** en la vista de proyecto.
- **Coste y duración por issue y por motor**, para comparar Codex y Claude con datos.
- **Notificación de finalización** (Telegram o webhook) con enlace al commit integrado.
- **Exportar el diagnóstico de un Worker** (estado, eventos, últimas ejecuciones, versión de herramientas) como un único fichero para adjuntar a una issue de soporte.
