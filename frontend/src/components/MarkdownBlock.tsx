import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface MarkdownBlockProps {
  content: string;
  emptyText: string;
  normalize?: boolean;
}

export function MarkdownBlock({ content, emptyText, normalize = true }: MarkdownBlockProps) {
  const normalizedContent = normalize ? normalizeMarkdownContent(content) : content.replace(/\r\n/g, '\n');

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
  const lines = normalizeFenceLayout(content.replace(/\r\n/g, '\n')).split('\n');
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

    const normalizedBrokenLabelLine = normalizeBrokenLabelLine(trimmed);
    if (normalizedBrokenLabelLine) {
      flushParagraph();
      normalizedLines.push(normalizedBrokenLabelLine);
      continue;
    }

    if (isStandaloneMarkdownBlock(trimmed)) {
      flushParagraph();
      normalizedLines.push(normalizeStandaloneMarkdownLine(trimmed));
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

function normalizeFenceLayout(content: string): string {
  const rawLines = content.split('\n');
  const normalizedLines: string[] = [];
  let insideFence = false;

  for (const rawLine of rawLines) {
    let line = rawLine;

    if (!insideFence) {
      const embeddedOpeningFenceMatch = line.match(/^(.*?)(```[A-Za-z0-9_-]*)(.*)$/);
      if (embeddedOpeningFenceMatch && !line.trimStart().startsWith('```')) {
        const [, before, fence, after] = embeddedOpeningFenceMatch;
        if (before.trim()) {
          normalizedLines.push(before.trimEnd());
        }

        normalizedLines.push(fence);
        insideFence = true;

        const trailingCode = after.trimStart();
        if (trailingCode) {
          normalizedLines.push(trailingCode);
        }

        continue;
      }

      const trimmed = line.trim();
      const inlineOpeningFenceMatch = trimmed.match(/^(```[A-Za-z0-9_-]*)(?:\s+)(.+)$/);
      if (inlineOpeningFenceMatch) {
        normalizedLines.push(inlineOpeningFenceMatch[1]);
        normalizedLines.push(inlineOpeningFenceMatch[2]);
        insideFence = true;
        continue;
      }

      normalizedLines.push(rawLine);
      if (trimmed.startsWith('```')) {
        insideFence = true;
      }

      continue;
    }

    const closingFenceIndex = line.indexOf('```');
    if (closingFenceIndex >= 0) {
      const beforeFence = line.slice(0, closingFenceIndex);
      const afterFence = line.slice(closingFenceIndex + 3).trimStart();

      if (beforeFence.length > 0) {
        normalizedLines.push(beforeFence);
      }

      normalizedLines.push('```');
      insideFence = false;

      if (afterFence) {
        normalizedLines.push(afterFence);
      }

      continue;
    }

    normalizedLines.push(rawLine);
  }

  return normalizedLines.join('\n');
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

function normalizeStandaloneMarkdownLine(line: string): string {
  return line
    .replace(/^(\d+\.\s+)\*\*\s*([^:*][^:]*?)\s*:\*\*\s*(.*)$/u, '$1**$2:** $3')
    .replace(/^([-*+]\s+)\*\*\s*([^:*][^:]*?)\s*:\*\*\s*(.*)$/u, '$1**$2:** $3')
    .replace(/^(\d+\.\s+)([^:*][^:]*?):\*\*\s+(.*)$/u, '$1**$2:** $3')
    .replace(/^([-*+]\s+)([^:*][^:]*?):\*\*\s+(.*)$/u, '$1**$2:** $3');
}

function normalizeBrokenLabelLine(line: string): string | null {
  const match = line.match(new RegExp(`^(${BROKEN_LABEL_PREFIXES.map(escapeRegExp).join('|')})(?:\\s*\\((.*?)\\))?(?::\\*\\*|::)\\s*(.*)$`, 'u'));
  if (!match) {
    return null;
  }

  const [, label, suffix, remainder] = match;
  const title = suffix ? `${label} (${suffix})` : label;
  const value = remainder.trim();
  return value ? `**${title}:** ${value}` : `**${title}:**`;
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
  'Проблема',
  'Пояснение к замечанию',
  'Пояснение к комментарию',
  'Рекомендации',
  'Что проверить и исправить',
  'Пример исправления',
  'Ответ на вопрос о публикации в TFS',
  'Рекомендуемое исправление',
  'Конкретные шаги для исправления'
] as const;

const BROKEN_LABEL_PREFIXES = [
  'Проблема',
  'Рекомендации',
  'Что проверить и исправить',
  'Пример исправления',
  'Ответ на вопрос о публикации в TFS'
] as const;
