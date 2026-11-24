# Segunda revisión funcional · noviembre 2026: lo que falta

Revisión hecha sobre `main` en la v0.36.1, después de aplicar todo lo de la [revisión funcional anterior](revision-funcional-2026-11.md). Esta vez no se buscan fallos, sino capacidades que no existen y aportarían valor. Cada punto indica lo comprobado en el código.

## Mucho valor y poco esfuerzo

1. **Cuota del motor visible en el Server y usada al planificar.** El Worker ya lee las ventanas de cuota de Codex (porcentaje restante y hora de reinicio, `CodexQuota.cs`), pero solo las escribe en el log. El Server no las conoce: puede asignar una issue a un Worker con un 2 % de cuota y el dashboard no puede decir «Codex agotado hasta las 14:00». Propuesta: enviarla en el heartbeat, mostrarla en el Worker y no asignar trabajo por debajo de un umbral. Más adelante, desviar a Claude cuando Codex esté agotado.
2. **Tokens y coste por ejecución.** Claude se lanza con `--output-format json` (incluye `usage` y `total_cost_usd`) y Codex con `exec --json` (emite eventos con el uso de tokens), pero no se leen. Basta con extraerlos y enviarlos en el informe final para que la comparativa por motor incluya coste.
3. **«Reintentar con indicaciones».** El Worker ya incluye los comentarios de la issue en el prompt (`IssueCommentContext`). «Retry from scratch» no acepta texto: un campo opcional que el Server publique como comentario antes de encolar da un bucle de corrección directo sin pasar por GitHub.
4. **Qué cambió en cada ejecución.** La página de ejecución enlaza el commit integrado, pero no muestra ficheros tocados, líneas ni un enlace de comparación con la base.

## Mucho valor, más diseño

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
