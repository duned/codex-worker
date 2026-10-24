import { parseIssueMarkdown } from './issue-markdown.mjs';

type InlineToken = { type: string; value?: string; href?: string; children?: InlineToken[] };
type MarkdownBlock = { type: string; depth?: number; ordered?: boolean; language?: string; value?: string; children?: InlineToken[] | MarkdownBlock[]; items?: InlineToken[][] };

function InlineContent({ tokens }: { tokens: InlineToken[] }) {
  return <>{tokens.map((token, index) => {
    if (token.type === 'code') return <code key={index} className="rounded bg-secondary px-1 py-0.5 font-mono text-[0.9em] text-primary">{token.value}</code>;
    if (token.type === 'link') return <a key={index} href={token.href} className="text-brand-secondary underline underline-offset-2">{token.children && <InlineContent tokens={token.children} />}</a>;
    if (token.type === 'strong') return <strong key={index} className="font-semibold text-primary"><InlineContent tokens={token.children ?? []} /></strong>;
    if (token.type === 'emphasis') return <em key={index}><InlineContent tokens={token.children ?? []} /></em>;
    if (token.type === 'strike') return <del key={index}><InlineContent tokens={token.children ?? []} /></del>;
    if (token.type === 'break') return <br key={index} />;
    if (token.type === 'imageAlt') return <span key={index} className="italic text-tertiary">{token.value ? `[Image: ${token.value}]` : '[Image]'}</span>;
    return <span key={index}>{token.value}</span>;
  })}</>;
}

function MarkdownBlocks({ blocks }: { blocks: MarkdownBlock[] }) {
  return <div className="space-y-4 break-words text-sm leading-6 text-secondary">{blocks.map((block, index) => {
    if (block.type === 'heading') {
      const content = <InlineContent tokens={block.children as InlineToken[] ?? []} />;
      if (block.depth === 1) return <h3 key={index} className="text-xl font-semibold text-primary">{content}</h3>;
      if (block.depth === 2) return <h4 key={index} className="text-lg font-semibold text-primary">{content}</h4>;
      return <h5 key={index} className="text-base font-semibold text-primary">{content}</h5>;
    }
    if (block.type === 'paragraph') return <p key={index}><InlineContent tokens={block.children as InlineToken[] ?? []} /></p>;
    if (block.type === 'code') return <pre key={index} className="max-w-full overflow-x-auto rounded-lg bg-secondary p-4 font-mono text-xs leading-5 text-primary"><code>{block.value}</code></pre>;
    if (block.type === 'quote') return <blockquote key={index} className="border-l-2 border-brand pl-4 text-tertiary"><MarkdownBlocks blocks={block.children as MarkdownBlock[] ?? []} /></blockquote>;
    if (block.type === 'list') {
      const List = block.ordered ? 'ol' : 'ul';
      return <List key={index} className={`${block.ordered ? 'list-decimal' : 'list-disc'} space-y-1 pl-6`}>{(block.items ?? []).map((item, itemIndex) => <li key={itemIndex}><InlineContent tokens={item} /></li>)}</List>;
    }
    return null;
  })}</div>;
}

export function IssueMarkdown({ source }: { source: string }) {
  return <MarkdownBlocks blocks={parseIssueMarkdown(source) as MarkdownBlock[]} />;
}
