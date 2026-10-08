# Projects visual references

The [Projects list](projects-list-reference.svg) and [Project detail](project-detail-reference.svg) images are embedded in the dashboard design guide. This note preserves API field mappings; sample values are illustrative.

## API fields represented

- Project definitions: `id`, `name`, `repository`, `defaultBranch`, `description`, `revision`, `enabled`, labels, requirements and nullable `automaticDiscovery` policy.
- Project executions: bounded `/api/v1/executions?projectId=...` results; show only supplied timestamps, worker IDs and work references.
- Issue and Worker details come from their existing project Issue and Worker observation endpoints. These observations are not scheduling authority.
