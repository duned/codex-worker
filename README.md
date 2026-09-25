# codex-worker
Un worker para integrar un proyecto, con github issues y con codex distribuido

## V0.1

START
  │
  ▼
Leer project.yml
  │
  ▼
Buscar Issue con codex-ready
  │
  ├── ninguna → esperar
  │
  ▼
Escoger la más antigua
  │
  ▼
codex-ready → codex-working
  │
  ▼
git checkout main
git pull
  │
  ▼
crear feature/<issue>-<slug>
  │
  ▼
codex exec
  │
  ▼
resultado estructurado
  │
  ├── SUCCESS
  │      ↓
  │   validaciones
  │      ↓
  │   commit
  │      ↓
  │   merge main
  │      ↓
  │   push main
  │      ↓
  │   done/feature/... origin
  │      ↓
  │   codex-done + cerrar Issue
  │
  ├── BLOCKED
  │      ↓
  │   codex-blocked + comentario
  │
  └── FAILED
         ↓
      codex-failed + comentario

             ↓
          Telegram
             ↓
       siguiente Issue
