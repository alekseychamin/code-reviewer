import mermaid from 'mermaid';
import { useEffect, useRef, useState } from 'react';

interface MermaidDiagramProps {
  chart: string;
}

mermaid.initialize({
  startOnLoad: false,
  securityLevel: 'loose',
  theme: 'neutral'
});

export function MermaidDiagram({ chart }: MermaidDiagramProps) {
  const renderAttemptRef = useRef(0);
  const [svg, setSvg] = useState<string>('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;

    async function render(): Promise<void> {
      try {
        const renderAttempt = ++renderAttemptRef.current;
        const candidates = Array.from(new Set([chart.trim(), normalizeMermaidChart(chart)]))
          .filter((candidate) => candidate.length > 0);
        let lastError: unknown = null;

        setSvg('');
        setError(null);

        for (let index = 0; index < candidates.length; index++) {
          try {
            const result = await mermaid.render(buildRenderId(renderAttempt, index), candidates[index]);
            if (!active) {
              return;
            }

            setSvg(result.svg);
            setError(null);
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
  }, [chart]);

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

function buildRenderId(renderAttempt: number, candidateIndex: number): string {
  const randomPart = Math.random().toString(36).slice(2);
  return `diagram-${Date.now()}-${renderAttempt}-${candidateIndex}-${randomPart}`;
}

function normalizeMermaidChart(chart: string): string {
  const stripped = chart
    .replace(/```mermaid/gi, '')
    .replace(/```/g, '')
    .trim();

  const withJsonEscapes = stripped
    .replace(/\\r\\n/g, '<br/>')
    .replace(/\\n/g, '<br/>')
    .replace(/\\r/g, '<br/>')
    .replace(/\\t/g, ' ');

  const sanitized = withJsonEscapes
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

  const withLegacyTextEdges = normalizeLegacyQuotedTextEdges(line);
  const withNormalizedEdgeLabel = normalizeMermaidEdgeLabel(withLegacyTextEdges);
  return withNormalizedEdgeLabel.replace(
    /\b([A-Za-z][A-Za-z0-9_]*)\s*\[\((.*?)\)\]|\b([A-Za-z][A-Za-z0-9_]*)\s*\[(.*?)\]/g,
    (_, cylinderId: string, cylinderLabel: string, boxId: string, boxLabel: string) => {
      const nodeId = cylinderId || boxId;
      const label = sanitizeNodeLabel(cylinderLabel || boxLabel);
      return `${nodeId}["${label}"]`;
    });
}

function normalizeMermaidEdgeLabel(line: string): string {
  return line.replace(
    /^(\s*)([A-Za-z][A-Za-z0-9_]*)\s*(-->|---|-.->|==>)\s*([A-Za-z][A-Za-z0-9_]*)\s*:\s*(.+?)\s*$/,
    (_, indent: string, from: string, edge: string, to: string, label: string) =>
      `${indent}${from} ${edge}|${sanitizeEdgeLabel(label)}| ${to}`);
}

/** Mermaid 11 is flaky with `A -- "text" --> B`; normalize to `A -->|text| B`. */
function normalizeLegacyQuotedTextEdges(line: string): string {
  const doubleQuoted = line.replace(
    /^(\s*)([A-Za-z][A-Za-z0-9_]*)\s*--\s*"([^"]*)"\s*-->\s*([A-Za-z][A-Za-z0-9_]*)\s*$/,
    (_, indent: string, from: string, text: string, to: string) =>
      `${indent}${from} -->|${sanitizeEdgeLabel(text)}| ${to}`
  );
  return doubleQuoted.replace(
    /^(\s*)([A-Za-z][A-Za-z0-9_]*)\s*--\s*'([^']*)'\s*-->\s*([A-Za-z][A-Za-z0-9_]*)\s*$/,
    (_, indent: string, from: string, text: string, to: string) =>
      `${indent}${from} -->|${sanitizeEdgeLabel(text)}| ${to}`
  );
}

function sanitizeEdgeLabel(label: string): string {
  return label
    .replace(/\|/g, '/')
    .replace(/"/g, "'")
    .trim();
}

function sanitizeNodeLabel(label: string): string {
  let s = label.trim();
  if (s.length >= 2 && ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'")))) {
    s = s.slice(1, -1).trim();
  }
  s = s.replace(/`/g, '');
  s = stripBogusParenQuoteWrappers(s);
  return s
    .replace(/\[\]/g, '()')
    .replace(/\[/g, '(')
    .replace(/\]/g, ')')
    .replace(/"/g, "'")
    .trim();
}

function stripBogusParenQuoteWrappers(value: string): string {
  let s = value.trim();
  while (s.length >= 4 && s.startsWith("('") && s.endsWith("')")) {
    s = s.slice(2, -2).trim();
  }
  while (s.length >= 4 && s.startsWith('("') && s.endsWith('")')) {
    s = s.slice(2, -2).trim();
  }
  if (s.length >= 3 && s.startsWith("('") && s.endsWith("'") && !s.endsWith("')")) {
    s = s.slice(2, -1).trim();
  }
  if (s.length >= 3 && s.startsWith('("') && s.endsWith('"') && !s.endsWith('")')) {
    s = s.slice(2, -1).trim();
  }
  return s.trim();
}
