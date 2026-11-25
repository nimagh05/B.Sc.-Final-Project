import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { SourceCitationDto } from '../api/types';
import { CitationText } from '../components/CitationText';

const sampleSources: SourceCitationDto[] = [
  {
    index: 1,
    documentId: 'doc-1',
    fileName: 'aein-91.pdf',
    page: 12,
    sectionTitle: 'ماده ۱۲',
    snippet: 'حداکثر واحد مجاز ۲۰ واحد است.',
    score: 0.89,
  },
  {
    index: 2,
    documentId: 'doc-1',
    fileName: 'aein-91.pdf',
    page: 13,
    sectionTitle: 'تبصره ۱',
    snippet: 'دانشجوی ممتاز تا ۲۴ واحد مجاز است.',
    score: 0.84,
  },
];

describe('CitationText', () => {
  it('renders [1], [1][2], [1, 2], and [1، 2] as clickable buttons while keeping unmatched brackets as text', () => {
    const onSelect = vi.fn();

    render(
      <CitationText
        text={'سقف واحد ۲۰ واحد است [1]. در شرایط خاص [1, 2] یا [1، 2] و همچنین [1][2]؛ مورد نامعتبر [99] و فارسی [۱].'}
        sources={sampleSources}
        activeSourceIndex={1}
        onSelectSource={onSelect}
      />,
    );

    const source1Buttons = screen.getAllByRole('button', { name: /منبع شماره 1/ });
    const source2Buttons = screen.getAllByRole('button', { name: /منبع شماره 2/ });
    expect(source1Buttons.length).toBe(4);
    expect(source2Buttons.length).toBe(3);
    expect(source1Buttons[0]).toHaveAttribute('aria-pressed', 'true');
    expect(source2Buttons[0]).toHaveAttribute('aria-pressed', 'false');

    expect(screen.queryByRole('button', { name: /99/ })).not.toBeInTheDocument();
    expect(screen.getByText(/\[99\]/)).toBeInTheDocument();
    expect(screen.getByText(/\[۱\]/)).toBeInTheDocument();

    fireEvent.click(source2Buttons[0]);
    expect(onSelect).toHaveBeenCalledWith(2);
  });

  it('escapes HTML/XSS payloads as plain text with zero injected DOM tags', () => {
    const { container } = render(
      <CitationText
        text={'متن آزمایشی <script>alert(1)</script> و <img src=x onerror=alert(2)> [1]'}
        sources={sampleSources}
        activeSourceIndex={null}
        onSelectSource={vi.fn()}
      />,
    );

    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByText(/<script>alert\(1\)<\/script>/)).toBeInTheDocument();
  });

  it('splits multi-line answers into paragraphs with dir="auto"', () => {
    const { container } = render(
      <CitationText
        text={'پاراگراف اول [1]\nپاراگراف دوم درباره JCR [2]'}
        sources={sampleSources}
        activeSourceIndex={null}
        onSelectSource={vi.fn()}
      />,
    );

    const paragraphs = container.querySelectorAll('p[dir="auto"]');
    expect(paragraphs).toHaveLength(2);
  });
});
