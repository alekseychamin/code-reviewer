import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface MarkdownBlockProps {
  content: string;
  emptyText: string;
}

export function MarkdownBlock({ content, emptyText }: MarkdownBlockProps) {
  const normalizedContent = normalizeMarkdownContent(content);

  if (!normalizedContent.trim()) {
    return <div className="empty-state compact">{emptyText}</div>;
  }

  return (
    <div className="markdown-content">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{normalizedContent}</ReactMarkdown>
    </div>
  );
}

function normalizeMarkdownContent(content: string): string {
  const lines = content.replace(/\r\n/g, '\n').split('\n');
  const normalizedLines: string[] = [];
  let paragraphBuffer = '';
  let insideCodeFence = false;

  function flushParagraph(): void {
    if (paragraphBuffer.trim()) {
      normalizedLines.push(paragraphBuffer.trim());
      paragraphBuffer = '';
    }
  }

  for (const rawLine of lines) {
    const line = rawLine.replace(/\s+$/, '');
    const trimmed = line.trim();

    if (trimmed.startsWith('```')) {
      flushParagraph();
      normalizedLines.push(trimmed);
      insideCodeFence = !insideCodeFence;
      continue;
    }

    if (insideCodeFence) {
      normalizedLines.push(rawLine);
      continue;
    }

    if (!trimmed) {
      flushParagraph();
      if (normalizedLines.at(-1) !== '') {
        normalizedLines.push('');
      }

      continue;
    }

    if (isStandaloneFormattingArtifact(trimmed)) {
      flushParagraph();
      continue;
    }

    if (isStandaloneMarkdownBlock(trimmed)) {
      flushParagraph();
      normalizedLines.push(trimmed);
      continue;
    }

    const normalizedLine = normalizeContinuationLine(trimmed);
    paragraphBuffer = paragraphBuffer
      ? `${paragraphBuffer.replace(/\s+$/, '')} ${normalizedLine}`
      : normalizedLine;
  }

  flushParagraph();

  return normalizeSectionLabels(normalizedLines.join('\n'));
}

function normalizeContinuationLine(line: string): string {
  const cleanedLine = stripDanglingInlineFormatting(line);

  if (cleanedLine.startsWith(':')) {
    return cleanedLine.slice(1).trimStart();
  }

  return cleanedLine;
}

function isStandaloneMarkdownBlock(line: string): boolean {
  return (
    /^(#{1,6})\s+/.test(line) ||
    /^>\s?/.test(line) ||
    /^[-*+]\s+/.test(line) ||
    /^\d+\.\s+/.test(line) ||
    /^\|.*\|$/.test(line) ||
    /^---+$/.test(line)
  );
}

function normalizeSectionLabels(markdown: string): string {
  const segments = markdown.split(/(```[\s\S]*?```)/g);
  return segments
    .map((segment) => (segment.startsWith('```') ? segment : normalizeLabelsInText(segment)))
    .join('');
}

function normalizeLabelsInText(text: string): string {
  const cleanedText = stripFormattingArtifacts(text);
  const labelsPattern = SECTION_LABELS.map(escapeRegExp).join('|');
  const labelRegex = new RegExp(`(${labelsPattern})\\s*:\\s*`, 'g');
  const matches = [...cleanedText.matchAll(labelRegex)];

  if (matches.length === 0) {
    return cleanedText;
  }

  const parts: string[] = [];
  const prefix = cleanedText.slice(0, matches[0].index ?? 0).trim();
  if (prefix && !isStandaloneFormattingArtifact(prefix)) {
    parts.push(prefix);
  }

  for (let index = 0; index < matches.length; index += 1) {
    const match = matches[index];
    const nextMatch = matches[index + 1];
    const label = match[1];
    const valueStart = (match.index ?? 0) + match[0].length;
    const valueEnd = nextMatch?.index ?? cleanedText.length;
    const value = stripEdgeFormattingArtifacts(cleanedText.slice(valueStart, valueEnd).trim());

    parts.push(formatSectionBlock(label, value));
  }

  return parts.join('\n\n');
}

function formatSectionBlock(label: string, value: string): string {
  if (!value) {
    return `**${label}:**`;
  }

  if (isListBlock(value)) {
    return `**${label}:**\n\n${value}`;
  }

  return `**${label}:** ${value}`.trim();
}

function stripFormattingArtifacts(text: string): string {
  return text
    .replace(/(^|\n)\s*(\*\*|__)\s*(?=\n|$)/g, '$1')
    .replace(/\*\*(?=\s*(?:Категория|Описание|Краткое описание|Затронутые модули|Ключевые модули|Что улучшилось|Что можно улучшить дальше|Конкретная проблема|Конкретные проблемы|Конкретный риск|Пояснение к замечанию|Пояснение к комментарию|Рекомендуемое исправление|Конкретные шаги для исправления)\s*:)/g, '')
    .replace(/__(?=\s*(?:Категория|Описание|Краткое описание|Затронутые модули|Ключевые модули|Что улучшилось|Что можно улучшить дальше|Конкретная проблема|Конкретные проблемы|Конкретный риск|Пояснение к замечанию|Пояснение к комментарию|Рекомендуемое исправление|Конкретные шаги для исправления)\s*:)/g, '');
}

function stripEdgeFormattingArtifacts(text: string): string {
  return stripDanglingInlineFormatting(
    text
    .replace(/^(?:\*\*|__)\s*/, '')
    .replace(/\s*(?:\*\*|__)$/, '')
    .trim()
  );
}

function isStandaloneFormattingArtifact(text: string): boolean {
  return /^(?:\*\*|__|[*_]{3,})$/.test(text.trim());
}

function isListBlock(text: string): boolean {
  return /^([-*+]\s+|\d+\.\s+)/.test(text.trim());
}

function stripDanglingInlineFormatting(text: string): string {
  return text
    .replace(/(^|[\s(])(?:\*\*|__)(?=\S)/g, '$1')
    .replace(/(?<![:*_\s])(?:\*\*|__)(?=$|[\s).,;!?])/g, '');
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

const SECTION_LABELS = [
  'Категория',
  'Описание',
  'Краткое описание',
  'Затронутые модули',
  'Ключевые модули',
  'Что улучшилось',
  'Что можно улучшить дальше',
  'Конкретная проблема',
  'Конкретные проблемы',
  'Конкретный риск',
  'Пояснение к замечанию',
  'Пояснение к комментарию',
  'Рекомендуемое исправление',
  'Конкретные шаги для исправления'
] as const;
