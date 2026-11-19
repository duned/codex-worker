// One file per screen area keeps unrelated changes from touching the same file.
import { api } from './en/api';
import { conflicts } from './en/conflicts';
import { executions } from './en/executions';
import { home } from './en/home';
import { issues } from './en/issues';
import { maintenance } from './en/maintenance';
import { nodes } from './en/nodes';
import { observers } from './en/observers';
import { projects } from './en/projects';
import { provisioning } from './en/provisioning';
import { server } from './en/server';
import { settings } from './en/settings';
import { shared } from './en/shared';
import { status } from './en/status';
import { workers } from './en/workers';
export const en = { ...api, ...conflicts, ...executions, ...home, ...issues, ...maintenance, ...nodes, ...observers, ...projects, ...provisioning, ...server, ...settings, ...shared, ...status, ...workers } as const;
export type TranslationKey = keyof typeof en;
