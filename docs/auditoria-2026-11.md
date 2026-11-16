# Auditoría del proyecto · noviembre 2026

Auditoría hecha sobre `main` en `35a3b0d` (v0.34.11), con .NET SDK 10.0.115 y Node 22 en un contenedor Linux de 4 núcleos. A diferencia de las auditorías anteriores (archivadas en [archive](archive/README.md)), esta sí compiló y ejecutó la suite completa, y renderizó el dashboard.

## Resumen

El producto funciona y compila limpio, pero ha crecido demasiado deprisa: unas 500 Issues en 7 semanas, casi todas implementadas por agentes, cada una añadiendo código, tests y documentación sin que nada se retirase. Los tres problemas que describes tienen causas concretas y medibles:

| Síntoma | Causa principal | Dato |
| --- | --- | --- |
| Integración en paralelo frágil | La integración es serie por diseño (rebase + validación completa + push directo a la rama base) y el propio repo concentra los cambios en unos pocos ficheros gigantes | 12 de los últimos 36 commits de implementación tocan `ExecutionRunner.cs` y `WorkerV011Tests.cs`; 10 tocan `en.ts`/`es.ts` |
| Tests lentos | 38 clases comparten una colección xUnit no paralela porque el código lee variables de entorno del proceso | 270 s de los 290 s totales son esa colección; el resto de la suite tarda unos 20 s |
| Dashboard confuso | Es una prueba de concepto promovida a producción; muestra el modelo interno en vez de responder preguntas del operador | Home enseña textos como «Latest 50 managed requests + up to 100 retained standalone execution observations…» |

La recomendación de fondo es **parar de añadir funcionalidad y de parchear recuperaciones**, y dedicar un ciclo corto a tres cambios estructurales (apartados 2, 3 y 4). Ninguno requiere reescribir el producto.

## 1. Tamaño y forma del proyecto

| Área | Tamaño |
| --- | --- |
| Código C# de producción | 2,3 MB en 224 ficheros (Server 61, Worker 111, Shared 33, WET 19) |
| Tests C# | 2,9 MB en 143 ficheros; 2.798 casos |
| Dashboard React | 1 MB propio, de los que 335 KB son las dos traducciones |
| Scripts y tests no .NET | 9.000 líneas (`cw`, instaladores, 30 scripts de dashboard) |
| Documentación | 48 documentos Markdown antes de esta limpieza |
| Superficie | 131 endpoints HTTP en Server, 29 en Worker, 25 herramientas MCP, 24 tablas SQLite |

Hay más tests que código, y el código está concentrado en pocos ficheros: `RegistryPersistence.cs` (211 KB, una interfaz `IRegistryStore` con 61 métodos), `WorkerHost.cs`, `ServerApplication.cs` y `Worker.cs` (≈95 KB cada uno), `GitRepository.cs` (81 KB). En tests, tres ficheros suman 745 KB (`CodexServerTests`, `WorkerV011Tests`, `GitWorktreeTests`).

El alcance también se ha disparado respecto a la idea original (orquestar Issues hacia Workers). Hoy conviven: modo gestionado y standalone, observadores standalone que reportan al Server, dos motores (Codex y Claude), aprovisionamiento remoto de nodos, entrega de credenciales cifradas, autoactualización y reinicio remoto, Telegram, MCP remoto con OAuth, la CLI WET de planificación, mantenimiento de ramas completadas, herencia de sesiones Codex entre Issues y backups. Cada una multiplica los estados que las demás deben contemplar.

Señal clara de que el sistema está pagando esa complejidad: de las últimas 100 Issues, casi un tercio son «Fix», «Recover», «Diagnose», «Restore» o «Reconcile» de funcionalidad ya entregada. Hay 31 ficheros de producción dedicados a recuperación, interrupción, conflicto, mantenimiento o compatibilidad *legacy*.

## 2. Integración en paralelo

### Cómo funciona hoy

