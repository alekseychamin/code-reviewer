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
        const result = await mermaid.render(`diagram-${id}`, chart);
        if (!active) {
          return;
        }

        setSvg(result.svg);
        setError(null);
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
        <p>Could not render Mermaid diagram.</p>
        <pre>{chart}</pre>
      </div>
    );
  }

  if (!svg) {
    return <div className="empty-state compact">Rendering diagram...</div>;
  }

  return <div className="mermaid-diagram" dangerouslySetInnerHTML={{ __html: svg }} />;
}
