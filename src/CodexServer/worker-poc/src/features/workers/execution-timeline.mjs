export const executionStages = [
  { id: 'claimed', label: 'Claimed' },
  { id: 'preparing', label: 'Preparing' },
  { id: 'codex', label: 'Codex' },
  { id: 'validation', label: 'Validating' },
  { id: 'integration', label: 'Integrating' },
  { id: 'reporting', label: 'Reporting' }
];

const stageAliases = new Map([
  ['claim', 'claimed'], ['claiming', 'claimed'], ['claimed', 'claimed'],
  ['preparation', 'preparing'], ['preparing', 'preparing'],
  ['codex', 'codex'], ['implementing', 'codex'],
  ['validation', 'validation'], ['validating', 'validation'],
  ['integration', 'integration'], ['integrating', 'integration'],
  ['reporting', 'reporting']
]);

export function formatStageDuration(startedAtMilliseconds, endedAtMilliseconds) {
  if (!Number.isFinite(startedAtMilliseconds) || !Number.isFinite(endedAtMilliseconds) ||
      endedAtMilliseconds < startedAtMilliseconds) return null;
  const seconds = Math.floor((endedAtMilliseconds - startedAtMilliseconds) / 1000);
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

function stageTimestamp(stageStartedAtUtc, stageId) {
  const value = stageStartedAtUtc?.[stageId];
  const timestamp = Date.parse(typeof value === 'string' ? value : '');
  return Number.isFinite(timestamp) ? timestamp : null;
}

export function executionTimeline(currentStage, stageStartedAtUtc = {}, now = Date.now()) {
  const normalized = String(currentStage ?? '').trim().toLowerCase();
  const currentId = stageAliases.get(normalized);
  const currentIndex = executionStages.findIndex(stage => stage.id === currentId);

  return executionStages.map((stage, index) => {
    const state = currentIndex < 0 ? 'unknown'
      : index < currentIndex ? 'completed'
        : stage.id === currentId ? 'current' : 'pending';
    const previous = executionStages[index - 1];
    const connectorComplete = !!previous && (state === 'completed' || state === 'current');
    const startsAt = stageTimestamp(stageStartedAtUtc, stage.id);
    const nextStartsAt = stageTimestamp(stageStartedAtUtc, executionStages[index + 1]?.id);
    const endsAt = state === 'current' ? now : nextStartsAt;
    const duration = state === 'completed' || state === 'current'
      ? startsAt === null ? null : formatStageDuration(startsAt, endsAt)
      : null;
    return { ...stage, state, connectorComplete, duration };
  });
}