`GitRepository.CommitAndIntegrateAsync` hace, por cada ejecución y bajo el *gate* del repositorio: commit en el worktree, `fetch`, rebase sobre la rama base, **validación completa otra vez** sobre el resultado, `merge --ff-only` en el checkout canónico y `push` directo a la rama base. Si la base avanzó mientras tanto, repite (hasta 5 veces). Si el rebase tiene conflicto, invoca al motor para resolverlo dentro del rebase y verifica después identidad, intención de la Issue y autoridad del *lease*.

### Por qué da problemas

1. **El paralelismo solo existe en la fase de implementación.** La integración es estrictamente serie y cada turno cuesta una validación entera. Para este repo eso son unos 5 minutos de tests por Issue, con el *gate* tomado. Con N Issues en paralelo, la última espera N validaciones, y cada una que entra obliga a las demás a rebasar y revalidar.
2. **El *gate* es local al proceso.** Entre Workers de máquinas distintas no hay coordinación: la única protección es que el `push` falle y el bucle de 5 reintentos, cada uno con rebase y validación completa.
3. **El repo está hecho para chocar.** Un tercio de los commits recientes tocan los mismos 6 ficheros. Dos Issues en paralelo sobre este proyecto conflictúan casi siempre, así que el camino «excepcional» de resolución de conflictos por IA es en realidad el camino habitual.
4. **Cualquier estado no previsto detiene el Worker.** Hay 192 puntos que lanzan `WorkerInfrastructureException`, y la política es «estado incierto → dejar de planificar y preservar». Es prudente por separado, pero sumado a lo anterior significa que cada combinación nueva (primer intento sin `RetryOf`, evidencia de padre ausente, GraphQL mal formado…) tumba el Worker y genera otra Issue de recuperación. Las Issues 491–504 son exactamente esa secuencia.
5. **La resolución de conflictos por IA dentro del rebase es la pieza más frágil.** Requiere verificar historia, índice, identidad del rebase, intención y *lease* antes y después; es donde se han concentrado los últimos fallos.

### Qué propongo

Por orden de impacto y de menor a mayor esfuerzo:

1. **Trocear los ficheros calientes** (sin cambiar comportamiento): traducciones por pantalla en vez de un `en.ts`/`es.ts` únicos, `WorkerV011Tests`/`CodexServerTests`/`GitWorktreeTests` en ficheros por área, `ExecutionRunner` separando integración de ejecución. Es lo que más reduce conflictos reales y no toca la lógica.
2. **Abaratar el turno de integración.** Tras el rebase, ejecutar la validación de riesgo (`tools/validate.py` ya existe) en lugar de la suite completa, y arreglar la velocidad de tests (apartado 3). Con una validación de 30–60 s el *gate* deja de ser un cuello de botella.
3. **Integrar mediante Pull Request en lugar de push directo a la base.** El Worker empuja la rama de la Issue y abre un PR; la fusión la hace GitHub (cola de fusión o auto-merge con checks). Elimina de golpe el *ff-only* local, la carrera entre Workers, el bucle de reintentos y gran parte de la «incertidumbre de push», y te deja un sitio donde ver y arreglar a mano un conflicto. Es un cambio de diseño: conviene decidirlo antes de seguir parcheando el flujo actual.
4. **Convertir el conflicto en un resultado normal, no en una recuperación.** Si el rebase conflictúa: abortar, etiquetar la Issue y reencolarla como reintento sobre la base nueva (el mecanismo `retryMode` ya existe). Dejar la resolución por IA como opción explícita, no como camino automático.
5. **Congelar la herencia de sesiones Codex entre Issues** (serie 33) hasta que lo anterior esté estable: añade un tercer mecanismo de serialización (por hilo) con beneficio no medido.

## 3. Tests

### Medición

`dotnet test CodexWorker.sln --no-build -m:1`: **2.798 casos, 290 s de reloj**. 2.797 pasan; el único fallo (`LocalRepositoryDiscoveryTests.ReadsLocalFetchAndPushUrlsWithoutNetwork` con URL SSH) se debe a la reescritura de URLs Git de este contenedor, no al código.

