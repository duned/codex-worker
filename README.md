# Codex Server and Worker

Codex Worker executes GitHub Issues in isolated Git worktrees, runs configured validation, and owns commits, integration and GitHub reporting. Codex Server provides central project administration, Worker enrollment, assignments and leases. Both target .NET 10; Linux release archives are self-contained.

Start with the [operator guide](docs/operations.md) to choose standalone or Server-managed operation, configure services and understand communication and recovery.

- [Linux installation](docs/linux-installation.md): installation, enrollment, service accounts, paths and removal.
- [HTTPS deployment](docs/remote-https-deployment.md): separate hosts, TLS proxy and dashboard authentication.
- [Updates](docs/self-update.md): supported product commands and automation contracts.
- [Server administration](docs/server-local-administration.md) and [Worker administration](docs/worker-local-administration.md): configuration, credentials, provisioning and diagnostics.
- [Remote MCP](docs/server-mcp.md): read-only Server tools, bearer authentication and supported client setup.
- [Execution maintenance](docs/execution-maintenance.md): drain, inspect and safely recover retained work.

Contributors: [development](docs/development.md), [engineering instructions](AGENTS.md), [dashboard design](docs/dashboard-design-guide.md), [release packaging](docs/release-packaging.md), [changelog](CHANGELOG.md), and [documentation inventory](docs/documentation-inventory.md).
