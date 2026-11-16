export const projects = {
  "projects.title": "Projects", "projects.description": "Repositories whose Issues the Server hands out to Workers.", "projects.add": "Add project",
  "projects.search": "Search by name or repository", "projects.active": "Active", "projects.paused": "Paused", "projects.working": "Working",
  "projects.project": "Project", "projects.repository": "Repository", "projects.running": "Running", "projects.queued": "Queued",
  "projects.none": "No projects yet. Add one to start sending Issues to Workers.", "projects.edit": "Edit project", "projects.saved": "Project saved.",
  "projects.created": "Project created.", "projects.name": "Name", "projects.repositoryHint": "GitHub repository as owner/repository.",
  "projects.baseBranch": "Base branch", "projects.baseBranchHint": "Finished Issues are integrated here.", "projects.parallel": "Issues in parallel",
  "projects.parallelHint": "How many Issues of this project can be implemented at the same time.", "projects.validation": "Validation commands",
  "projects.validationHint": "One per line. They run before integrating; if one fails, nothing is integrated. Empty uses the Worker's own configuration.",
  "projects.readyLabel": "Ready label", "projects.readyLabelHint": "Issues with this label are ready to be implemented.",
  "projects.automatic": "Pick up Issues labelled {label} automatically", "projects.descriptionField": "Description",
  "projects.integratesInto": "integrates into", "projects.pause": "Pause project", "projects.resume": "Resume project",
  "projects.pausedDone": "Project paused. Running work finishes; nothing new starts.", "projects.resumed": "Project resumed.",
  "projects.configuration": "Configuration", "projects.upTo": "up to {n}", "projects.workerValidation": "Worker configuration",
  "projects.pickup": "Pick-up", "projects.pickupAutomatic": "Automatic: Issues labelled {label}", "projects.pickupManual": "Manual: only Issues you queue",
  "projects.anyWorker": "Any eligible Worker"
} as const;