| Tramo | Tiempo |
| --- | --- |
| Todas las clases que corren en paralelo (≈100 clases, ≈1.900 casos) | ≈20 s |
| Colección `ServerTokenEnvironment` (38 clases, en serie) | ≈270 s |
| — `GitWorktreeTests` (165 casos, Git real) | 87 s |
| — `ManagedWorkerReadinessTests` (22 casos) | 78 s |
| — `StandaloneInterruptedExecutionTests` | 27 s |
| — `CodexServerTests` | 20 s |
| — `ManagedHttpsSecurityTests` (4 casos) | 18 s |

2.132 casos tardan menos de 0,1 s. Solo 69 casos tardan más de 1 s y suman 182 s.

### Diagnóstico

- **El problema no es el número de tests, sino dónde esperan.** Borrar tests rápidos no ahorra nada; las auditorías anteriores eliminaron 2 casos y no pudieron medir.
- **Causa raíz de la serialización:** el código de producción lee `CODEX_SERVER_MANAGEMENT_TOKEN`, `CODEX_WORKER_CODEX_EXECUTABLE`, `PATH`, `HOME`, etc. directamente del entorno del proceso, así que los tests los mutan con `Environment.SetEnvironmentVariable` (más de 200 llamadas) y deben ir en una única colección no paralela.
- **`ManagedWorkerReadinessTests` gasta 3,5 s por caso** arrancando el host real y esperando intervalos reales de heartbeat y reconexión. No usa `TimeProvider` aunque `AGENTS.md` lo exige.
- **`GitWorktreeTests`** lanza procesos `git` reales en cada caso; muchos casos son variaciones `[Theory]` del mismo escenario completo.
- **Los 30 tests de `tests/dashboard` y los 29 de `worker-poc/tests`** compilan componentes con esbuild y comprueban cadenas en el HTML o directamente en el código fuente. Rompen con cualquier cambio de texto y no protegen comportamiento. Los scripts `*-browser-review.cjs` están ya rotos: `home-browser-review.cjs` falla esperando un encabezado «Home» que la pantalla ya no tiene.

### Qué propongo

1. **Inyectar el entorno** (un `IEnvironment`/opciones leídas una vez en `Program`) en lugar de leerlo en el punto de uso. Permite quitar `ServerTokenEnvironment` y que las 38 clases corran en paralelo. Estimación: de 290 s a 90–100 s, limitado por `GitWorktreeTests`.
2. Mientras tanto, **separar en tres ensamblados** (`CodexServer.Tests`, `CodexWorker.Tests`, `CodexWorker.GitTests`) y quitar `-m:1`: los procesos no comparten entorno, así que se paralelizan sin tocar producción.
3. **`ManagedWorkerReadinessTests` con reloj falso**: de 78 s a pocos segundos.
4. **Reducir `GitWorktreeTests`**: dejar con Git real un caso por contrato (inicio, integración limpia, conflicto, recuperación, limpieza) y pasar las variaciones a tests con el `ProcessRunner` simulado.
5. **Borrar los tests de dashboard que comparan cadenas** y los *browser-review* rotos; sustituirlos por 5–6 pruebas Playwright de humo (una por pantalla) contra datos simulados.
6. Mantener la validación por riesgo como validación del Worker para este repo, con la suite completa solo en *release*.

Objetivo razonable: suite completa por debajo de 60 s.

## 4. Dashboard

### Estado

- Vive en `src/CodexServer/worker-poc`: nombre, `poc.css`, `WorkerPocPage` y `detail.jsx` (44 KB, sin tipos) son restos de la prueba de concepto y siguen siendo el núcleo de la pantalla de Worker.
- Mezcla tres sistemas de estilo: componentes Untitled UI con tokens, utilidades Tailwind y 31 KB de CSS propio con 137 clases y 22 colores fijos en hexadecimal. Además hay 25 controles HTML nativos (`<select>`, `<input>`, `<table>`) sin el estilo de los componentes; se ven en los filtros de Ejecuciones y de Home.
- El código es difícil de mantener incluso para un agente: el 13 % de las líneas TSX supera 160 caracteres y hay ficheros de 400 líneas con 50 KB.
- Cada pantalla lanza muchas lecturas independientes (70 `useApiRead`); si una falla, la pantalla muestra «Data unavailable» parcial sin decir qué falta.
- El Worker standalone tiene además otro dashboard distinto (`dashboard.html`, 17 KB de HTML embebido).

