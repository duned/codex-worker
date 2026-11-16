import { record } from '../../shared/api/validation';
export interface TelegramTest { id: string; version: string; status: string; deadlineUtc: string; diagnostic?: string }
export function telegramTest(value: unknown): TelegramTest | null {
  if (value === null) return null;
  const item = record(value);
  if (typeof item.id !== 'string' || !/^[a-f0-9]{32}$/.test(item.id) || typeof item.version !== 'string' ||
    !['pending', 'running', 'succeeded', 'failed'].includes(String(item.status)) || typeof item.deadlineUtc !== 'string' ||
    (item.diagnostic != null && !['accepted', 'telegram-rejected', 'delivery-unavailable', 'configuration-changed'].includes(String(item.diagnostic))))
    throw new Error('Invalid Telegram test result.');
  return item as unknown as TelegramTest;
}
