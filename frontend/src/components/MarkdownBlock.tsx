import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface MarkdownBlockProps {
  content: string;
  emptyText: string;
}

export function MarkdownBlock({ content, emptyText }: MarkdownBlockProps) {
  if (!content.trim()) {
    return <div className="empty-state compact">{emptyText}</div>;
  }

  return (
    <div className="markdown-content">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{content}</ReactMarkdown>
    </div>
  );
}