### El problema real: el contenido

Renderizado con datos de ejemplo, el dashboard enseña el modelo interno, no respuestas:

- Home: tres de cuatro tarjetas con «Unavailable» y notas como «Managed running requests + reported nonterminal work in bounded windows / known total execution slots».
- Detalle de Worker: cinco indicadores de estado (Connection, Scheduling, Execution readiness, Managed settings, Work slots), y debajo párrafos sobre qué revoca cada token, cuatro pasos de «preparación», capacidades «Unknown / Not checked» y una ejecución fallida cuya única explicación es «Recovery status: IntegrationUncertain».
- Ejecuciones: una tabla con identificadores internos y un botón «Execution Maintenance» que lleva a otra pantalla de 37 KB.

Ninguna pantalla contesta las tres preguntas que tiene quien lo abre: **¿está todo bien?, ¿qué está pasando ahora?, ¿qué tengo que hacer yo?**

### Qué propongo

Rediseñar el contenido antes que los estilos, con cuatro pantallas:

1. **Inicio** = una bandeja de «Requiere tu atención» (cada elemento con causa en una frase y un botón de acción: reintentar, descartar, ver en GitHub) + trabajo en curso + últimas completadas. Si no hay nada que atender, lo dice.
2. **Proyectos** = cola de Issues por proyecto y su estado (pendiente, en curso, integrada, bloqueada).
3. **Workers** = lista con un único estado por Worker (Trabajando / Libre / Necesita atención / Desconectado). Capacidades, aprovisionamiento, tokens y diario pasan a una pestaña «Diagnóstico».
4. **Ajustes**.

Y en lo técnico: un solo sistema de estilo (componentes Untitled UI + tokens; eliminar `poc.css`), renombrar `worker-poc` a `dashboard`, tipar o eliminar `detail.jsx`/`model.js`, y un endpoint agregado por pantalla en vez de 5–8 lecturas. Los estados de recuperación deben llegar al dashboard ya traducidos a «qué pasó / qué hacer», no como nombres de enumerado.

Puedo preparar una maqueta navegable de estas pantallas para que la apruebes antes de tocar código.

## 5. Documentación y proceso

- La documentación reproducía el patrón del código: cada Issue añadía su informe. Las cuatro «auditorías» de la serie 370.8 suman 700 líneas y declaran no haber podido compilar ni ejecutar tests.
- `AGENTS.md` pide a la vez «evitar complejidad» y preservar todo estado, toda compatibilidad y toda recuperación; en la práctica los agentes solo añaden. Conviene añadirle un presupuesto explícito: tamaño máximo de fichero, prohibición de documentos de «verificación» por Issue, y que una Issue de corrección deba simplificar o eliminar el camino que falló cuando sea posible.
- Las Issues son muy largas y prescriptivas (la 504 pide a la vez diagnosticar, arreglar, cubrir cinco escenarios y documentar). Issues pequeñas con un único criterio de aceptación producen cambios pequeños que conflictúan menos.
- No hay CI en GitHub: la única validación es la del propio Worker. Un workflow mínimo (build + tests) daría una señal independiente y es requisito para integrar por PR.

## 6. Qué se ha hecho en esta rama

**Limpieza**

- Archivados en [`docs/archive`](archive/README.md) 11 informes históricos y los directorios `benchmarks` y `performance`; enlaces actualizados y comprobados.
- Eliminado `tests/__pycache__`, que estaba versionado pese a estar en `.gitignore`.

**Tests (punto 1 del plan): de 290 s a 126 s, mismos 2.798 casos**

