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
  if (line.startsWith(':')) {
    return line.slice(1).trimStart();
  }

  return line;
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
  const labelsPattern = SECTION_LABELS.map(escapeRegExp).join('|');
  const inlineLabelRegex = new RegExp(`\\s+(${labelsPattern})\\s*:\\s*`, 'g');
  const lineStartLabelRegex = new RegExp(`(^|\\n)(${labelsPattern})\\s*:\\s*`, 'g');

  return text
    .replace(inlineLabelRegex, '\n\n$1: ')
    .replace(lineStartLabelRegex, '$1**$2:** ');
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
