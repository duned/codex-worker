export const home = {
  "home.title": "Home", "home.summary": "{online} of {workers} Workers online · {active} running · {queued} queued.",
  "home.attentionCount": "{n} things need your attention.", "home.allGood": "Everything is working.", "home.restOk": "Everything else is working normally.",
  "home.attention": "Needs your attention", "home.issueFailed": "{issue} did not finish in {project}", "home.workerCannotWork": "{worker} cannot take work",
  "home.group.paused": "Projects paused on a Worker", "home.group.maintenance": "Restarts and updates not resolved", "home.group.clock": "Worker clocks out of sync",
  "home.group.uncertain": "Executions with an unknown outcome", "home.group.ineligible": "Queued but cannot be assigned",
  "home.group.failed": "Issues that did not finish", "home.group.workers": "Workers that cannot take work",
  "home.projectPaused": "{project} is paused on {worker}", "home.maintenanceUnresolved": "{worker}: a restart or update did not finish",
  "home.executionUncertain": "{issue} in {project}: the Worker stopped reporting", "home.executionUncertainDetail": "It keeps a project slot until you resolve it, so with one slot the whole project waits.",
  "home.queuedBlocked": "{issue} is queued in {project} but cannot be assigned", "home.queuedBlockedDetail": "The Issue does not meet the conditions to run.",
  "home.running": "Running now", "home.allExecutions": "All executions", "home.nothingRunning": "Nothing is running. Workers pick up the next queued Issue as soon as they are free.",
  "home.finished": "Recently finished", "home.withProblems": "With problems"
} as const;
