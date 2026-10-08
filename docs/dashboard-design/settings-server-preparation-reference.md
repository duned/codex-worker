# Settings and Server preparation visual reference

The [Settings and Server preparation image](settings-server-preparation-reference.svg) is embedded in the dashboard design guide. This note preserves contract mappings; sample values are illustrative.

## Contract mapping

- Server readiness, capabilities and typed actions come from node, connection and provisioning command APIs.
- Credential administration exposes metadata such as provider, type, version and assignment. Secret payloads are never displayed.
- Node, command and connection freshness remain distinct; failed reads do not establish current readiness.
