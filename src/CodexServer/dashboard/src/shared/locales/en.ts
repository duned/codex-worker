// One file per screen area keeps unrelated changes from touching the same file.
import { api } from './en/api';
import { common } from './en/common';
import { home } from './en/home';
import { projects } from './en/projects';
import { issues } from './en/issues';
import { executions } from './en/executions';
import { workers } from './en/workers';
import { settings } from './en/settings';
export const en = { ...api, ...common, ...home, ...projects, ...issues, ...executions, ...workers, ...settings } as const;
export type TranslationKey = keyof typeof en;
