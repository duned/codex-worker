const blockStart = /^(?: {0,3}(?:#{1,6}(?:\s|$)|```|~~~|>|[-+*]\s+|\d+[.)]\s+))/;
const fenceStart = /^ {0,3}(`{3,}|~{3,})(.*)$/;
const headingStart = /^ {0,3}(#{1,6})(?:\s+|$)(.*?)\s*#*\s*$/;
const unorderedItem = /^ {0,3}([-+*])\s+(.*)$/;
const orderedItem = /^ {0,3}(\d+)[.)]\s+(.*)$/;

export function safeMarkdownHref(destination) {
  const value = destination.trim();
  if (!value || /[\u0000-\u001f\u007f\\]/.test(value) || value.startsWith('//')) return undefined;
  if (/^(?:#|\/(?!\/)|\.\.?\/|\?|[^:/?#]+(?:[/?#]|$))/.test(value) && !/^[a-z][a-z\d+.-]*:/i.test(value)) return value;
  try {
    const url = new URL(value);
    return ['https:', 'http:', 'mailto:'].includes(url.protocol) ? url.href : undefined;
  } catch {
    return undefined;
  }
}

export function parseIssueMarkdown(source) {
  const lines = String(source ?? '').replace(/\r\n?/g, '\n').split('\n');
  return parseBlocks(lines);
}

function parseBlocks(lines) {
  const blocks = [];
  for (let index = 0; index < lines.length;) {
    const line = lines[index];
    if (!line.trim()) { index++; continue; }

    const fence = line.match(fenceStart);
    if (fence) {
      const marker = fence[1];
      const body = [];
      index++;
      while (index < lines.length && !new RegExp(`^ {0,3}${marker[0]}{${marker.length},}\\s*$`).test(lines[index])) body.push(lines[index++]);
      if (index < lines.length) index++;
      blocks.push({ type: 'code', language: fence[2].trim().split(/\s+/, 1)[0] || undefined, value: body.join('\n') });
      continue;
    }

    const heading = line.match(headingStart);
    if (heading) {
      blocks.push({ type: 'heading', depth: heading[1].length, children: parseInline(heading[2]) });
      index++;
      continue;
    }

    if (/^ {0,3}>/.test(line)) {
      const quote = [];
      while (index < lines.length && /^ {0,3}>/.test(lines[index])) quote.push(lines[index++].replace(/^ {0,3}> ?/, ''));
      blocks.push({ type: 'quote', children: parseBlocks(quote) });
      continue;
    }

    const firstListItem = line.match(unorderedItem) ?? line.match(orderedItem);
    if (firstListItem) {
      const ordered = !!line.match(orderedItem);
      const items = [];
      while (index < lines.length) {
        const match = lines[index].match(ordered ? orderedItem : unorderedItem);
        if (!match) break;
        const parts = [match[2]];
        index++;
        while (index < lines.length && /^ {2,}\S/.test(lines[index]) && !unorderedItem.test(lines[index]) && !orderedItem.test(lines[index])) parts.push(lines[index++].trim());
        items.push(parseInline(parts.join('\n')));
      }
      blocks.push({ type: 'list', ordered, items });
      continue;
    }

    const paragraph = [line];
    index++;
    while (index < lines.length && lines[index].trim() && !blockStart.test(lines[index])) paragraph.push(lines[index++]);
    blocks.push({ type: 'paragraph', children: parseInline(paragraph.join('\n')) });
  }
  return blocks;
}

function parseInline(source) {
  const tokens = [];
  const syntax = /(`+)(.+?)\1|!\[([^\]]*)\]\(([^)]+)\)|\[([^\]]+)\]\(([^)]+)\)|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|\*(.+?)\*|_(.+?)_|\n/g;
  let start = 0;
  for (const match of source.matchAll(syntax)) {
    const offset = match.index ?? 0;
    if (offset > start) tokens.push({ type: 'text', value: source.slice(start, offset) });
    if (match[1]) tokens.push({ type: 'code', value: match[2] });
    else if (match[3] !== undefined) tokens.push({ type: 'imageAlt', value: match[3] });
    else if (match[5] !== undefined) {
      const href = safeMarkdownHref(match[6]);
      tokens.push(href ? { type: 'link', href, children: parseInline(match[5]) } : { type: 'text', value: match[5] });
    } else if (match[7] !== undefined || match[8] !== undefined) tokens.push({ type: 'strong', children: parseInline(match[7] ?? match[8]) });
    else if (match[9] !== undefined) tokens.push({ type: 'strike', children: parseInline(match[9]) });
    else if (match[10] !== undefined || match[11] !== undefined) tokens.push({ type: 'emphasis', children: parseInline(match[10] ?? match[11]) });
    else tokens.push({ type: 'break' });
    start = offset + match[0].length;
  }
  if (start < source.length) tokens.push({ type: 'text', value: source.slice(start) });
  return tokens;
}