- Cada heartbeat del Worker dormía 1 s para muestrear la CPU (`WorkerHostResourceCollector`). Ahora la CPU se calcula entre dos recogidas consecutivas, sin esperar; el primer heartbeat no informa CPU. Era también un retraso real en producción. `ManagedWorkerReadinessTests` y `ManagedHttpsSecurityTests` pasan de 96 s a 11 s.
- El sondeo del planificador y el temporizador de heartbeat usan el `TimeProvider` inyectado.
- Los tests con Git real (`GitWorktreeTests`, `StandaloneInterruptedExecutionTests`) están en un ensamblado propio, `tests/CodexWorker.GitTests`, que corre en otro proceso en paralelo. Conservan el espacio de nombres, así que los filtros existentes siguen valiendo.
- `tools/validate.py` incluye el proyecto nuevo y, en modo completo, ejecuta los proyectos de test a la vez.

El límite actual lo marca `CodexWorker.GitTests` (≈2 min con Git real). Bajarlo más requiere el punto 5 del plan.

**Ficheros calientes (punto 2 del plan), sin cambios de comportamiento**

- Traducciones: `en.ts` y `es.ts` son ahora índices; el contenido está en un fichero por área en `locales/en/` y `locales/es/` (16 áreas).
- `WorkerV011Tests`, `CodexServerTests` y `GitWorktreeTests` están repartidos en clases parciales por área (de 220–290 KB por fichero a ≈30 KB de media). Los nombres completos de los tests no cambian.
- `ExecutionRunner` separa integración y validación (`ExecutionRunner.Integration.cs`, `ExecutionRunner.Validation.cs`); `Worker` separa reclamación de recuperaciones, informes e historial.

**Integración en paralelo (punto 3 del plan): vuelta al comportamiento de la v0.33.1**

- Eliminada la herencia de sesiones Codex entre issues dependientes (serie 33): cada issue arranca su propia sesión.
- Un primer intento ya no pide al motor que resuelva un conflicto de rebase ni verifica la «intención» de la issue contra GitHub: el conflicto se aborta, se conserva el trabajo, se informa y el Worker sigue.
- La recuperación automática standalone vuelve a reintentar solo la integración del commit conservado; la resolución con el motor queda como acción explícita.
- Nuevo: en un Worker standalone, la primera vez que una issue pierde el rebase vuelve sola a la etiqueta de lista y se reimplementa sobre la base actual. Solo una vez; si vuelve a chocar queda etiquetada para revisión.
- En modo gestionado el conflicto queda etiquetado y hay que volver a encolar la issue desde el Server: el reintento automático en ese modo requiere cambios en la cola del Server y no está hecho.
- Comportamiento documentado en [parallel-integration.md](parallel-integration.md).

**Pendiente conocido, no introducido aquí**

- `tests/dashboard/home-overview` tiene un caso que ya fallaba en `main` («share the same status label and tone»).
- `LocalRepositoryDiscoveryTests.ReadsLocalFetchAndPushUrlsWithoutNetwork` falla solo en entornos que reescriben URLs SSH de Git, como este contenedor.

## 7. Plan propuesto

| Orden | Trabajo | Efecto |
| --- | --- | --- |
| 1 | Hecho: ensamblado Git separado, heartbeat sin espera, validación completa en paralelo | Suite de 290 s a 126 s |
| 2 | Hecho: traducciones, tests gigantes, `ExecutionRunner` y `Worker` troceados | Menos conflictos entre Issues paralelas |
| 3 | Hecho en standalone: sin sesiones heredadas ni resolución automática; conflicto = reintento único. Pendiente: reintento automático en modo gestionado | Elimina la clase de fallos 491–504 |
| 4 | Maqueta y rediseño del dashboard por contenido | Dashboard útil y un solo estilo |
| 5 | Inyectar entorno; reducir `GitWorktreeTests`; sustituir tests de dashboard | Suite < 60 s |
| 6 | Revisar funcionalidades poco usadas (observadores, herencia de sesiones, MCP/OAuth, segundo motor) y retirar o congelar las que no uses | Menos estados que mantener |

Hasta completar 1–3 recomiendo no abrir Issues de funcionalidad nueva sobre este repo.
