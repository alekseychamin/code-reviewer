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

  return lines
    .map((line) => {
      const trimmed = line.trim();

      if (!trimmed) {
        return line;
      }

      if (trimmed.startsWith(':')) {
        const normalizedTail = trimmed.slice(1).trimStart();
        if (isListItemLine(lastMeaningfulLine)) {
          return `    ${normalizedTail}`;
        }

        return normalizedTail;
      }

      lastMeaningfulLine = trimmed;
      return line;
    })
    .join('\n');
}

function isListItemLine(line: string): boolean {
  return /^\d+\.\s+/.test(line) || /^[-*+]\s+/.test(line);
}
