// One file per screen area keeps unrelated changes from touching the same file.
import type { TranslationKey } from './en';
import { api } from './es/api';
import { common } from './es/common';
import { home } from './es/home';
import { projects } from './es/projects';
import { issues } from './es/issues';
import { executions } from './es/executions';
import { workers } from './es/workers';
import { settings } from './es/settings';
export const es = { ...api, ...common, ...home, ...projects, ...issues, ...executions, ...workers, ...settings } satisfies Record<TranslationKey, string>;
