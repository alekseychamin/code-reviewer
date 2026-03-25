import mermaid from 'mermaid';
import { useEffect, useId, useState } from 'react';

interface MermaidDiagramProps {
  chart: string;
}

mermaid.initialize({
  startOnLoad: false,
  securityLevel: 'loose',
  theme: 'neutral'
});

export function MermaidDiagram({ chart }: MermaidDiagramProps) {
  const id = useId().replace(/:/g, '-');
  const [svg, setSvg] = useState<string>('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;

    async function render(): Promise<void> {
      try {
        const candidates = [chart, normalizeMermaidChart(chart)];
        let lastError: unknown = null;

        for (let index = 0; index < candidates.length; index++) {
          try {
            const result = await mermaid.render(`diagram-${id}-${index}`, candidates[index]);
            if (!active) {
              return;
            }

            setSvg(result.svg);
            setError(index === 0 ? null : 'Диаграмма была автоматически скорректирована для рендера Mermaid.');
            return;
          } catch (reason) {
            lastError = reason;
          }
        }

        if (!active) {
          return;
        }

        setError(lastError instanceof Error ? lastError.message : String(lastError));
        setSvg('');
      } catch (reason) {
        if (!active) {
          return;
        }

        setError(reason instanceof Error ? reason.message : String(reason));
        setSvg('');
      }
    }

    void render();

    return () => {
      active = false;
    };
  }, [chart, id]);

  if (error) {
    return (
      <div className="diagram-fallback">
        <p>Не удалось отрисовать Mermaid-диаграмму.</p>
        <pre>{chart}</pre>
      </div>
    );
  }

  if (!svg) {
    return <div className="empty-state compact">Отрисовка диаграммы...</div>;
  }

  return <div className="mermaid-diagram" dangerouslySetInnerHTML={{ __html: svg }} />;
}

function normalizeMermaidChart(chart: string): string {
  const stripped = chart
    .replace(/```mermaid/gi, '')
    .replace(/```/g, '')
    .trim();

  const sanitized = stripped
    .replace(/\[\]/g, '()')
    .replace(/<br\s*\/?>/gi, '<br/>');

  return sanitized
    .split('\n')
    .map((line) => normalizeMermaidLine(line))
    .join('\n');
}

function normalizeMermaidLine(line: string): string {
  const trimmed = line.trim();
  if (!trimmed ||
      trimmed.startsWith('flowchart') ||
      trimmed.startsWith('graph') ||
      trimmed.startsWith('subgraph') ||
      trimmed === 'end' ||
      trimmed.startsWith('style ') ||
      trimmed.startsWith('classDef ') ||
      trimmed.startsWith('class ') ||
      trimmed.startsWith('linkStyle ') ||
      trimmed.startsWith('%%')) {
    return line;
  }

  return line.replace(
    /\b([A-Za-z][A-Za-z0-9_]*)\s*\[\((.*?)\)\]|\b([A-Za-z][A-Za-z0-9_]*)\s*\[(.*?)\]/g,
    (_, cylinderId: string, cylinderLabel: string, boxId: string, boxLabel: string) => {
      const nodeId = cylinderId || boxId;
      const label = sanitizeNodeLabel(cylinderLabel || boxLabel);
      return `${nodeId}["${label}"]`;
    });
}

function sanitizeNodeLabel(label: string): string {
  return label
    .replace(/\[\]/g, '()')
    .replace(/\[/g, '(')
    .replace(/\]/g, ')')
    .replace(/"/g, "'")
    .trim();
}
