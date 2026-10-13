export const executionStages = [
  { id: 'claimed', label: 'Claimed' },
  { id: 'preparing', label: 'Preparing' },
  { id: 'codex', label: 'Codex' },
  { id: 'validation', label: 'Validating' },
  { id: 'integration', label: 'Integrating' }
];

const stageAliases = new Map([
  ['claim', 'claimed'], ['claiming', 'claimed'], ['claimed', 'claimed'],
  ['preparation', 'preparing'], ['preparing', 'preparing'],
  ['codex', 'codex'], ['implementing', 'codex'],
  ['validation', 'validation'], ['validating', 'validation'],
  ['integration', 'integration'], ['integrating', 'integration']
]);

const completedBefore = new Map([
  ['claimed', []],
  ['preparing', ['claimed']],
  ['codex', ['claimed', 'preparing']],
  ['validation', ['claimed', 'preparing', 'codex']],
  // Integration recovery can transition directly from Preparing to Integrating.
  // Without stage history, Codex and Validation cannot be claimed as completed.
  ['integration', ['claimed', 'preparing']]
]);

export function executionTimeline(currentStage) {
  const normalized = String(currentStage ?? '').trim().toLowerCase();
  const currentId = stageAliases.get(normalized);
  const currentIndex = executionStages.findIndex(stage => stage.id === currentId);
  const completed = new Set(currentId ? completedBefore.get(currentId) : []);

  return executionStages.map((stage, index) => {
    const state = currentIndex < 0 ? 'unknown'
      : completed.has(stage.id) ? 'completed'
        : stage.id === currentId ? 'current'
          : index > currentIndex ? 'pending' : 'unknown';
    const previous = executionStages[index - 1];
    const connectorComplete = !!previous &&
      ((state === 'completed' && completed.has(previous.id)) ||
        (state === 'current' && completed.has(previous.id)));
    return { ...stage, state, connectorComplete };
  });
}
