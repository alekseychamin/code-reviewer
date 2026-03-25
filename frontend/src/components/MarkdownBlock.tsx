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
  let lastMeaningfulLine = '';
  const normalizedLines: string[] = [];

  for (const line of lines) {
    const trimmed = line.trim();

    if (!trimmed) {
      normalizedLines.push(line);
      continue;
    }

    if (trimmed.startsWith(':')) {
      const normalizedTail = trimmed.slice(1).trimStart();
      if (normalizedLines.length > 0) {
        const previousIndex = findPreviousMeaningfulLineIndex(normalizedLines);
        if (previousIndex >= 0) {
          const previousLine = normalizedLines[previousIndex].replace(/\s+$/, '');
          const separator = previousLine.endsWith(':') ? ' ' : ': ';
          normalizedLines[previousIndex] = `${previousLine}${separator}${normalizedTail}`;
          lastMeaningfulLine = normalizedLines[previousIndex].trim();
          continue;
        }
      }

      normalizedLines.push(normalizedTail);
      lastMeaningfulLine = normalizedTail;
      continue;
    }

    normalizedLines.push(line);
    lastMeaningfulLine = trimmed;
  }

  return normalizedLines.join('\n');
}

function isListItemLine(line: string): boolean {
  return /^\d+\.\s+/.test(line) || /^[-*+]\s+/.test(line);
}

function findPreviousMeaningfulLineIndex(lines: string[]): number {
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    if (lines[index].trim()) {
      return index;
    }
  }

  return -1;
}
