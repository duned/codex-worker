# Segunda revisión funcional · noviembre 2026: lo que falta

Revisión hecha sobre `main` en la v0.36.1, después de aplicar todo lo de la [revisión funcional anterior](revision-funcional-2026-11.md). Esta vez no se buscan fallos, sino capacidades que no existen y aportarían valor. Cada punto indica lo comprobado en el código.

## Mucho valor y poco esfuerzo (hecho)

Los cuatro puntos están implementados; el resumen de cada uno está al final, en «Implementado».

1. **Cuota del motor visible en el Server y usada al planificar.** El Worker ya lee las ventanas de cuota de Codex (porcentaje restante y hora de reinicio, `CodexQuota.cs`), pero solo las escribe en el log. El Server no las conoce: puede asignar una issue a un Worker con un 2 % de cuota y el dashboard no puede decir «Codex agotado hasta las 14:00». Propuesta: enviarla en el heartbeat, mostrarla en el Worker y no asignar trabajo por debajo de un umbral. Más adelante, desviar a Claude cuando Codex esté agotado.
2. **Tokens y coste por ejecución.** Claude se lanza con `--output-format json` (incluye `usage` y `total_cost_usd`) y Codex con `exec --json` (emite eventos con el uso de tokens), pero no se leen. Basta con extraerlos y enviarlos en el informe final para que la comparativa por motor incluya coste.
3. **«Reintentar con indicaciones».** El Worker ya incluye los comentarios de la issue en el prompt (`IssueCommentContext`). «Retry from scratch» no acepta texto: un campo opcional que el Server publique como comentario antes de encolar da un bucle de corrección directo sin pasar por GitHub.
4. **Qué cambió en cada ejecución.** La página de ejecución enlaza el commit integrado, pero no muestra ficheros tocados, líneas ni un enlace de comparación con la base.

## Mucho valor, más diseño (hecho)

Los tres puntos están implementados; el resumen está al final, en «Implementado».

5. **Revisión antes de integrar (modo PR) y CI en GitHub.** Sigue abierto desde la [auditoría](auditoria-2026-11.md) (apartado 2, punto 3) y el repositorio no tiene `.github/`. Como opción por proyecto: empujar la rama y abrir un PR, o dejarla «pendiente de aprobación» con un botón en el dashboard.
6. **Identidad de operador y registro de auditoría.** El dashboard usa un único token compartido, sin usuarios. Solo los comandos de mantenimiento guardan quién los pidió (`actor`); cancelar, reconciliar, reintentar, priorizar, pausar o cambiar credenciales no deja rastro.
7. **Estadísticas históricas en el Server.** La comparativa por motor se calcula en el navegador sobre la página listada. Faltan, por proyecto y semana: integradas, éxito al primer intento, causas de fallo principales y tiempo en cola.

## Operación

8. **Webhooks de GitHub para el descubrimiento.** Hoy se sondea cada 300 s por defecto; un webhook de `issues.labeled` encolaría al instante y con menos llamadas a la API.
9. **Notificaciones más allá de Telegram.** Los mensajes de Telegram están solo en español y salen del Worker. Faltan un webhook genérico (Slack u otro) y un resumen diario del Server.
10. **Métricas `/metrics` (Prometheus).** Solo existen `/health`, `/livez` y `/readyz`.
11. **Copias del Server automáticas.** La copia es un comando manual. Falta una programada con rotación y avisar en el dashboard de que restaurar revoca los tokens de los Workers.
12. **Límites por proyecto**: máximo de intentos por issue y día, coste diario o franja horaria.

## Orden propuesto

1 → 2 → 3 → 4: son acotados, reutilizan datos que ya existen y no tocan decisiones de seguridad. El 5 conviene decidirlo antes de seguir añadiendo funciones al flujo de integración.

## Implementado

1. **Cuota.** El Worker envía su última lectura de cuota de Codex con cada heartbeat (la refresca cada 5 minutos en reposo). Si una lectura de menos de 15 minutos muestra una ventana al 5 % o menos sin reiniciar, el Server no le asigna issues de Codex y la petición en cola lo explica (`engine-quota-exhausted`); las issues de Claude siguen. La página del Worker muestra las ventanas y su hora de reinicio, y su estado pasa a «Cuota de Codex agotada». Una lectura fallida o antigua nunca bloquea. Queda pendiente desviar a Claude automáticamente.
2. **Tokens y coste.** El Worker lee el uso que informa cada CLI (eventos `turn.completed`/`token_count` de Codex; `usage` y `total_cost_usd` de Claude Code), lo suma en todas las ejecuciones del motor de un intento, lo guarda en su historial (esquema 25) y lo envía con el informe final. El Server lo guarda aparte (`execution_metrics`) y descarta métricas inválidas sin rechazar el informe. Se ve en la ejecución, en la tarjeta «Por motor» y en la issue.
3. **Reintentar con indicaciones.** Botón en la página de ejecución. El Server comprueba que la issue se puede encolar, publica el texto como comentario normal de la issue (sin la marca de mensaje generado, así que el Worker lo incluye en el prompt) y encola el reintento, restaurando la etiqueta de lista si era lo único que faltaba.
4. **Qué cambió.** Al integrar, el Worker mide el cambio con `git diff --numstat` contra la base sobre la que aterriza: ficheros, líneas añadidas y quitadas, hasta 50 rutas y los dos commits. La página de ejecución lo muestra con un enlace «Comparar en GitHub».
5. **Integración por pull request, opcional por proyecto** ([guía](pull-request-integration.md)). Por defecto todo sigue igual (integración directa). Con `integrationMode: pull-request` en el proyecto (campo «Integración» del dashboard, o `git.integration` en el YAML standalone), el Worker rebasa sobre la base actual, vuelve a validar, empuja el resultado como rama completada sin forzar y abre un PR contra la base con «Closes #N»; la base nunca se empuja y la issue queda abierta con la etiqueta de hecha hasta que GitHub fusiona. Reutiliza un PR abierto para la misma rama y un fallo al crearlo es estado incierto, nunca se repite solo. El enlace viaja en el informe final y la ejecución muestra «Pull request #N abierto para revisión». El valor por defecto se guarda como nulo para no cambiar la versión de los snapshots ni la huella de las completaciones pendientes. Se añade `.github/workflows/ci.yml` (`tools/validate.py --full` y tests del modelo del dashboard) para exigirlo como check en la rama protegida. Queda fuera la variante «pendiente de aprobación con botón en el dashboard».
6. **Identidad de operador y auditoría** ([guía](audit-log.md)). El login del dashboard pide un nombre (se recuerda en el navegador) y los clientes con token lo envían en `X-Codex-Operator`. El Server registra cada POST/PUT/PATCH/DELETE autorizado bajo `/api` y cada herramienta MCP que modifica estado: quién, canal, acción, resultado e id de petición; nunca cuerpos ni credenciales. Se guarda un año (máx. 20 000 eventos), se consulta en `GET /api/v1/audit` y en Ajustes → Actividad, y los comandos de mantenimiento guardan la misma identidad. El nombre es declarado, no autenticado: el token sigue siendo único.
7. **Estadísticas históricas en el Server** ([definiciones](execution-statistics.md)). `GET /api/v1/statistics` calcula por proyecto y semana UTC, sobre todas las ejecuciones terminadas (archivadas incluidas): integradas, fallidas, canceladas, éxito al primer intento, tres causas de fallo principales, mediana y p90 del tiempo en cola, mediana de duración y coste. La página del proyecto muestra las últimas 8 semanas en «Historial semanal».
